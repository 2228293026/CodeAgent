using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查超大方法：方法体行数过多、嵌套层级过深、参数过多。</summary>
public sealed class LargeMethodReportTool : ITool
{
    public string Name => "large_method_report";
    public string Description =>
        "只读检查超大方法：方法体过长、嵌套过深、参数过多、圈复杂度信号（分支数过多）。";

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

    /// <summary>方法体行数上限。超过就该拆了。</summary>
    internal const int MaxMethodLines = 80;

    /// <summary>嵌套层级上限（相对方法体第一层）。</summary>
    internal const int MaxNesting = 4;

    /// <summary>参数个数上限。超过就该传对象了。</summary>
    internal const int MaxParameters = 5;

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 30), 1, 300);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        var tooLong = new List<string>();
        var tooDeep = new List<string>();
        var tooManyParams = new List<string>();
        var tooManyBranches = new List<string>();
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
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                var m = Regex.Match(trimmed, @"^(?<mods>(?:public|private|protected|internal|static|virtual|override|abstract|sealed|async|extern|new|partial|\s)*)(?<ret>[\w\.<>\[\],\?\s]+?)\s+(?<name>\w+)\s*\((?<args>[^)]*)\)");
                if (!m.Success)
                    continue;
                // 跳过控制流与调用（调用里也可能有括号）
                if (Regex.IsMatch(trimmed, @"\b(if|for|foreach|while|switch|catch|using|lock|return|new|typeof|nameof)\b"))
                    continue;
                var name = m.Groups["name"].Value;
                var parameters = m.Groups["args"].Value;
                var paramCount = parameters.Trim().Length == 0
                    ? 0
                    : parameters.Split(',').Length;
                if (paramCount > MaxParameters)
                    tooManyParams.Add($"{relative}:{i + 1} {name} 有 {paramCount} 个参数（应传对象）");
                var body = MethodBody(lines, i, trimmed);
                if (body.Count == 0)
                    continue;
                if (body.Count > MaxMethodLines)
                    tooLong.Add($"{relative}:{i + 1} {name} 方法体 {body.Count} 行（上限 {MaxMethodLines}）");
                var deepest = MaxDepthOf(body);
                if (deepest > MaxNesting)
                    tooDeep.Add($"{relative}:{i + 1} {name} 嵌套 {deepest} 层（上限 {MaxNesting}）");
                var branches = body.Count(l => Regex.IsMatch(l, @"\b(if|else if|case|catch)\b"));
                if (branches > 12)
                    tooManyBranches.Add($"{relative}:{i + 1} {name} 有 {branches} 个分支（可读性差，改用多态或查表）");
            }
        }
        if (files == 0)
            return "超大方法报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"超大方法报告: {files} 个 .cs 文件（阈值 {MaxMethodLines} 行 / {MaxNesting} 层 / {MaxParameters} 参数）");
        Report(output, "方法体过长", tooLong, maxResults);
        Report(output, "嵌套过深", tooDeep, maxResults);
        Report(output, "参数过多", tooManyParams, maxResults);
        Report(output, "分支过多", tooManyBranches, maxResults);
        if (tooLong.Count == 0 && tooDeep.Count == 0 && tooManyParams.Count == 0 && tooManyBranches.Count == 0)
            output.AppendLine("未发现超大方法");
        return output.ToString().TrimEnd();
    }

    /// <summary>方法体：表达式体取本行，块体按大括号配平。返回体内各行。</summary>
    private static List<string> MethodBody(string[] lines, int declLine, string decl)
    {
        if (decl.Contains("=>", StringComparison.Ordinal))
            return new List<string> { decl };
        var depth = 0;
        var started = false;
        for (var i = declLine; i < lines.Length && i < declLine + 800; i++)
        {
            foreach (var ch in lines[i])
            {
                if (ch == '{') { depth++; started = true; }
                else if (ch == '}') depth--;
            }
            if (started && depth <= 0)
                return lines.Skip(declLine).Take(i - declLine + 1).ToList();
        }
        return started ? lines.Skip(declLine).ToList() : new List<string>();
    }

    private static int MaxDepthOf(List<string> body)
    {
        var depth = 0;
        var deepest = 0;
        foreach (var line in body)
        {
            foreach (var ch in line)
            {
                if (ch == '{') { depth++; if (depth > deepest) deepest = depth; }
                else if (ch == '}') depth--;
            }
        }
        return deepest - 1; // 方法自身的大括号不算嵌套
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
