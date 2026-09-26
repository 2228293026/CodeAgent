using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查日志卫生：敏感信息（密钥/令牌/口令）被写进日志、异常里夹带原始请求体、以及调试残留。</summary>
public sealed class LogHygieneReportTool : ITool
{
    public string Name => "log_hygiene_report";
    public string Description =>
        "只读检查日志卫生：日志/异常里出现密钥令牌等敏感值、异常消息夹带完整请求体、以及 Console.WriteLine 调试残留。";

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
        var secretInLog = new List<string>();
        var bodyInException = new List<string>();
        var debugResidue = new List<string>();
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
                // 日志/输出里带敏感字段名 + 变量：值会被原样写进日志
                if (Regex.IsMatch(trimmed, @"\b(Log|Trace|Debug|Info|Warn|Error|Fatal|Write(Line)?)\s*\(")
                    && Regex.IsMatch(trimmed, @"(?i)\b(apiKey|api_key|accessToken|access_token|password|passwd|secret|clientSecret|authorization|bearer)\b"))
                    secretInLog.Add($"{relative}:{lineNo} 日志/输出中出现敏感字段（{TextUtil.TruncateLine(trimmed, 40)}）");
                // 抛出的异常把请求/响应原文拼进消息里。
                // 判据是 `throw` + 敏感内容词 + 插值/拼接——早先用 `\bexception\b` 定位失败：
                // `InvalidOperationException` 里 E 前面是字母，没有词边界，\b 永远不成立。
                if (Regex.IsMatch(trimmed, @"(?i)\bthrow\b")
                    && Regex.IsMatch(trimmed, @"(?i)\b(request|response|payload|body|headers|bearer|token)\w*\b")
                    && (trimmed.Contains('$') || trimmed.Contains("+ \"", StringComparison.Ordinal))
                    && !trimmed.Contains("Length", StringComparison.Ordinal))
                    bodyInException.Add($"{relative}:{lineNo} 异常/错误里夹带请求或响应全文（可能泄露敏感内容）");
                // 调试残留
                if (Regex.IsMatch(trimmed, @"\bConsole\.Write(Line)?\s*\(")
                    && !Regex.IsMatch(trimmed, @"\bConsole\.(Out|Error)\b"))
                    debugResidue.Add($"{relative}:{lineNo} Console.Write* 调试残留（应走统一日志/输出通道）");
                if (Regex.IsMatch(trimmed, @"\bDebugger\.Break\s*\(") || Regex.IsMatch(trimmed, @"//\s*TODO\s*:\s*remove\b"))
                    debugResidue.Add($"{relative}:{lineNo} 调试断点/待删标记残留");
            }
        }
        if (files == 0)
            return "日志卫生报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"日志卫生报告: {files} 个 .cs 文件");
        Report(output, "日志含敏感字段", secretInLog, maxResults);
        Report(output, "异常夹带全文", bodyInException, maxResults);
        Report(output, "调试残留", debugResidue, maxResults);
        if (secretInLog.Count == 0 && bodyInException.Count == 0 && debugResidue.Count == 0)
            output.AppendLine("未发现日志卫生问题");
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
