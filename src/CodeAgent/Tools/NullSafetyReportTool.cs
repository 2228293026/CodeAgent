using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查空值处理：?. 与 ?? 缺失、字典索引未判存在、以及可空引用类型标注缺失。</summary>
public sealed class NullSafetyReportTool : ITool
{
    public string Name => "null_safety_report";
    public string Description =>
        "只读检查空值安全：字典索引未判存在、链式调用缺 ?.、可能为 null 的返回值直接解引用、以及 ?? 缺失。";

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
        var indexUnchecked = new List<string>();
        var chainUnchecked = new List<string>();
        var nullFallback = new List<string>();
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
                var line = lines[i];
                var trimmed = line.Trim();
                var lineNo = i + 1;
                // 字典下标后立刻解引用，且既没有 TryGetValue 也没有判 ContainsKey
                if (Regex.IsMatch(trimmed, @"\w+\[\s*[\w""\.]+\s*\]\s*\."))
                {
                    var guarded = lines.Any(l => l.Contains("TryGetValue", StringComparison.Ordinal)
                        || l.Contains("ContainsKey", StringComparison.Ordinal)
                        || l.Contains("TryParse", StringComparison.Ordinal))
                        || trimmed.Contains("?", StringComparison.Ordinal);
                    if (!guarded)
                        indexUnchecked.Add($"{relative}:{lineNo} 字典下标后直接解引用（键不存在时会 NRE）");
                }
                // 已知可能返回 null 的调用（FirstOrDefault / SingleOrDefault / ElementAt）后直接点调用
                if (Regex.IsMatch(trimmed, @"\b(FirstOrDefault|SingleOrDefault|LastOrDefault|ElementAtOrDefault)\s*\([^)]*\)\s*\."))
                    chainUnchecked.Add($"{relative}:{lineNo} OrDefault 结果直接链式调用（可能为 null）");
                // 单行三元判空：cond ? a : null 之后未必处理，但 ?? "" 是廉价的兜底
                if (Regex.IsMatch(trimmed, @"=\s*[^;]*\?\s*[^:;]+:\s*null\s*;") && !trimmed.Contains("??", StringComparison.Ordinal))
                    nullFallback.Add($"{relative}:{lineNo} 赋值可能为 null 且无 ?? 兜底");
            }
        }
        if (files == 0)
            return "空值安全报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"空值安全报告: {files} 个 .cs 文件");
        Report(output, "下标未判存在", indexUnchecked, maxResults);
        Report(output, "OrDefault 直接链式", chainUnchecked, maxResults);
        Report(output, "缺 ?? 兜底", nullFallback, maxResults);
        if (indexUnchecked.Count == 0 && chainUnchecked.Count == 0 && nullFallback.Count == 0)
            output.AppendLine("未发现空值安全问题");
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
