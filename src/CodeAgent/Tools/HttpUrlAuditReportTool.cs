using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读汇总源码与文档中的硬编码 URL，按主机分组并标出占位地址。</summary>
public sealed class HttpUrlAuditReportTool : ITool
{
    private static readonly Regex Url = new(
        @"https?://[^\s""'<>()\[\]\\,;]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Name => "http_url_audit_report";
    public string Description =>
        "只读提取代码和文档中的 http/https 地址，按主机分组统计，并标出 example.com/localhost 等占位地址。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["include_docs"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含 Markdown 文档（默认 true）" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示地址数（默认 100，最大 2000）" },
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
        var includeDocs = ToolArgs.GetBool(args, "include_docs", true);
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 100), 1, 2_000);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 8), 1, 32);
        var byHost = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var placeholders = new List<string>();
        var urls = new List<(string Url, string File)>();
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(file).ToLowerInvariant();
            var isDoc = ext == ".md";
            if (!isDoc && ext is not (".cs" or ".json" or ".yml" or ".yaml" or ".props" or ".targets" or ".sh" or ".ps1" or ".cmd"))
                continue;
            if (isDoc && !includeDocs)
                continue;
            string text;
            try { text = await File.ReadAllTextAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            foreach (Match match in Url.Matches(text))
            {
                var url = match.Value.TrimEnd('.', ',', ')', ']', ';', ':');
                if (url.Length == 0)
                    continue;
                var host = HostOf(url);
                byHost[host] = byHost.GetValueOrDefault(host) + 1;
                urls.Add((url, relative));
                if (IsPlaceholder(host))
                    placeholders.Add($"  {host} @ {relative}");
            }
        }
        if (urls.Count == 0)
            return "HTTP 地址审计: 未发现硬编码 URL";
        var output = new StringBuilder();
        output.AppendLine($"HTTP 地址审计: {urls.Count:N0} 处，{byHost.Count:N0} 个主机");
        output.AppendLine("按主机:");
        foreach (var pair in byHost.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            output.AppendLine($"  {pair.Key}: {pair.Value:N0}");
        if (placeholders.Count > 0)
        {
            var unique = placeholders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            output.AppendLine($"占位地址: {unique.Count:N0} 处（example.com / localhost 等，确认是否为遗留样例）");
            foreach (var line in unique.Take(maxResults))
                output.AppendLine(line);
        }
        output.AppendLine("明细:");
        foreach (var entry in urls.OrderBy(u => u.Url, StringComparer.Ordinal).ThenBy(u => u.File, StringComparer.Ordinal).Take(maxResults))
            output.AppendLine($"  {entry.Url} @ {entry.File}");
        if (urls.Count > maxResults)
            output.AppendLine($"…（另有 {urls.Count - maxResults} 处未显示）");
        return output.ToString().TrimEnd();
    }

    internal static string HostOf(string url)
    {
        var rest = url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var end = rest.IndexOfAny(['/', ':', '?', '#']);
        return (end < 0 ? rest : rest[..end]).ToLowerInvariant();
    }

    internal static bool IsPlaceholder(string host) =>
        host.Contains("example.com", StringComparison.OrdinalIgnoreCase)
        || host.Contains("example.org", StringComparison.OrdinalIgnoreCase)
        || host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
        || host.StartsWith("127.0.0.1", StringComparison.Ordinal);
}
