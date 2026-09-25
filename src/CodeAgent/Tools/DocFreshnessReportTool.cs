using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 /docs 风格的文档陈旧度：内部链接锚点失效、版本号不一致、示例引用的文件已删除。</summary>
public sealed class DocFreshnessReportTool : ITool
{
    public string Name => "doc_freshness_report";
    public string Description =>
        "只读检查文档陈旧度：失效的内部锚点链接、章节间不一致的版本号、引用了已删除文件的示例。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示每类问题数（默认 50，最大 500）" },
            ["max_depth"] = new JsonObject { ["type"] = "integer", ["description"] = "扫描深度上限（默认 6，最大 32）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 50), 1, 500);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 6), 1, 32);
        var markdown = new List<string>();
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                markdown.Add(file);
        }
        if (markdown.Count == 0)
            return "文档新鲜度报告: 未找到 Markdown 文件";
        // 收集全文标题 → 锚点，供跨文件链接校验
        var anchors = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var headings = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in markdown)
        {
            string text;
            try { text = await File.ReadAllTextAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var key = Relative(root, file);
            headings[key] = text.Replace("\r\n", "\n").Split('\n')
                .Where(l => l.StartsWith('#'))
                .Select(HeadingText)
                .Where(h => h.Length > 0)
                .ToList();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in headings[key])
                set.Add(Anchor(h));
            anchors[key] = set;
        }
        var brokenAnchors = new List<string>();
        var versionMismatch = new List<string>();
        var deadPaths = new List<string>();
        foreach (var file in markdown)
        {
            ct.ThrowIfCancellationRequested();
            var key = Relative(root, file);
            string text;
            try { text = await File.ReadAllTextAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (Match m in Regex.Matches(text, @"\[[^\]]*\]\(([^)\s]+)\)"))
            {
                var target = m.Groups[1].Value;
                if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                    continue;
                var hash = target.IndexOf('#');
                var filePart = hash < 0 ? target : target[..hash];
                var anchor = hash < 0 ? null : target[(hash + 1)..];
                if (filePart.Length == 0)
                {
                    if (anchor is not null && !anchors[key].Contains(anchor))
                        brokenAnchors.Add($"{key} → #{anchor}");
                    continue;
                }
                var targetKey = filePart.Replace('\\', '/');
                if (!anchors.ContainsKey(targetKey))
                {
                    deadPaths.Add($"{key} → {filePart}");
                    continue;
                }
                if (anchor is not null && !anchors[targetKey].Contains(anchor))
                    brokenAnchors.Add($"{key} → {target}");
            }
            var versions = Regex.Matches(text, @"(?i)\bv?(\d+\.\d+(?:\.\d+)?)\b")
                .Select(m => m.Groups[1].Value)
                .Where(v => v.Split('.').Length == 3)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (versions.Count > 1)
                versionMismatch.Add($"{key} 出现 {versions.Count} 个不同的点分版本号: {string.Join(", ", versions.Take(6))}");
        }
        var output = new StringBuilder();
        output.AppendLine($"文档新鲜度报告: {markdown.Count} 个 Markdown 文件");
        Report(output, "失效锚点", brokenAnchors, maxResults);
        Report(output, "失效文件链接", deadPaths, maxResults);
        Report(output, "版本号不一致", versionMismatch, maxResults);
        if (brokenAnchors.Count == 0 && deadPaths.Count == 0 && versionMismatch.Count == 0)
            output.AppendLine("未发现文档陈旧问题");
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
            output.AppendLine($"  …（另有 {items.Count - maxResults} 个未显示）");
    }

    private static string Relative(string root, string file) =>
        Path.GetRelativePath(root, file).Replace('\\', '/');

    /// <summary>取 Markdown 标题文本（去掉 # 与结尾的 #）。</summary>
    internal static string HeadingText(string line) =>
        Regex.Replace(line.Trim().TrimStart('#'), @"\s*#+\s*$", string.Empty).Trim();

    /// <summary>GitHub 风格锚点：小写、空格转连字符、去掉标点。</summary>
    internal static string Anchor(string heading)
    {
        var lower = heading.ToLowerInvariant();
        var sb = new StringBuilder(lower.Length);
        foreach (var ch in lower)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
                sb.Append(ch);
            else if (char.IsWhiteSpace(ch))
                sb.Append('-');
        }
        return sb.ToString();
    }
}
