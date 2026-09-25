using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 async 正确性：async 无 await、返回 Task 却无 return、async void、以及同步阻塞的 async 方法签名。</summary>
public sealed class AsyncCorrectnessReportTool : ITool
{
    public string Name => "async_correctness_report";
    public string Description =>
        "只读检查 async 正确性：async 方法里没有 await（多余的状态机）、async void、返回 Task 却没有 return、以及无 CancellationToken 的长任务。";

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
        var noAwait = new List<string>();
        var asyncVoid = new List<string>();
        var taskNoReturn = new List<string>();
        var missingToken = new List<string>();
        var files = 0;
        var methods = 0;
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
                var signature = Regex.Match(trimmed, @"^(?:\[[^\]]*\]\s*)?(?:public|private|protected|internal|static|sealed|override|virtual|partial|\s)*\basync\s+(\S+)\s+(\w+)\s*\(([^)]*)\)\s*$");
                if (!signature.Success)
                    continue;
                methods++;
                var returnType = signature.Groups[1].Value;
                var name = signature.Groups[2].Value;
                var parameters = signature.Groups[3].Value;
                var lineNo = i + 1;
                // async void：异常无法被调用方 await 捕获，会直接崩进程
                if (returnType == "void")
                    asyncVoid.Add($"{relative}:{lineNo} {name}() 是 async void（异常无法被 await 捕获）");
                // async 方法体里没有 await：白付一次状态机开销，且易被误当成异步
                var body = BodyOf(lines, i);
                if (!Regex.IsMatch(body, @"\bawait\b"))
                    noAwait.Add($"{relative}:{lineNo} {name}() 标了 async 但方法体里没有 await");
                // 返回 Task/Task<T> 却没有 return：调用方 await 到 null
                var returnsTask = returnType.StartsWith("Task", StringComparison.Ordinal);
                var hasReturn = Regex.IsMatch(body, @"\breturn\b");
                if (returnsTask && returnType == "Task" && !hasReturn && body.Trim().Length > 0)
                    taskNoReturn.Add($"{relative}:{lineNo} {name}() 返回 Task 但方法体没有 return（await 结果为 null）");
                // 无 CancellationToken 的长任务：用户取消不掉
                if (returnsTask
                    && !parameters.Contains("CancellationToken", StringComparison.Ordinal)
                    && Regex.IsMatch(body, @"\b(File|Directory|HttpClient|Socket|Process|Stream)\w*\."))
                    missingToken.Add($"{relative}:{lineNo} {name}() 执行 I/O 但不接受 CancellationToken（无法中途取消）");
            }
        }
        if (files == 0)
            return "async 正确性报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"async 正确性报告: {files} 个 .cs 文件，{methods} 个 async 方法");
        Report(output, "async void", asyncVoid, maxResults);
        Report(output, "无 await 的 async", noAwait, maxResults);
        Report(output, "Task 无 return", taskNoReturn, maxResults);
        Report(output, "I/O 缺 CancellationToken", missingToken, maxResults);
        if (asyncVoid.Count == 0 && noAwait.Count == 0 && taskNoReturn.Count == 0 && missingToken.Count == 0)
            output.AppendLine("未发现 async 正确性问题");
        return output.ToString().TrimEnd();
    }

    /// <summary>async 方法体内容：从签名下一行起到配对闭括号（单行体也覆盖）。</summary>
    internal static string BodyOf(string[] lines, int methodIndex)
    {
        var sb = new StringBuilder();
        var openOnSameLine = lines[methodIndex].TrimEnd().EndsWith('{');
        if (openOnSameLine)
        {
            var rest = lines[methodIndex].TrimEnd()[..^1];
            var close = rest.IndexOf('}');
            sb.Append(close >= 0 ? rest[..close] : rest).Append('\n');
        }
        var depth = openOnSameLine ? 1 : 0;
        for (var i = methodIndex + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
                continue;
            if (trimmed == "}")
            {
                if (depth <= 1)
                    return sb.ToString();
                depth--;
            }
            else if (trimmed == "{")
            {
                depth++;
                continue;
            }
            sb.Append(trimmed).Append('\n');
        }
        return sb.ToString();
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
