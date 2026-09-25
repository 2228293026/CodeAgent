using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读扫描 TODO/FIXME/XXX/HACK 等待办标记，按标签汇总并给出文件行号。</summary>
public sealed class TodoMarkerReportTool : ITool
{
    private static readonly (string Tag, string Pattern)[] Markers =
    {
        ("TODO", @"\bTODO\b"),
        ("FIXME", @"\bFIXME\b"),
        ("XXX", @"\bXXX\b"),
        ("HACK", @"\bHACK\b"),
        ("BUG", @"\bBUG\b"),
        ("NOTE", @"\bNOTE\b"),
        ("DEPRECATED", @"\bDEPRECATED\b"),
    };

    public string Name => "todo_marker_report";
    public string Description =>
        "只读扫描代码中的 TODO/FIXME/XXX/HACK/BUG/NOTE/DEPRECATED 标记，按标签统计并列出文件行号。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["tags"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "只统计指定标签，默认全部",
            },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示条目数（默认 100，最大 2000）" },
            ["max_depth"] = new JsonObject { ["type"] = "integer", ["description"] = "扫描深度上限（默认 8，最大 32）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 100), 1, 2_000);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 8), 1, 32);
        var wanted = ToolArgs.GetStringList(args, "tags") ?? new List<string>();
        var selected = wanted.Count == 0
            ? Markers
            : Markers.Where(m => wanted.Any(tag => string.Equals(tag, m.Tag, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (selected.Length == 0)
            throw new ToolException("未识别的标签（可用: " + string.Join(", ", Markers.Select(m => m.Tag)) + "）");
        var counts = selected.ToDictionary(m => m.Tag, _ => 0, StringComparer.OrdinalIgnoreCase);
        var hits = new List<(string Tag, string File, int Line, string Snippet)>();
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsTextCandidate(file))
                continue;
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var marker in selected)
                {
                    if (!Regex.IsMatch(lines[i], marker.Pattern))
                        continue;
                    counts[marker.Tag]++;
                    hits.Add((marker.Tag, Path.GetRelativePath(root, file).Replace('\\', '/'), i + 1, lines[i].Trim()));
                    break; // 同一行只归入第一个命中的标签
                }
            }
        }
        var total = counts.Values.Sum();
        var output = new StringBuilder();
        output.AppendLine($"待办标记报告: {total:N0} 处");
        foreach (var pair in counts.Where(pair => pair.Value > 0).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            output.AppendLine($"  {pair.Key}: {pair.Value:N0}");
        if (total == 0)
            return output.ToString().TrimEnd();
        output.AppendLine("明细:");
        foreach (var hit in hits.OrderBy(hit => hit.Tag, StringComparer.OrdinalIgnoreCase).ThenBy(hit => hit.File, StringComparer.Ordinal).ThenBy(hit => hit.Line).Take(maxResults))
            output.AppendLine($"  [{hit.Tag}] {hit.File}:{hit.Line} {hit.Snippet}");
        if (total > maxResults)
            output.AppendLine($"…（另有 {total - maxResults} 处未显示）");
        return output.ToString().TrimEnd();
    }

    private static bool IsTextCandidate(string file)
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        return ext is ".cs" or ".md" or ".json" or ".yml" or ".yaml" or ".txt" or ".sh" or ".ps1" or ".cmd" or ".props" or ".targets";
    }
}
