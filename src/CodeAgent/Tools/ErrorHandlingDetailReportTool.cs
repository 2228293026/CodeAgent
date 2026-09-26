using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查错误处理：吞掉的 catch、丢失上下文的异常包装、以及未处理的 Task 异常。</summary>
public sealed class ErrorHandlingDetailReportTool : ITool
{
    public string Name => "error_handling_detail_report";
    public string Description =>
        "只读检查错误处理细节：catch 里 return 默认值而不说明、异常包装时丢失原异常、以及未 await / 未观察的 Task。";

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
        var silentDefault = new List<string>();
        var lostInner = new List<string>();
        var unobserved = new List<string>();
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
                if (Regex.IsMatch(trimmed, @"^catch\b"))
                {
                    var body = string.Join("\n", lines.Skip(i).Take(5));
                    // catch 里直接 return 默认值：调用方拿到的结果无从判断是真值还是兜底
                    if (Regex.IsMatch(body, @"return\s+(null|0|false|default|string\.Empty)\s*;")
                        && !Regex.IsMatch(body, @"throw|Log|Trace|Debug"))
                        silentDefault.Add($"{relative}:{lineNo} catch 里直接返回默认值（调用方无法区分失败）");
                    // 包装新异常时没有 innerException：原始堆栈彻底丢失
                    if (Regex.IsMatch(trimmed, @"throw\s+new\s+\w+Exception\s*\(")
                        && !Regex.IsMatch(trimmed, @"\binner\b|,\s*\w+\s*\)"))
                        lostInner.Add($"{relative}:{lineNo} 抛新异常未传 inner（原异常堆栈丢失）");
                }
                // 未观察的 Task：异常在终结时抛出，进程可能直接崩。
                // 调用常常是**不带限定**的（同类里的 DoWorkAsync()），限定符可选。
                if (Regex.IsMatch(trimmed, @"^\s*_ = (?:Task\.Run\s*\()?\s*(?:[\w.]+\.)?\w*Async\s*\("))
                {
                    var observed = lines.Any(l => l.Contains(".Wait", StringComparison.Ordinal)
                        || l.Contains(".Result", StringComparison.Ordinal)
                        || l.Contains("ContinueWith", StringComparison.Ordinal));
                    if (!observed)
                        unobserved.Add($"{relative}:{lineNo} 启动 Task 后未观察结果（异常会在终结时抛出）");
                }
            }
        }
        if (files == 0)
            return "错误处理细节报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"错误处理细节报告: {files} 个 .cs 文件");
        Report(output, "静默返回默认值", silentDefault, maxResults);
        Report(output, "异常包装丢 inner", lostInner, maxResults);
        Report(output, "未观察的 Task", unobserved, maxResults);
        if (silentDefault.Count == 0 && lostInner.Count == 0 && unobserved.Count == 0)
            output.AppendLine("未发现错误处理细节问题");
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
