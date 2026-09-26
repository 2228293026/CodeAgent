using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查迭代器（yield return）的陷阱：方法体里用 return 结束迭代、
/// yield 出现在 try-catch 里、以及迭代器里做阻塞 I/O。</summary>
public sealed class IteratorYieldReportTool : ITool
{
    public string Name => "iterator_yield_report";
    public string Description =>
        "只读检查迭代器陷阱：含 yield 的方法里用 return 结束（调用方看到不完整序列）、yield 出现在 try-catch 中、迭代器里做阻塞 I/O。";

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
        var returnInIterator = new List<string>();
        var yieldInCatch = new List<string>();
        var blockingIo = new List<string>();
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
            // 含 yield 的方法
            foreach (Match sig in Regex.Matches(text, @"(?m)^\s*(?:public|private|internal|protected)[\w\s<>\[\],\.]*?\s(?<n>\w+)\s*\([^)]*\)(?:\s*where[^\{]*)?\s*\{"))
            {
                var startLine = text[..sig.Index].Count(c => c == '\n');
                var body = Block(lines, startLine);
                if (body is null || !Regex.IsMatch(body, @"\byield\s+(?:return|break)\b"))
                    continue;
                var name = sig.Groups["n"].Value;
                var bodyLines = body.Split('\n');
                for (var k = 0; k < bodyLines.Length; k++)
                {
                    var t = bodyLines[k].Trim();
                    if (t.Length == 0 || t.StartsWith("//", StringComparison.Ordinal))
                        continue;
                    var absLine = startLine + k + 2;
                    if (Regex.IsMatch(t, @"^\s*return\b"))
                        returnInIterator.Add($"{relative}:{absLine} 迭代器 {name}() 里用 return 结束（调用方以为枚举完了，实际序列被提前截断，且不抛任何异常）");
                    // yield 出现在 try/catch 里
                    if (Regex.IsMatch(t, @"\byield\b")
                        && string.Join("\n", bodyLines.Skip(Math.Max(0, k - 3)).Take(Math.Min(4, k + 1))).Contains("try", StringComparison.Ordinal)
                        && string.Join("\n", bodyLines.Skip(k).Take(8)).Contains("catch", StringComparison.Ordinal))
                        yieldInCatch.Add($"{relative}:{absLine} 迭代器 {name}() 的 yield 出现在 try-catch 中（语言限制；应把 try-catch 移进单独的辅助方法）");
                    // 迭代器里做阻塞 I/O
                    if (Regex.IsMatch(t, @"\b(?:File\.ReadAll\w*|File\.WriteAll\w*|\.Result|\.Wait\s*\(|Thread\.Sleep|HttpClient|SendAsync\s*\(|\.Result\b)"))
                        blockingIo.Add($"{relative}:{absLine} 迭代器 {name}() 里做阻塞 I/O（每次 MoveNext 都会真的去取数，调用方以为只是遍历内存序列；应先物化或改用异步迭代器）");
                }
            }
        }
        if (files == 0)
            return "迭代器报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"迭代器报告: {files} 个 .cs 文件");
        Report(output, "迭代器里用 return 结束", returnInIterator, maxResults);
        Report(output, "yield 在 try-catch 中", yieldInCatch, maxResults);
        Report(output, "迭代器里阻塞 I/O", blockingIo, maxResults);
        if (returnInIterator.Count == 0 && yieldInCatch.Count == 0 && blockingIo.Count == 0)
            output.AppendLine("未发现迭代器问题");
        return output.ToString().TrimEnd();
    }

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
