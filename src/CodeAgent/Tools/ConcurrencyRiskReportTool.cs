using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查并发风险：直接 Task.Run/.Result/.Wait、async 里的 Thread.Sleep、以及共享静态可变状态。</summary>
public sealed class ConcurrencyRiskReportTool : ITool
{
    public string Name => "concurrency_risk_report";
    public string Description =>
        "只读检查并发风险：阻塞式 Task.Result/.Wait()、未 await 的异步调用、async 方法里的 Thread.Sleep，以及无锁的共享静态可变字段。";

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
        var blocking = new List<string>();
        var fireAndForget = new List<string>();
        var sleepInAsync = new List<string>();
        var sharedState = new List<string>();
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
            var asyncMethod = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();
                if (Regex.IsMatch(trimmed, @"\basync\b"))
                    asyncMethod = true;
                // .Result / .Wait() 阻塞线程池线程：async 上下文里尤其危险
                if (Regex.IsMatch(line, @"\.Result\b") || Regex.IsMatch(line, @"\.Wait\s*\(\s*\)"))
                    blocking.Add($"{relative}:{i + 1} 阻塞等待（{TextUtil.TruncateLine(trimmed, 60)}）");
                if (asyncMethod && Regex.IsMatch(trimmed, @"^\s*Thread\.Sleep\s*\("))
                    sleepInAsync.Add($"{relative}:{i + 1} async 中 Thread.Sleep（应改 Task.Delay）");
                // 调用异步方法但既没 await 也没接收返回值：异常被静默丢弃
                var asyncLooking = Regex.IsMatch(trimmed, @"(?i)\b(Start|Load|Save|Send|Fetch|Refresh|Update|Init)\w*\s*\(\s*\)\s*;");
                var notAwaited = Regex.IsMatch(trimmed, @"^\s*(?!await|var|return|_ = )[A-Za-z_][A-Za-z0-9_]*\s*\(\s*\)\s*;");
                if (asyncLooking && notAwaited)
                    fireAndForget.Add($"{relative}:{i + 1} 可能的未 await 异步调用（{TextUtil.TruncateLine(trimmed, 60)}）");
                // 无锁的静态可变字段：static + 非 readonly/const，且不是静态方法或只读属性
                var staticField = Regex.IsMatch(trimmed, @"^\s*(public|internal|private)?\s*static\s+(?!readonly\b)(?!const\b)\w");
                var isMethod = Regex.IsMatch(trimmed, @"\(\s*\)");
                var isReadOnlyProperty = Regex.IsMatch(trimmed, @"\{\s*get");
                if (staticField && !isMethod && !isReadOnlyProperty)
                    sharedState.Add($"{relative}:{i + 1} 静态可变字段（并发下需同步）");
            }
        }
        if (files == 0)
            return "并发风险报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"并发风险报告: {files} 个 .cs 文件");
        Report(output, "阻塞等待", blocking, maxResults);
        Report(output, "async 中阻塞", sleepInAsync, maxResults);
        Report(output, "可能的未 await 调用", fireAndForget, maxResults);
        Report(output, "静态可变字段", sharedState, maxResults);
        if (blocking.Count == 0 && sleepInAsync.Count == 0 && fireAndForget.Count == 0 && sharedState.Count == 0)
            output.AppendLine("未发现并发风险迹象");
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
