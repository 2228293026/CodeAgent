using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查数组/列表的越界：arr[arr.Length] 这类 off-by-one、
/// 没有下界保护的 [i-1]、以及固定下标访问可能为空的 Split()/ElementAt 结果。</summary>
public sealed class ArrayBoundsReportTool : ITool
{
    public string Name => "array_bounds_report";
    public string Description =>
        "只读检查数组越界：arr[arr.Length] off-by-one、没有下界保护的 [i-1]、对 Split()/ElementAt() 结果直接取 [0]。";

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
        var offByOne = new List<string>();
        var noLowerGuard = new List<string>();
        var indexOnMaybeEmpty = new List<string>();
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
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;
                var before = string.Join("\n", lines.Skip(Math.Max(0, i - 4)).Take(Math.Min(5, i + 1)));
                // arr[arr.Length] / list[list.Count] —— 永远越界
                var len = Regex.Match(trimmed, @"\b(?<c>\w+)\s*\[\s*(?<a>\w+)\s*\.\s*(?<p>Length|Count)\s*\]");
                if (len.Success)
                {
                    if (len.Groups["c"].Value == len.Groups["a"].Value)
                        offByOne.Add($"{relative}:{lineNo} {len.Value} —— 索引等于长度，永远越界（off-by-one；最后一次有效索引是 Length-1）");
                    else if (!Regex.IsMatch(before, @"<\s*"))
                        offByOne.Add($"{relative}:{lineNo} {len.Value} —— 用 .Count/.Length 当上界，前面又没有 < 判断（Count 是有效长度不是上界）");
                }
                // arr[i - 1] 没有下界保护
                var down = Regex.Match(trimmed, @"\b(?<c>\w+)\s*\[\s*(?<i>\w+)\s*-\s*(?<n>\d+)\s*\]");
                if (down.Success && !Regex.IsMatch(before, @"(?i)\bif\b|>\s*0|>\s*=\s*1|Math\.Max|checked|IsFromZero"))
                    noLowerGuard.Add($"{relative}:{lineNo} {down.Value} —— 减法索引没有下界保护（{down.Groups["i"].Value} 为 0 时会抛 IndexOutOfRangeException）");
                // Split(...)[0] / ElementAt(0)[0]
                var chained = Regex.Match(trimmed, @"\b(?<m>Split|ElementAt|First|ToArray)\s*\([^)]*\)\s*\[\s*(?<idx>\d+)\s*\]");
                if (chained.Success)
                    indexOnMaybeEmpty.Add($"{relative}:{lineNo} 直接取 {chained.Groups["m"].Value}(…)[{chained.Groups["idx"].Value}]（结果可能为空；应先判长度）");
            }
        }
        if (files == 0)
            return "数组越界报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"数组越界报告: {files} 个 .cs 文件");
        Report(output, "off-by-one 上界", offByOne, maxResults);
        Report(output, "减法索引无下界保护", noLowerGuard, maxResults);
        Report(output, "可能为空的结果直接取下标", indexOnMaybeEmpty, maxResults);
        if (offByOne.Count == 0 && noLowerGuard.Count == 0 && indexOnMaybeEmpty.Count == 0)
            output.AppendLine("未发现数组越界问题");
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
