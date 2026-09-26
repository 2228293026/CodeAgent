using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查日志规范：直接 Console 写日志、拼接异常只取 Message、以及缺少结构化上下文的日志。</summary>
public sealed class LoggingPracticeReportTool : ITool
{
    public string Name => "logging_practice_report";
    public string Description =>
        "只读检查日志规范：直接 Console 写日志、只取 ex.Message 丢失堆栈、以及 catch 里只记日志不处理也未重抛。";

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
        var consoleWrites = new List<string>();
        var messageOnly = new List<string>();
        var swallow = new List<string>();
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
                // 库代码直接 Console 写日志：输出无法被宿主重定向/分级
                var isEntryPoint = relative.Contains("Program.cs", StringComparison.OrdinalIgnoreCase);
                if (!isEntryPoint && Regex.IsMatch(trimmed, @"Console\.(Write|WriteLine|Error\.WriteLine)\s*\("))
                    consoleWrites.Add($"{relative}:{lineNo} 库代码直接写控制台（应走日志抽象）");
                // 只取 Message：丢掉了堆栈与异常类型，定位问题时要重新复现
                if (Regex.IsMatch(trimmed, @"\bex(ception)?\.Message\b")
                    && !trimmed.Contains("GetType", StringComparison.Ordinal)
                    && !trimmed.Contains("StackTrace", StringComparison.Ordinal)
                    && !trimmed.Contains("ex.ToString", StringComparison.Ordinal))
                    messageOnly.Add($"{relative}:{lineNo} 只记录 {Regex.Match(trimmed, @"\bex(ception)?\b").Value}.Message（丢失堆栈）");
                // catch 只写日志，既不处理也不重抛：异常被静默吞掉
                if (Regex.IsMatch(trimmed, @"^catch\b") && !trimmed.Contains("throw", StringComparison.Ordinal))
                {
                    var body = string.Join("\n", lines.Skip(i).Take(4));
                    if (Regex.IsMatch(body, @"(Log|Trace|Debug|Warn|Error|WriteLine)\s*\(") && !Regex.IsMatch(body, @"return\s"))
                        swallow.Add($"{relative}:{lineNo} catch 只记日志既不处理也不重抛");
                }
            }
        }
        if (files == 0)
            return "日志规范报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"日志规范报告: {files} 个 .cs 文件");
        Report(output, "库代码写控制台", consoleWrites, maxResults);
        Report(output, "只取 Message", messageOnly, maxResults);
        Report(output, "catch 吞异常", swallow, maxResults);
        if (consoleWrites.Count == 0 && messageOnly.Count == 0 && swallow.Count == 0)
            output.AppendLine("未发现日志规范问题");
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
