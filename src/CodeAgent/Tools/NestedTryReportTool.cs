using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 try/finally 的控制流陷阱：嵌套 try 里内层 catch 吞掉异常、
/// finally 里 return/throw 覆盖原异常、以及用 return 代替 continue 跳过循环清理。</summary>
public sealed class NestedTryReportTool : ITool
{
    public string Name => "nested_try_report";
    public string Description =>
        "只读检查 try/finally 控制流陷阱：内层 catch 吞掉外层要处理的异常、finally 里 return/throw 吞掉原始异常、finally 里 return 覆盖循环继续。";

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
        var swallowInner = new List<string>();
        var finallyOverrides = new List<string>();
        var returnInLoopFinally = new List<string>();
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
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;
                // try 块（不是 catch/finally，也不是关键字出现在别处）
                if (!Regex.IsMatch(trimmed, @"^try\b\s*\{?"))
                    continue;
                // 取**窗口**而不是按大括号配对截取的块：try 体本身闭合后，
                // catch/finally 子句还在后面，配对截断会把 finally 整段切掉，
                // 于是"finally 里有 return"这条规则对所有代码静默失效。
                var window = string.Join("\n", lines.Skip(i).Take(60));
                // 内层 try 的 catch 是否会吞掉外层想处理的异常
                var inner = Regex.Match(window, @"(?s)\btry\b.*?\bcatch\s*\(\s*(?:System\.)?Exception\b[^)]*\)\s*\{(.*?)\}");
                if (inner.Success)
                {
                    var body = inner.Groups[1].Value;
                    var rethrows = Regex.IsMatch(body, @"(?i)\bthrow\b");
                    if (!rethrows)
                        swallowInner.Add($"{relative}:{lineNo} try 里的内层 catch 吞掉了所有 Exception 且不重新抛出（外层再也拿不到这个异常，错误会静默消失）");
                }
                // finally 块
                var fin = Regex.Match(window, @"(?s)\bfinally\b\s*\{(?<f>.*?)\}");
                if (fin.Success)
                {
                    var fbody = fin.Groups["f"].Value;
                    if (Regex.IsMatch(fbody, @"(?m)^\s*return\b"))
                        finallyOverrides.Add($"{relative}:{lineNo} finally 里有 return（会覆盖 try 里正在传播的异常，甚至把异常彻底吃掉）");
                    else if (Regex.IsMatch(fbody, @"(?m)^\s*throw\b"))
                        finallyOverrides.Add($"{relative}:{lineNo} finally 里有 throw（原异常被替换成新的，原始栈迹丢失）");
                }
                // 循环里的 try/finally，finally 用 return 代替 continue
                if (Regex.IsMatch(trimmed, @"^\s*(?:for|foreach|while)\b") == false)
                {
                    var before = string.Join("\n", lines.Skip(Math.Max(0, i - 10)).Take(11));
                    if (Regex.IsMatch(before, @"(?m)^\s*(?:for|foreach|while)\b")
                        && fin.Success
                        && Regex.IsMatch(fin.Groups["f"].Value, @"(?m)^\s*return\b"))
                        returnInLoopFinally.Add($"{relative}:{lineNo} 循环里的 try/finally 在 finally 中 return（整个迭代被提前结束，应改用 continue 让本轮继续）");
                }
            }
        }
        if (files == 0)
            return "嵌套 try 报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"嵌套 try 报告: {files} 个 .cs 文件");
        Report(output, "内层 catch 吞异常", swallowInner, maxResults);
        Report(output, "finally 覆盖异常", finallyOverrides, maxResults);
        Report(output, "finally 用 return 结束迭代", returnInLoopFinally, maxResults);
        if (swallowInner.Count == 0 && finallyOverrides.Count == 0 && returnInLoopFinally.Count == 0)
            output.AppendLine("未发现 try/finally 控制流问题");
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
