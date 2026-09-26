using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查并发正确性：锁粒度过大、await 期间持锁、以及共享集合的并发写。</summary>
public sealed class ConcurrencySafetyReportTool : ITool
{
    public string Name => "concurrency_safety_report";
    public string Description =>
        "只读检查并发正确性：await 期间仍持有锁（跨 await 持锁会死锁）、对共享集合无保护写入、以及手动线程缺少异常处理。";

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
        var lockAcrossAwait = new List<string>();
        var unprotectedWrite = new List<string>();
        var rawThread = new List<string>();
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
            // 逐方法扫描：lock(…) { … await … } 这种跨 await 持锁
            var lockDepth = 0;
            var braceDepthAtLock = 0;
            var depth = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                var lineNo = i + 1;
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                // `lock (…)` 的 `{` 通常在**下一行**（本仓库风格如此），
                // 所以看到 lock 就记下当前花括号深度，等深度回落到该层即视为出锁。
                if (lockDepth == 0 && Regex.IsMatch(trimmed, @"\block\s*\("))
                {
                    braceDepthAtLock = depth;
                    lockDepth = 1;
                }
                if (lockDepth > 0 && Regex.IsMatch(trimmed, @"\bawait\b"))
                    lockAcrossAwait.Add($"{relative}:{lineNo} lock 块内出现 await（跨 await 持锁可能死锁）");
                depth += Regex.Matches(trimmed, @"{").Count;
                depth -= Regex.Matches(trimmed, @"}").Count;
                // 退回到 lock 所在层**之下**才算出锁：记录的那一行 `{` 还没被计入，
                // 用 `depth <= braceDepthAtLock` 会在看到 lock 的同一行就误判为已出锁。
                if (lockDepth > 0 && depth < braceDepthAtLock)
                    lockDepth = 0;
                // 共享集合无保护写入
                if (Regex.IsMatch(trimmed, @"\w*(Cache|List|Set|Queue|Buffer|Items)\w*\s*\.\s*(Add|Remove|Clear|Enqueue|TryWrite|Push)\s*\(")
                    && !trimmed.Contains("lock", StringComparison.Ordinal)
                    && !trimmed.Contains("Interlocked", StringComparison.Ordinal)
                    && !trimmed.Contains("Concurrent", StringComparison.Ordinal)
                    && !lines.Skip(Math.Max(0, i - 8)).Take(10).Any(l => l.Contains("lock", StringComparison.Ordinal)))
                    unprotectedWrite.Add($"{relative}:{lineNo} 疑似无保护地写共享集合（{TextUtil.TruncateLine(trimmed, 40)}）");
                // 手动线程缺异常处理：未捕获异常会终止进程（类型名可能带命名空间限定）
                if (Regex.IsMatch(trimmed, @"\bnew\s+(?:[\w.]+\.)?Thread\s*\(")
                    && !lines.Skip(i).Take(8).Any(l => l.Contains("try", StringComparison.Ordinal)))
                    rawThread.Add($"{relative}:{lineNo} 裸 new Thread 未见异常处理（未捕获异常会终止进程）");
            }
        }
        if (files == 0)
            return "并发安全报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"并发安全报告: {files} 个 .cs 文件");
        Report(output, "跨 await 持锁", lockAcrossAwait, maxResults);
        Report(output, "无保护写共享集合", unprotectedWrite, maxResults);
        Report(output, "裸线程缺异常处理", rawThread, maxResults);
        if (lockAcrossAwait.Count == 0 && unprotectedWrite.Count == 0 && rawThread.Count == 0)
            output.AppendLine("未发现并发安全问题");
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
