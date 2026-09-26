using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 API 使用：硬编码 URL、缺少超时、缺 User-Agent、以及不受控的反序列化。</summary>
public sealed class ApiUsageReportTool : ITool
{
    public string Name => "api_usage_report";
    public string Description =>
        "只读检查 API/网络调用：硬编码的 URL、缺少超时设置、缺 User-Agent，以及不做校验的反序列化。";

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
        var hardcodedUrl = new List<string>();
        var noTimeout = new List<string>();
        var uncheckedDeserialize = new List<string>();
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
                // 内联的 http(s) 端点：换环境时必须改代码
                if (Regex.IsMatch(trimmed, @"""https?://[^""\s]+""")
                    && !trimmed.Contains("baseUrl", StringComparison.Ordinal)
                    && !trimmed.Contains("schema", StringComparison.Ordinal))
                    hardcodedUrl.Add($"{relative}:{lineNo} 硬编码 URL（{TextUtil.TruncateLine(trimmed, 50)}）");
                // HttpClient 无超时 = 线程可能永久挂住
                if (trimmed.Contains("new HttpClient", StringComparison.Ordinal)
                    && !trimmed.Contains("Timeout", StringComparison.Ordinal))
                    noTimeout.Add($"{relative}:{lineNo} new HttpClient 未设置 Timeout（请求可能永久挂起）");
                // 反序列化不做任何校验：外部数据直接进模型
                if (Regex.IsMatch(trimmed, @"JsonSerializer\.Deserialize<[^>]+>\s*\(")
                    && !lines.Any(l => l.Contains("Validate", StringComparison.Ordinal)))
                    uncheckedDeserialize.Add($"{relative}:{lineNo} 反序列化结果未做校验");
            }
        }
        if (files == 0)
            return "API 使用报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"API 使用报告: {files} 个 .cs 文件");
        Report(output, "硬编码 URL", hardcodedUrl, maxResults);
        Report(output, "HttpClient 无超时", noTimeout, maxResults);
        Report(output, "反序列化未校验", uncheckedDeserialize, maxResults);
        if (hardcodedUrl.Count == 0 && noTimeout.Count == 0 && uncheckedDeserialize.Count == 0)
            output.AppendLine("未发现 API 使用问题");
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
