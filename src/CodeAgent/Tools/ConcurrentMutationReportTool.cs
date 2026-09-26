using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查任务并发安全：并行流上的共享可变集合、
/// Task.Run 里改了外层的变量、以及对非线程安全对象（Dictionary/List/Stopwatch）的并发访问。</summary>
public sealed class ConcurrentMutationReportTool : ITool
{
    public string Name => "concurrent_mutation_report";
    public string Description =>
        "只读检查并发写共享状态：并行任务里写同一个 List/Dictionary、未加锁的共享计数器、闭包里改外层局部变量。";

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
        var sharedCollection = new List<string>();
        var unlockedCounter = new List<string>();
        var closureCapture = new List<string>();
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

            // 找到「并行区域」：Task.WhenAll / Parallel.For / Task.Run 里的大括号块
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                if (!Regex.IsMatch(trimmed, @"Task\.WhenAll|Parallel\.(For|ForEach)|Task\.Run|\.AsParallel\s*\("))
                    continue;
                var open = lines[i].IndexOf('{', StringComparison.Ordinal);
                for (var j = i; open < 0 && j < i + 5; j++) open = lines[j].IndexOf('{', StringComparison.Ordinal);
                if (open < 0)
                    continue;
                var depth = 0;
                var end = -1;
                for (var j = i; j < lines.Length && j < i + 80; j++)
                {
                    foreach (var ch in lines[j])
                    {
                        if (ch == '{') depth++;
                        else if (ch == '}') { depth--; if (depth == 0) { end = j; break; } }
                    }
                    if (end >= 0) break;
                }
                if (end < 0)
                    continue;
                var body = string.Join("\n", lines.Skip(i).Take(end - i + 1));
                var locked = Regex.IsMatch(body, @"\block\s*\(|ConcurrentBag|ConcurrentDictionary|ConcurrentQueue|Interlocked\.|SemaphoreSlim");
                foreach (Match m in Regex.Matches(body, @"\b(?<c>[A-Za-z_]\w*)\.(?<op>Add|AddRange|Remove|Clear|Insert|AddOrUpdate|TryAdd|Enqueue|Dequeue|Push)\s*\("))
                {
                    if (locked) continue;
                    var c = m.Groups["c"].Value;
                    // 块内局部声明/局部新建的集合不是共享的；捕获外层的才是。
                    // 不按名字白名单猜——`results`/`bag`/`hits`/`partial` 都可能是共享集合，
                    // 按名字猜会漏掉最常见的那几个。
                    if (Regex.IsMatch(body, $@"\bvar\s+{Regex.Escape(c)}\s*=")) continue;
                    if (Regex.IsMatch(body, $@"\b{Regex.Escape(c)}\s*=\s*new\s")) continue;
                    sharedCollection.Add($"{relative}:{i + 1} 并行块里写 {c}.{m.Groups["op"].Value}（捕获的共享可变集合，多个任务并发写会丢数据甚至抛异常）");
                }
                foreach (Match m in Regex.Matches(body, @"\b(?<c>count|total|index|pos)\s*(?<op>\+\+|--|\+=|-=)"))
                {
                    if (locked) continue;
                    unlockedCounter.Add($"{relative}:{i + 1} 并行块里自增 {m.Groups["c"].Value}（非原子操作，会丢更新；应 Interlocked.Increment）");
                }
                // 闭包捕获外层变量后修改
                foreach (Match m in Regex.Matches(body, @"\b(?<c>count|total|result|items|seen|visited|partial)\b\s*(?:\.|=)"))
                {
                    if (locked) continue;
                    if (Regex.IsMatch(body, $@"\b(?:var|int|long)\s+{Regex.Escape(m.Groups["c"].Value)}\s*=")) continue;
                    closureCapture.Add($"{relative}:{i + 1} 并行任务里用到捕获的 {m.Groups["c"].Value}（每个任务改的是自己那份还是共享那份，取决于声明位置）");
                }
            }
        }
        if (files == 0)
            return "并发写入报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"并发写入报告: {files} 个 .cs 文件");
        Report(output, "并行写共享集合", sharedCollection, maxResults);
        Report(output, "未加锁的计数器自增", unlockedCounter, maxResults);
        Report(output, "并行里改写捕获变量", closureCapture, maxResults);
        if (sharedCollection.Count == 0 && unlockedCounter.Count == 0 && closureCapture.Count == 0)
            output.AppendLine("未发现并发写入问题");
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
