using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查锁的正确性：lock 里有 await/return、lock 保护了错误的范围、
/// 以及在同一个对象上嵌套 lock（自己等自己会死锁）。</summary>
public sealed class LockMisuseReportTool : ITool
{
    public string Name => "lock_misuse_report";
    public string Description =>
        "只读检查锁的使用：lock 块里有 await 或 return（Monitor 不可跨 await）、lock 了值类型、以及同一个 lock 对象嵌套 lock。";

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
        var awaitInLock = new List<string>();
        var valueTypeLock = new List<string>();
        var nestedLock = new List<string>();
        var files = 0;
        var perFile = new List<(string Relative, List<string> Locks)>();
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
            // 先收集本文件里值类型的字段名——`lock (counter)` 光看锁那行只知道名字，
            // 不知道它是 int 还是 object。靠名字猜（"像数字"）永远判不准。
            var valueTypeNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match d in Regex.Matches(string.Join("\n", lines), @"\b(?:int|long|bool|double|decimal|char|float|short|byte)\s+(?<n>\w+)\s*[;=]"))
                valueTypeNames.Add(d.Groups["n"].Value);
            var locksHere = new List<string>();
            var lockStack = new List<(string Obj, int Line, int Depth)>();
            // 看到 `lock (...)` 还不算进入临界区——`{` 可能在下一行。
            // 必须等**真的**开括号时才入栈，Depth 记的是**块内**的深度；
            // 早先在 lock 那一行就入栈，结果同一行的 `depth <= Depth` 立刻把栈弹空，
            // 嵌套检测整条失效。
            (string Obj, int Line)? pendingLock = null;
            var depth = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                {
                    foreach (var ch in line)
                    {
                        if (ch == '{') depth++;
                        else if (ch == '}') depth--;
                    }
                    continue;
                }
                var lineNo = i + 1;
                var m = Regex.Match(trimmed, @"^lock\s*\(\s*(?<o>[\w\.\(\)""\s]*?)\s*\)\s*\{?");
                if (m.Success)
                {
                    var obj = m.Groups["o"].Value.Trim();
                    locksHere.Add(obj);
                    // Monitor.Enter 锁的是**引用**；锁值类型/字符串字面量等于没锁或每次都新建
                    if (Regex.IsMatch(obj, @"^\d") || obj.Contains('"')
                        || valueTypeNames.Contains(obj)
                        || Regex.IsMatch(obj, @"^\s*(?:int|long|bool|double|decimal|char)\b"))
                        valueTypeLock.Add($"{relative}:{lineNo} lock({obj}) 锁的是值类型/字面量——每次装箱或新建实例，锁不住任何东西");
                    if (lockStack.Count > 0 && lockStack[^1].Obj == obj)
                        nestedLock.Add($"{relative}:{lineNo} lock({obj}) 嵌在第 {lockStack[^1].Line} 行的同一个 lock 里（Monitor 可重入，但这里多半是想同步两段不同状态）");
                    pendingLock = (obj, lineNo);
                }
                // 块体内容
                var opens = trimmed.Count(c => c == '{');
                var closes = trimmed.Count(c => c == '}');
                var blockDepth = depth;
                if (opens > 0)
                {
                    var body = new List<string>();
                    var d2 = 0;
                    var started = false;
                    for (var j = i; j < lines.Length && j < i + 60; j++)
                    {
                        foreach (var ch in lines[j])
                        {
                            if (ch == '{') { d2++; started = true; }
                            else if (ch == '}') d2--;
                        }
                        body.Add(lines[j]);
                        if (started && d2 <= 0) break;
                    }
                    var text = string.Join("\n", body);
                    if (Regex.IsMatch(text, @"\bawait\b"))
                    {
                        var aw = Regex.Match(text, @"\bawait\b[^;]*");
                        awaitInLock.Add($"{relative}:{lineNo} lock 块里有 {aw.Value.Trim()}——Monitor 在 await 处释放，锁的语义已经不成立（应改用 SemaphoreSlim）");
                    }
                    depth += opens;
                }
                depth -= closes;
                if (depth < 0) depth = 0;
                _ = blockDepth;
                // 看到 `{` 才算进入临界区：Depth 记的是**块内**深度（depth 已含本次 opens）
                if (opens > 0 && pendingLock is { } p)
                {
                    lockStack.Add((p.Obj, p.Line, depth));
                    pendingLock = null;
                }
                // 退出最内层 lock：深度回落到它**块内**深度之下才算离开
                while (lockStack.Count > 0 && depth < lockStack[^1].Depth)
                    lockStack.RemoveAt(lockStack.Count - 1);
            }
            if (locksHere.Count > 0)
                perFile.Add((relative, locksHere));
        }
        // 同一文件里同一个 lock 对象出现多次但拼写不同——多半是两把不同的锁
        if (files == 0)
            return "锁使用报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"锁使用报告: {files} 个 .cs 文件");
        Report(output, "lock 块里有 await", awaitInLock, maxResults);
        Report(output, "锁了值类型/字面量", valueTypeLock, maxResults);
        Report(output, "同一把锁嵌套", nestedLock, maxResults);
        if (awaitInLock.Count == 0 && valueTypeLock.Count == 0 && nestedLock.Count == 0)
            output.AppendLine("未发现锁使用问题");
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
