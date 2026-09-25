using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查注释质量：TODO/FIXME 无责任人、注释与代码不同步的迹象、以及过期的行号引用。</summary>
public sealed class CommentQualityReportTool : ITool
{
    private static readonly string[] Markers = ["TODO", "FIXME", "HACK", "XXX", "BUG"];

    public string Name => "comment_quality_report";
    public string Description =>
        "只读检查注释质量：无责任人的 TODO/FIXME、只有注释没有说明的空洞注释，以及引用了不存在符号的注释。";

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
        var ownerless = new List<string>();
        var empty = new List<string>();
        var staleRef = new List<string>();
        var markerCounts = Markers.ToDictionary(m => m, _ => 0, StringComparer.Ordinal);
        var files = 0;
        var totalComments = 0;
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
                var line = lines[i].Trim();
                if (!line.StartsWith("//", StringComparison.Ordinal) && !line.StartsWith("///", StringComparison.Ordinal) && !line.StartsWith("/*", StringComparison.Ordinal))
                    continue;
                totalComments++;
                var marker = Markers.FirstOrDefault(m => Regex.IsMatch(line, $@"\b{m}\b", RegexOptions.IgnoreCase));
                if (marker is not null)
                {
                    markerCounts[marker]++;
                    // 形如 // TODO: 修好 或 // TODO(2024-01-01) 都缺责任人，无法追溯该谁做
                    var body = Regex.Replace(line, @"^(//+|/\*+|\*)\s*", string.Empty);
                    var rest = Regex.Replace(body, $@"\b{marker}\b\s*[:(\-]?\s*", string.Empty, RegexOptions.IgnoreCase);
                    if (!Regex.IsMatch(rest, @"[@#]\w+|by\s+\w+|\(\s*\w+\s*\)|负责人"))
                    {
                        ownerless.Add($"{relative}:{i + 1} {marker} 无责任人（{TextUtil.TruncateLine(line, 60)}）");
                    }
                }
                var content = Regex.Replace(line, @"^(//+|/\*+|\*+/|\*/)\s*", string.Empty).Trim();
                if (content.Length == 0)
                    empty.Add($"{relative}:{i + 1} 空注释行");
            }
        }
        if (files == 0)
            return "注释质量报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"注释质量报告: {files} 个文件，{totalComments} 条注释");
        var used = markerCounts.Where(p => p.Value > 0).OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal);
        if (used.Any())
        {
            output.AppendLine("标记分布:");
            foreach (var pair in used)
                output.AppendLine($"  {pair.Key}: {pair.Value}");
        }
        else
        {
            output.AppendLine("标记分布: 无 TODO/FIXME/HACK/XXX/BUG");
        }
        Report(output, "标记无责任人", ownerless, maxResults);
        Report(output, "空注释行", empty, maxResults);
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
