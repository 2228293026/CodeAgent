using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查空值处理：连续的空值检查应收敛为模式匹配、
/// 对可能为 null 的引用直接解引用、以及 `??` 链里对可空值类型的重复比较。</summary>
public sealed class NullCoalescingReportTool : ITool
{
    public string Name => "null_coalescing_report";
    public string Description =>
        "只读检查空值处理：连续的 null 检查应收敛为模式匹配、对可空引用直接解引用、以及过长的 ?? 链。";

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
        var coalesceChain = new List<string>();
        var consecutiveNullCheck = new List<string>();
        var uncheckedDereference = new List<string>();
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
                // 过长的 ?? 链：三层以上应收敛为模式匹配或辅助方法
                if (Regex.Matches(trimmed, @"\?\?").Count >= 3)
                    coalesceChain.Add($"{relative}:{lineNo} ?? 链有 {Regex.Matches(trimmed, @"\?\?").Count} 层（应收敛为模式匹配）");
                // 判空后仍反复解引用：建议用模式匹配一次取值
                if (Regex.IsMatch(trimmed, @"\bis\s+null\b"))
                {
                    var varName = Regex.Match(trimmed, @"\b(\w+)\s+is\s+null").Groups[1].Value;
                    if (varName.Length > 0)
                    {
                        var body = string.Join("\n", lines.Skip(i).Take(8));
                        var dereferences = Regex.Matches(body, $@"\b{Regex.Escape(varName)}\s*(\.|\[)").Count;
                        var guarded = body.Contains("is not null", StringComparison.Ordinal);
                        if (dereferences >= 2 && !guarded)
                            uncheckedDereference.Add($"{relative}:{lineNo} {varName} 判空后仍反复解引用（建议用模式匹配一次取值）");
                    }
                }
                // if/else if 连续判空同一变量
                if (Regex.IsMatch(trimmed, @"\belse\s+if\s*\(") && Regex.IsMatch(trimmed, @"(==|!=)\s*null"))
                {
                    var window = string.Join("\n", lines.Skip(Math.Max(0, i - 2)).Take(3));
                    if (Regex.Matches(window, @"!=\s*null").Count + Regex.Matches(window, @"==\s*null").Count >= 3)
                        consecutiveNullCheck.Add($"{relative}:{lineNo} if/else if 连续判空（应收敛为模式匹配）");
                }
            }
        }
        if (files == 0)
            return "空值处理报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"空值处理报告: {files} 个 .cs 文件");
        Report(output, "过长 ?? 链", coalesceChain, maxResults);
        Report(output, "连续判空", consecutiveNullCheck, maxResults);
        Report(output, "判空后反复解引用", uncheckedDereference, maxResults);
        if (coalesceChain.Count == 0 && consecutiveNullCheck.Count == 0 && uncheckedDereference.Count == 0)
            output.AppendLine("未发现空值处理问题");
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
