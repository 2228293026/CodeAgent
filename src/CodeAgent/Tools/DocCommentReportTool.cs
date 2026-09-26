using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 XML 文档注释：公开成员缺 &lt;summary&gt;、标签未闭合、
/// 文档注释写在非文档位置，以及 summary 为空。</summary>
public sealed class DocCommentReportTool : ITool
{
    public string Name => "doc_comment_report";
    public string Description =>
        "只读检查 XML 文档注释：公开成员缺 <summary>、标签未闭合、summary 为空、文档注释紧跟非文档成员。";

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
        var missingSummary = new List<string>();
        var unclosedTag = new List<string>();
        var emptySummary = new List<string>();
        var orphanDoc = new List<string>();
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
            var inDoc = false;
            var docStart = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("///", StringComparison.Ordinal))
                {
                    if (!inDoc)
                    {
                        inDoc = true;
                        docStart = i + 1;
                    }
                    // 未闭合标签：<summary> 没有对应的 </summary>
                    var opens = Regex.Matches(trimmed, @"<(?!\/)([a-zA-Z]+)(?:\s[^>]*)?>");
                    var closes = Regex.Matches(trimmed, @"</([a-zA-Z]+)>");
                    foreach (Match o in opens)
                    {
                        var tag = o.Groups[1].Value;
                        if (Regex.IsMatch(trimmed, $@"</{tag}>"))
                            continue;
                        // 单行里自闭合或后续行才闭合：只统计块内再无同名开闭的情况
                        unclosedTag.Add($"{relative}:{i + 1} <{tag}> 未见 </{tag}>（文档注释不完整）");
                    }
                    var empty = Regex.IsMatch(trimmed, @"<summary\s*>\s*</summary>")
                        || Regex.IsMatch(trimmed, @"<summary\s*/>");
                    if (empty)
                        emptySummary.Add($"{relative}:{line(i)} <summary> 为空");
                    continue;
                }
                if (inDoc)
                {
                    inDoc = false;
                    // 文档块之后第一个非空、非属性行必须是被注释的成员
                    var member = NextCodeLine(lines, i);
                    if (member.Length == 0)
                        orphanDoc.Add($"{relative}:{docStart} 文档注释后面没有成员");
                    else if (member.StartsWith("#", StringComparison.Ordinal))
                        orphanDoc.Add($"{relative}:{docStart} 文档注释后面跟的是 {member}，不是成员声明");
                }
                // 公开成员缺 <summary>。只跳过**带花括号的**自动属性/索引器
                // （`public int P { get; set; }` 一行就写完了，没有位置挂文档）。
                // 表达式体成员（`public int Value => 3;`）是需要文档的，不能一起排除。
                if (Regex.IsMatch(trimmed, @"^\s*public\s") && !trimmed.Contains('{')
                    && !Regex.IsMatch(trimmed, @"get;|set;|extern|partial\s+void|#"))
                {
                    var hasDoc = i > 0 && lines[i - 1].Trim().EndsWith("</summary>", StringComparison.Ordinal);
                    if (!hasDoc)
                        missingSummary.Add($"{relative}:{i + 1} 公开成员未见 <summary> 文档注释：{Truncate(trimmed, 60)}");
                }
            }
        }
        if (files == 0)
            return "文档注释报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"文档注释报告: {files} 个 .cs 文件");
        Report(output, "公开成员缺 summary", missingSummary, maxResults);
        Report(output, "标签未闭合", unclosedTag, maxResults);
        Report(output, "summary 为空", emptySummary, maxResults);
        Report(output, "文档注释后面没有成员", orphanDoc, maxResults);
        if (missingSummary.Count == 0 && unclosedTag.Count == 0 && emptySummary.Count == 0 && orphanDoc.Count == 0)
            output.AppendLine("未发现文档注释问题");
        return output.ToString().TrimEnd();
    }

    private static int line(int i) => i + 1;

    private static string NextCodeLine(string[] lines, int from)
    {
        for (var i = from; i < lines.Length && i < from + 12; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0) continue;
            return t;
        }
        return string.Empty;
    }

    private static string Truncate(string s, int budget) =>
        s.Length <= budget ? s : s[..Math.Max(0, budget - 1)] + "…";

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
