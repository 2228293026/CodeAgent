using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查字符串拼接的写法问题：循环里反复用 `+` 拼（O(n²)）、
/// 拼接了两次以上才插值（该用插值）、以及用 + 拼日志消息（每次都白算）。</summary>
public sealed class StringInterpolationReportTool : ITool
{
    public string Name => "string_interpolation_report";
    public string Description =>
        "只读检查字符串拼接写法：循环里用 + 拼字符串（O(n²)）、两段以上拼接却不用插值、循环里拼日志消息。";

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

    /// <summary>把结果写进日志的调用：不该在循环里拼。</summary>
    internal static readonly string[] LogCalls =
    {
        "Log", "LogInformation", "LogWarning", "LogError", "LogDebug", "LogTrace",
        "Trace", "Debug", "Info", "Warn", "Console.WriteLine", "Console.Error.WriteLine",
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
        var concatInLoop = new List<string>();
        var manyConcats = new List<string>();
        var logConcatInLoop = new List<string>();
        var files = 0;
        var logPattern = string.Join('|', LogCalls);
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
                // 至少一个字符串字面量参与 + 运算，且整行没有插值串
                // 注意这里是 **字符串** "$\"" 而不是字符 '$"'——后者是两个字符的字符字面量。
                if (trimmed.Contains("$\"", StringComparison.Ordinal))
                    continue;
                // 用 \x22 表示双引号：在逐字字符串里 "" 既是转义又可能被当成结束引号，
                // 很容易数错（我数错过一次，直接编译不过）。\x22 没有这个歧义。
                if (!Regex.IsMatch(trimmed, @"\x22[^\x22]*\x22\s*\+|\+\s*\x22[^\x22]*\x22"))
                    continue;
                // 循环里拼接
                var inLoop = InLoop(lines, i);
                if (inLoop.HasValue)
                {
                    if (Regex.IsMatch(trimmed, $@"\b(?:{logPattern})\s*\("))
                        logConcatInLoop.Add($"{relative}:{lineNo} 在循环里先拼好日志字符串再写（每次迭代都白算一次；应把参数交给日志方法，或把调用挪出循环）");
                    else
                        concatInLoop.Add($"{relative}:{lineNo} 在循环里用 + 拼字符串（每次迭代都新建一个字符串，复杂度 O(n²)；应改用 StringBuilder 或插值）");
                }
                // 一次表达式里 3 段以上拼接 → 该用插值
                var plusCount = Regex.Matches(trimmed, @"\+").Count;
                if (plusCount >= 2 && Regex.Matches(trimmed, @"\x22[^\x22]*\x22").Count >= 2)
                    manyConcats.Add($"{relative}:{lineNo} 一行里有 {plusCount} 处 + 把多段拼起来（读起来难、也容易漏插值点；应写成 $\"...{{x}}...\"）");
            }
        }
        if (files == 0)
            return "字符串拼接报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"字符串拼接报告: {files} 个 .cs 文件");
        Report(output, "循环里 + 拼接", concatInLoop, maxResults);
        Report(output, "多段拼接未用插值", manyConcats, maxResults);
        Report(output, "循环里拼日志", logConcatInLoop, maxResults);
        if (concatInLoop.Count == 0 && manyConcats.Count == 0 && logConcatInLoop.Count == 0)
            output.AppendLine("未发现字符串拼接问题");
        return output.ToString().TrimEnd();
    }

    /// <summary>这一行是否处在循环里；不在则 null。</summary>
    private static int? InLoop(string[] lines, int index)
    {
        for (var k = index; k >= 0 && k > index - 25; k--)
        {
            var t = lines[k].Trim();
            if (Regex.IsMatch(t, @"^(?:for|foreach|while|do)\b"))
                return k + 1;
            if (Regex.IsMatch(t, @"^\}\s*$") || Regex.IsMatch(t, @"^\}\s*(?:else|catch|finally)"))
                return null;
        }
        return null;
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
