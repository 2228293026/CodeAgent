using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查可测试性：方法太长无法单测、private 方法里混着可测逻辑、
/// 以及同一个分支被多个条件重复覆盖（边界没测全）。</summary>
public sealed class TestabilityReportTool : ITool
{
    public string Name => "testability_report";
    public string Description =>
        "只读检查可测性：方法过长难以单测、private 方法里藏着可独立测试的逻辑、条件分支成对出现时只测了一侧。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示每类问题数（默认 30，最大 300）" },
            ["max_depth"] = new JsonObject { ["type"] = "integer", ["description"] = "扫描深度上限（默认 10，最大 32）" },
        },
    };

    /// <summary>超过这么多行的方法通常已经超出"一个方法只做一件事"的尺度。</summary>
    internal const int LongMethodLines = 60;

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 30), 1, 300);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        var longMethods = new List<string>();
        var privateLogic = new List<string>();
        var unpairedBranches = new List<string>();
        var files = 0;
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;
            files++;
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var hasIf = false;
            var hasElse = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;
                if (Regex.IsMatch(trimmed, @"^\s*(?:if|else if)\s*\(")) hasIf = true;
                if (Regex.IsMatch(trimmed, @"^\s*else\b")) hasElse = true;

                var sig = Regex.Match(trimmed, @"(?<acc>(?:public|private|protected|internal|static|virtual|override|sealed|async|abstract|partial)\s+)+(?<ret>[\w\.<>\[\],\?]+)\s+(?<n>\w+)\s*\([^)]*\)");
                if (!sig.Success) continue;
                var isPrivate = sig.Value.Contains("private", StringComparison.Ordinal);
                // 找方法体范围
                var depth = 0;
                var started = false;
                var end = i;
                for (var j = i + 1; j < lines.Length && j < i + 400; j++)
                {
                    foreach (var ch in lines[j])
                    {
                        if (ch == '{') { depth++; started = true; }
                        else if (ch == '}') depth--;
                    }
                    end = j;
                    if (started && depth <= 0) break;
                }
                if (!started)
                {
                    // 表达式体方法：单行
                    continue;
                }
                var bodyLines = end - i;
                var name = sig.Groups["n"].Value;
                if (bodyLines >= LongMethodLines)
                    longMethods.Add($"{relative}:{lineNo} {name}() 有 {bodyLines} 行——单测里要准备 {bodyLines} 行前置状态");
                if (isPrivate && bodyLines >= 25 && !trimmed.Contains("override", StringComparison.Ordinal))
                    privateLogic.Add($"{relative}:{lineNo} private {name}() 有 {bodyLines} 行，外部测不到（考虑抽成可注入依赖的内部方法）");
            }
            // 整个文件里有 if 却没有 else（或反过来）：有一侧从未被考虑
            if (hasIf && !hasElse)
                unpairedBranches.Add($"{relative} 有 if 但没有 else——否定分支从未被显式处理过（新增条件时容易漏）");
            if (hasElse && !hasIf)
                unpairedBranches.Add($"{relative} 有 else 但没有 if");
        }
        if (files == 0)
            return "可测性报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"可测性报告: {files} 个 .cs 文件（长方法阈值 {LongMethodLines} 行）");
        Report(output, "过长的方法", longMethods, maxResults);
        Report(output, "private 里藏着长逻辑", privateLogic, maxResults);
        Report(output, "分支不成对", unpairedBranches, maxResults);
        if (longMethods.Count == 0 && privateLogic.Count == 0 && unpairedBranches.Count == 0)
            output.AppendLine("未发现可测性问题");
        return output.ToString().TrimEnd();
    }

    private static void Report(StringBuilder output, string title, List<string> items, int maxResults)
    {
        if (items.Count == 0)
            return;
        output.AppendLine($"{title}: {items.Count}");
        foreach (var item in items.OrderBy(x => x, StringComparer.Ordinal).Take(maxResults))
            output.AppendLine($"  {item}");
        if (items.Count > maxResults)
            output.AppendLine($"  …（另有 {items.Count - maxResults} 处未显示）");
    }
}
