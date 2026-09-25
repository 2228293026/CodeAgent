using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查字符串/正则性能：在循环里编译正则、灾难性回溯的嵌套量词、以及超长输入上的字符串拼接。</summary>
public sealed class StringPerformanceReportTool : ITool
{
    public string Name => "string_performance_report";
    public string Description =>
        "只读检查字符串/正则性能：循环内 Regex 编译、嵌套量词导致的灾难性回溯、以及循环内的字符串拼接。";

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
        var regexInLoop = new List<string>();
        var backtracking = new List<string>();
        var concatInLoop = new List<string>();
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
            var loopIndent = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();
                var indent = line.Length - line.TrimStart().Length;
                var lineNo = i + 1;
                // 嵌套量词（(a+)+、(.*)* ）在无匹配的长输入上会指数级回溯
                if (Regex.IsMatch(trimmed, @"\([^()]*[+*]\)\s*[+*]") || Regex.IsMatch(trimmed, @"[+*]\)\s*\)\s*[+*]"))
                    backtracking.Add($"{relative}:{lineNo} 疑似嵌套量词（可能灾难性回溯：{TextUtil.TruncateLine(trimmed, 50)}）");
                if (Regex.IsMatch(trimmed, @"^(?:for|foreach|while)\b"))
                {
                    loopIndent = indent;
                    continue;
                }
                if (loopIndent < 0)
                    continue;
                if (trimmed.Length > 0 && indent <= loopIndent)
                {
                    loopIndent = -1;
                    continue;
                }
                if (Regex.IsMatch(trimmed, @"\bRegex\.(Match|Matches|IsMatch|Replace|Split)\s*\("))
                    regexInLoop.Add($"{relative}:{lineNo} 循环体内调用 Regex（每次迭代重新解析模式）");
                // 循环内 + 拼接字符串：每次迭代都产生一个新串并拷贝全部分片
                if (Regex.IsMatch(trimmed, @"\w+\s*\+=\s*") && !Regex.IsMatch(trimmed, @"\w+\s*\+=\s*\d"))
                    concatInLoop.Add($"{relative}:{lineNo} 循环体内字符串 += 拼接（应改 StringBuilder）");
            }
        }
        if (files == 0)
            return "字符串性能报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"字符串性能报告: {files} 个 .cs 文件");
        Report(output, "循环体内 Regex", regexInLoop, maxResults);
        Report(output, "循环体内字符串拼接", concatInLoop, maxResults);
        Report(output, "疑似灾难性回溯", backtracking, maxResults);
        if (regexInLoop.Count == 0 && concatInLoop.Count == 0 && backtracking.Count == 0)
            output.AppendLine("未发现字符串/正则性能问题");
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
