using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查集合边界：Where 后没判空就 First/FirstOrDefault 之外的 Single、
/// 索引访问代替 TryGet、以及 Contains 用在了大集合上（O(n) 退化成 O(n²)）。</summary>
public sealed class CollectionEdgeReportTool : ITool
{
    public string Name => "collection_edge_report";
    public string Description =>
        "只读检查集合边界：空集合上取 First/Single、索引访问代替 TryGet/List.Contains 用在 List 上（O(n²)）。";

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
        var unsafeTake = new List<string>();
        var indexInsteadOfTry = new List<string>();
        var containsOnList = new List<string>();
        var files = 0;
        var listVars = new HashSet<string>(StringComparer.Ordinal);
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
            var text = string.Join("\n", lines);
            foreach (Match l in Regex.Matches(text, @"\bList<[^>]+>\s+(?<n>\w+)\s*="))
                listVars.Add(l.Groups["n"].Value);
            foreach (Match l in Regex.Matches(text, @"\bvar\s+(?<n>\w+)\s*=\s*new\s+List<"))
                listVars.Add(l.Groups["n"].Value);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;
                // 空集合上取 First/Single/Max 之前没有判空
                var take = Regex.Match(trimmed, @"\.(?<m>First|Single|Last)\s*\(");
                if (take.Success)
                {
                    var before = string.Join("\n", lines.Skip(Math.Max(0, i - 5)).Take(Math.Min(6, i + 1)));
                    if (!Regex.IsMatch(before, @"(?i)(Count\s*[!<>]=?|Any\s*\(|Length\s*[!<>]=?|IsNullOrEmpty|try\s)"))
                        unsafeTake.Add($"{relative}:{lineNo} 直接 .{take.Groups["m"].Value}()，前面没有判空（空集合会抛 InvalidOperationException）");
                }
                var agg = Regex.Match(trimmed, @"\.(?<m>Max|Min|Sum|Average)\s*\(");
                if (agg.Success)
                {
                    var before = string.Join("\n", lines.Skip(Math.Max(0, i - 5)).Take(Math.Min(6, i + 1)));
                    if (!Regex.IsMatch(before, @"(?i)(Count\s*[!<>]=?|Any\s*\(|Length\s*[!<>]=?|IsNullOrEmpty|try\s)"))
                        unsafeTake.Add($"{relative}:{lineNo} 直接 .{agg.Groups["m"].Value}()，前面没有判空（空序列会抛异常）");
                }
                // 索引访问代替 TryGetValue
                var idx = Regex.Match(trimmed, @"\b(?<d>\w+)\s*\[\s*(?<k>\w+)\s*\]");
                if (idx.Success && Regex.IsMatch(trimmed, @"\bTryGetValue\s*\(|\.Keys\s*\.|\bContainsKey\s*\("))
                    continue;
                if (idx.Success && Regex.IsMatch(text, @"(?s)Dictionary<[^>]+>\s+" + Regex.Escape(idx.Groups["d"].Value) + @"\b"))
                    indexInsteadOfTry.Add($"{relative}:{lineNo} 用 {idx.Groups["d"].Value}[{idx.Groups["k"].Value}] 访问字典（键不存在时抛 KeyNotFoundException，应 TryGetValue）");
                // List.Contains 出现在循环体里 → O(n²)
                var recv = Regex.Match(trimmed, @"\b(?<d>\w+)\s*\.\s*Contains\s*\(");
                if (recv.Success && listVars.Contains(recv.Groups["d"].Value))
                {
                    var before = string.Join("\n", lines.Skip(Math.Max(0, i - 8)).Take(Math.Min(9, i + 1)));
                    if (Regex.IsMatch(before, @"(?i)\b(?:for|foreach|while)\b"))
                        containsOnList.Add($"{relative}:{lineNo} 在循环里对 List 用 .Contains(...)（O(n²)；应先转 HashSet 或建索引）");
                }
            }
        }
        if (files == 0)
            return "集合边界报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"集合边界报告: {files} 个 .cs 文件");
        Report(output, "未判空就取首/聚合", unsafeTake, maxResults);
        Report(output, "索引访问代替 TryGetValue", indexInsteadOfTry, maxResults);
        Report(output, "循环里的 List.Contains", containsOnList, maxResults);
        if (unsafeTake.Count == 0 && indexInsteadOfTry.Count == 0 && containsOnList.Count == 0)
            output.AppendLine("未发现集合边界问题");
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
