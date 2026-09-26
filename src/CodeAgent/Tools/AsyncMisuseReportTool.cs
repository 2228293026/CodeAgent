using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 async/await 误用：同步阻塞异步导致死锁、
/// async 方法里没有 await、以及不 await 也不赋值的"发射后不管"调用（异常直接丢失）。</summary>
public sealed class AsyncMisuseReportTool : ITool
{
    public string Name => "async_misuse_report";
    public string Description =>
        "只读检查 async/await 误用：.Result/.Wait() 同步阻塞（死锁）、async 方法里没有 await、不 await 也不赋值的调用（异常丢失）。";

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
        var syncBlock = new List<string>();
        var asyncWithoutAwait = new List<string>();
        var fireAndForget = new List<string>();
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
            // async 方法：先找出签名行，再扫它的大括号块
            foreach (Match sig in Regex.Matches(text, @"(?m)^\s*(?:public|private|internal|protected)[\w\s]*\basync\s+[\w<>\[\],\s]+?\s+(?<n>\w+)\s*\("))
            {
                var startLine = text[..sig.Index].Count(c => c == '\n');
                var body = Block(lines, startLine);
                if (body is null)
                    continue;
                if (!Regex.IsMatch(body, @"\bawait\b"))
                    asyncWithoutAwait.Add($"{relative}:{startLine + 1} {sig.Groups["n"].Value}() 标了 async 但方法体里没有 await（白白生成状态机；异常还会被包进 Task，栈迹变形）");
            }
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;
                // .Result / .Wait() 同步阻塞。
                // `GetAsync().Result` 里 .Result 前面是**调用括号**而不是标识符，
                // 早先的正则要求 `\w+.Result` 直接相连，于是这种最常见形态整类漏掉。
                var block = Regex.Match(trimmed, @"\b(?<v>\w+)\s*(?:\(\s*\))?\s*\.\s*(?<m>Result|Wait|WaitAll|WaitAny)\s*(?:\(\s*\))?\s*[;)]");
                if (block.Success)
                    syncBlock.Add($"{relative}:{lineNo} 用 .{block.Groups["m"].Value} 同步等待异步（UI 线程/单线程上下文里会死锁；应 async/await）");
                // 不 await 也不赋值的 XxxAsync() 调用。
                // 必须先排除**方法声明**本身（`async Task DoAsync() { }`）——
                // 它同样以 `DoAsync(` 结尾，早先没排除时，每定义一个异步方法就多报一条。
                var isDecl = Regex.IsMatch(trimmed, @"^(?:public|private|internal|protected|static|async)\b")
                    || Regex.IsMatch(trimmed, @"\b(?:Task|ValueTask)\s*<?[\w<>\[\],\s]*>?\s+\w+Async\s*\(");
                if (!isDecl
                    && Regex.IsMatch(trimmed, @"\b\w+Async\s*\(")
                    && !Regex.IsMatch(trimmed, @"\bawait\b")
                    && !Regex.IsMatch(trimmed, @"^\s*_\s*=")
                    && !Regex.IsMatch(trimmed, @"=\s*\w+Async\s*\(")
                    && !Regex.IsMatch(trimmed, @"\breturn\b"))
                    fireAndForget.Add($"{relative}:{lineNo} 调用了 Async 方法却既不 await 也不接收返回值（异常会被静默丢弃；若确实要后台执行请写 _ = 并加注释）");
            }
        }
        if (files == 0)
            return "async 误用报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"async 误用报告: {files} 个 .cs 文件");
        Report(output, "同步阻塞异步", syncBlock, maxResults);
        Report(output, "async 里没有 await", asyncWithoutAwait, maxResults);
        Report(output, "发射后不管（异常丢失）", fireAndForget, maxResults);
        if (syncBlock.Count == 0 && asyncWithoutAwait.Count == 0 && fireAndForget.Count == 0)
            output.AppendLine("未发现 async 误用");
        return output.ToString().TrimEnd();
    }

    /// <summary>取从某行开始的大括号块（从签名的下一行起）。</summary>
    private static string? Block(string[] lines, int afterLine)
    {
        var body = new List<string>();
        var depth = 0;
        var started = false;
        for (var k = afterLine; k < lines.Length && k < afterLine + 120; k++)
        {
            foreach (var ch in lines[k])
            {
                if (ch == '{') { depth++; started = true; }
                else if (ch == '}') depth--;
            }
            body.Add(lines[k]);
            if (started && depth <= 0)
                return string.Join("\n", body);
        }
        return started ? string.Join("\n", body) : null;
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
