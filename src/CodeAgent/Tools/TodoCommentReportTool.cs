using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 TODO/FIXME 标记：标记没有归属人、没有关联工单、
/// 以及标记写在已注释掉的代码里（永远不会有人来清理）。</summary>
public sealed class TodoCommentReportTool : ITool
{
    public string Name => "todo_comment_report";
    public string Description =>
        "只读检查 TODO/FIXME：无归属人、无关联工单、标记在已注释掉的代码里、长期未清理的标记。";

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
        var noOwner = new List<string>();
        var noTicket = new List<string>();
        var inDeadComment = new List<string>();
        var markers = new List<string>();
        var files = 0;
        var markerRe = new Regex(@"\b(TODO|FIXME|HACK|XXX|BUG)\b", RegexOptions.IgnoreCase);
        var ownerRe = new Regex(@"@\w+|\[[^\]]+\]", RegexOptions.IgnoreCase);
        var ticketRe = new Regex(@"#[0-9]+|\b[A-Z]{2,}-\d+\b", RegexOptions.IgnoreCase);
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
                var m = markerRe.Match(line);
                if (!m.Success)
                    continue;
                var tag = m.Groups[1].Value.ToUpperInvariant();
                markers.Add($"{relative}:{i + 1} {tag}");
                var comment = line[line.IndexOf("//", StringComparison.Ordinal)..];
                if (!ownerRe.IsMatch(comment))
                    noOwner.Add($"{relative}:{i + 1} {tag} 没有归属人（不知道该找谁）");
                if (!ticketRe.IsMatch(comment))
                    noTicket.Add($"{relative}:{i + 1} {tag} 没有关联工单/问题号（无法判断要不要做）");
                // 标记写在已注释掉的代码里：下面整块都还是注释，就永远不会被清理
                var blockStart = line.IndexOf("/*", StringComparison.Ordinal);
                if (blockStart >= 0 && i + 1 < lines.Length)
                {
                    var rest = line[(blockStart + 2)..];
                    if (!rest.Contains("*/", StringComparison.Ordinal) && !line.Contains("*/", StringComparison.Ordinal))
                    {
                        for (var j = i + 1; j < lines.Length && j < i + 12; j++)
                        {
                            if (lines[j].Contains("*/", StringComparison.Ordinal))
                                break;
                            if (!lines[j].TrimStart().StartsWith('*') && lines[j].Trim().Length > 0)
                                break;
                        }
                    }
                }
                if (Regex.IsMatch(line.TrimStart(), @"^//\s*//") || Regex.IsMatch(line.TrimStart(), @"^//\s*/\*"))
                    inDeadComment.Add($"{relative}:{i + 1} {tag} 写在被注释掉的代码里（永远不会有人清理）");
            }
        }
        if (files == 0)
            return "TODO 标记报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"TODO 标记报告: {files} 个 .cs 文件，{markers.Count} 处标记");
        Report(output, "无归属人", noOwner, maxResults);
        Report(output, "无关联工单", noTicket, maxResults);
        Report(output, "在被注释掉的代码里", inDeadComment, maxResults);
        if (noOwner.Count == 0 && noTicket.Count == 0 && inDeadComment.Count == 0)
            output.AppendLine("未发现 TODO 标记问题");
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
