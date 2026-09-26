using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查可观测性缺口：捕获了异常却没记日志、
/// 关键业务分支没有任何日志/指标、以及记了日志却没带关联 id 无法追踪。</summary>
public sealed class ObservabilityGapReportTool : ITool
{
    public string Name => "observability_gap_report";
    public string Description =>
        "只读检查可观测性缺口：catch 之后既不记日志也不抛、关键状态变更没有日志/指标、日志缺少关联标识无法串联。";

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
        var silentCatch = new List<string>();
        var unobservedStateChange = new List<string>();
        var noCorrelationId = new List<string>();
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
            var text = string.Join("\n", lines);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;
                if (Regex.IsMatch(trimmed, @"^catch\b"))
                {
                    var body = new List<string>();
                    var d2 = 0;
                    var started = false;
                    for (var j = i; j < lines.Length && j < i + 20; j++)
                    {
                        foreach (var ch in lines[j])
                        {
                            if (ch == '{') { d2++; started = true; }
                            else if (ch == '}') d2--;
                        }
                        body.Add(lines[j]);
                        if (started && d2 <= 0) break;
                    }
                    if (started)
                    {
                        var bt = string.Join("\n", body);
                        var open = bt.IndexOf('{', StringComparison.Ordinal);
                        var inner = open >= 0 ? bt[(open + 1)..].TrimEnd('}') : bt;
                        if (!Regex.IsMatch(inner, @"(?i)(log|trace|warn|error|throw|Metric|Counter|Event|ILogger|Console\.Error)"))
                            silentCatch.Add($"{relative}:{lineNo} catch 之后既不记日志也不抛（出问题时线上没有任何痕迹）");
                    }
                }
                // 关键状态变更（写文件/发请求/改金额）没有伴随日志或指标
                if (Regex.IsMatch(trimmed, @"\b(File\.WriteAll\w+|Directory\.Delete|PostAsync|PutAsync|SaveChanges|ExecuteNonQuery|SendAsync)\s*\("))
                {
                    var before = string.Join("\n", lines.Skip(Math.Max(0, i - 3)).Take(Math.Min(4, i + 1)));
                    if (!Regex.IsMatch(before + trimmed, @"(?i)(log|trace|Metric|Counter|Event|ILogger)"))
                        unobservedStateChange.Add($"{relative}:{lineNo} 关键写操作没有伴随任何日志/指标（事后无法回答\"到底改没改\"）");
                }
            }
            // 日志缺少关联标识：一整个文件的日志一条关联 id 都没有。
            // 认三种写法：`LogInformation(...)`、`Log.Error(...)`（Serilog 风格）、
            // `Console.Error.WriteLine(...)`。只认一种会把另外两种的文件全算成"没有日志"。
            var logCount = Regex.Matches(text, @"(?i)(?:\bLog(?:ger|Information|Warning|Error|Debug|Trace)?\s*\.\s*\w+\s*\(|\bConsole\.(?:Error|Out)\.\w+\s*\()").Count;
            if (logCount >= 5
                && !Regex.IsMatch(text, @"(?i)(traceId|correlationId|requestId|Activity\.Current|spanId|会话|sessionId)"))
                noCorrelationId.Add($"{relative} 有 {logCount} 处日志却没有任何关联标识（多行日志无法串成一次请求，出问题只能靠时间戳猜）");
        }
        if (files == 0)
            return "可观测性缺口报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"可观测性缺口报告: {files} 个 .cs 文件");
        Report(output, "catch 无痕迹", silentCatch, maxResults);
        Report(output, "关键写操作无日志", unobservedStateChange, maxResults);
        Report(output, "日志无关联标识", noCorrelationId, maxResults);
        if (silentCatch.Count == 0 && unobservedStateChange.Count == 0 && noCorrelationId.Count == 0)
            output.AppendLine("未发现可观测性缺口");
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
