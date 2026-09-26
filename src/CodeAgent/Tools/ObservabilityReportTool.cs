using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查可观测性：日志级别误用、异常被吞、以及缺少关键操作的日志埋点。</summary>
public sealed class ObservabilityReportTool : ITool
{
    public string Name => "observability_report";
    public string Description =>
        "只读检查可观测性：把敏感内容写进日志、错误路径完全没有日志、以及用 Console 直接输出代替日志。";

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

    /// <summary>日志调用识别：方法名（log/logger/_logger/ILogger/Trace/Warn/Error/Info）**忽略大小写**。
    /// 只认大写开头会漏掉最常见的 logger/log 命名，于是「错误被彻底静默」把正常记日志的 catch 全误报。</summary>
    private static readonly Regex LogCallRe = new(
        @"(?:log|logger|_logger|ilogger|trace|warn|error|info|debug|fatal)\s*\.\s*\w+\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ConsoleWriteRe = new(
        @"Console\.(Write|WriteLine)\s*\(", RegexOptions.Compiled);

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 30), 1, 300);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        var secretLogged = new List<string>();
        var silentError = new List<string>();
        var consoleInsteadOfLog = new List<string>();
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
                // 把密钥/口令写进日志
                if (LogCallRe.IsMatch(trimmed)
                    && Regex.IsMatch(trimmed, @"(?i)(apiKey|apikey|password|secret|token|credential)")
                    && !trimmed.Contains("Redact", StringComparison.Ordinal)
                    && !trimmed.Contains("***", StringComparison.Ordinal))
                    secretLogged.Add($"{relative}:{lineNo} 日志里可能写入敏感字段（{TextUtil.TruncateLine(trimmed, 45)}）");
                // 错误分支没有任何日志
                if (Regex.IsMatch(trimmed, @"^catch\b"))
                {
                    var body = string.Join("\n", lines.Skip(i).Take(6));
                    var logs = LogCallRe.IsMatch(body) || ConsoleWriteRe.IsMatch(body);
                    var rethrows = Regex.IsMatch(body, @"\bthrow\b");
                    if (!logs && !rethrows && Regex.IsMatch(body, @"}"))
                        silentError.Add($"{relative}:{lineNo} catch 既不记日志也不上抛（错误彻底静默）");
                }
                // 用 Console 直出代替日志（无法按级别过滤/落盘）
                if (ConsoleWriteRe.IsMatch(trimmed)
                    && !Regex.IsMatch(trimmed, @"Console\.(Write|WriteLine)\s*\(\s*""")
                    && !lines.Skip(Math.Max(0, i - 3)).Take(4).Any(l => l.Contains("//", StringComparison.Ordinal)))
                    consoleInsteadOfLog.Add($"{relative}:{lineNo} 用 Console 输出非固定内容（应走日志以便分级与落盘）");
            }
        }
        if (files == 0)
            return "可观测性报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"可观测性报告: {files} 个 .cs 文件");
        Report(output, "日志含敏感字段", secretLogged, maxResults);
        Report(output, "错误被彻底静默", silentError, maxResults);
        Report(output, "Console 代替日志", consoleInsteadOfLog, maxResults);
        if (secretLogged.Count == 0 && silentError.Count == 0 && consoleInsteadOfLog.Count == 0)
            output.AppendLine("未发现可观测性问题");
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
