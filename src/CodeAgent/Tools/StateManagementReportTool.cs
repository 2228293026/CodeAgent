using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查状态管理：单例共享可变状态、事件未解绑、以及依赖注入绕过多。</summary>
public sealed class StateManagementReportTool : ITool
{
    public string Name => "state_management_report";
    public string Description =>
        "只读检查状态管理：单例持有可变集合、跨层直接修改内部集合（缺少只读视图）、以及 Reset 遗漏字段。";

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

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 30), 1, 300);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        var singletonState = new List<string>();
        var mutableExposure = new List<string>();
        var resetIncomplete = new List<string>();
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
                var lineNo = i + 1;
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                // 单例持有可变集合：进程内共享，测试之间会互相污染
                if (Regex.IsMatch(trimmed, @"\bstatic\s+(readonly\s+)?[\w.<>]+\s+Instance\b")
                    && lines.Skip(i).Take(8).Any(l => Regex.IsMatch(l, @"\b(?:[\w.]+\.)?(Dictionary|List|HashSet|Queue|ConcurrentDictionary)<")))
                    singletonState.Add($"{relative}:{lineNo} 单例持有可变集合（跨调用/跨测试共享状态）");
                // 只读属性却暴露可变集合：调用方可以直接改内部状态（类型名可能带命名空间限定）
                if (Regex.IsMatch(trimmed, @"\bpublic\s+(?:[\w.]+\.)?(Dictionary|List|HashSet|Queue|ConcurrentDictionary)<[^>]*>\s+\w+\s*=>\s*_\w+\s*;"))
                    mutableExposure.Add($"{relative}:{lineNo} 公开属性直接返回可变集合（应返回只读视图）");
                // Reset 只清了部分字段
                if (Regex.IsMatch(trimmed, @"\bvoid\s+Reset\s*\("))
                {
                    var fieldCount = lines.Count(l => Regex.IsMatch(l, @"^\s*private\s+.*\s_\w+\s*(=|;|=>)"));
                    var body = string.Join("\n", lines.Skip(i).Take(20));
                    var cleared = Regex.Matches(body, @"_\w+\s*=").Count;
                    if (fieldCount >= 3 && cleared < fieldCount / 2)
                        resetIncomplete.Add($"{relative}:{lineNo} Reset 未覆盖多数字段（{fieldCount} 个字段只清了 {cleared} 个）");
                }
            }
        }
        if (files == 0)
            return "状态管理报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"状态管理报告: {files} 个 .cs 文件");
        Report(output, "单例持有可变集合", singletonState, maxResults);
        Report(output, "暴露可变集合", mutableExposure, maxResults);
        Report(output, "Reset 覆盖不全", resetIncomplete, maxResults);
        if (singletonState.Count == 0 && mutableExposure.Count == 0 && resetIncomplete.Count == 0)
            output.AppendLine("未发现状态管理问题");
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
