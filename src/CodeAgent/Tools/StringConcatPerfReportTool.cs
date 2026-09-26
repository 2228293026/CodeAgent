using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查字符串在循环里被反复拼接（每次迭代都新建一个字符串），
/// 以及一行里连着很多个 + 的可读性问题。</summary>
public sealed class StringConcatPerfReportTool : ITool
{
    public string Name => "string_concat_perf_report";
    public string Description =>
        "只读检查字符串拼接性能：循环体内反复 += 或 = a + b、循环里不变的字面量前缀、超长多段拼接。";

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

    private static readonly Regex LoopHeader = new(@"^\s*(for|foreach|while|do)\b", RegexOptions.Compiled);
    private static readonly Regex PlusAssign = new(@"\b(?<v>[A-Za-z_]\w*)\s*\+=", RegexOptions.Compiled);
    private static readonly Regex EqConcat = new(@"\b(?<v>[A-Za-z_]\w*)\s*=\s*[^;]*\+", RegexOptions.Compiled);
    private static readonly Regex LiteralPrefix = new(@"\b\w+\s*=\s*[""'].*?[+{]", RegexOptions.Compiled);

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 30), 1, 300);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        var inLoopList = new List<string>();
        var longConcat = new List<string>();
        var invariantInLoop = new List<string>();
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

            // 逐行跟踪"当前是否处在循环体里"。这里踩过两个坑，都记在注释里：
            //
            // ① 早期版本只对循环压栈、却对**每个** } 都弹栈，于是循环体里套一个 if 时，
            //    if 的 } 会把外层循环的栈帧弹掉，if 之后的行被误判成"不在循环里"。
            //    现在**每个** { 都压一帧，帧上记"这是不是循环体"。
            //
            // ② 循环体的大括号可能单独占一行（`for (...)` 换行再写 `{`），那一行本身
            //    不匹配循环关键字。所以记住"有个循环头等着开体"（pendingLoop），由下一个
            //    { 兑现。同一行有多个大括号时（`foreach (var x in new[] { 1, 2 })`），
            //    只有**最后一个**是循环体，前面的是数组初始化。
            var blocks = new List<bool>();   // 每层大括号是不是循环体
            var pendingLoop = false;         // 有循环头还没开体
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var isLoopHeader = LoopHeader.IsMatch(trimmed);
                if (isLoopHeader)
                    pendingLoop = true;
                var inLoop = blocks.Exists(x => x);
                var lineNo = i + 1;

                var plus = PlusAssign.Match(trimmed);
                if (plus.Success)
                    inLoopList.Add($"{relative}:{lineNo} {plus.Groups["v"].Value} += …（每次拼接都新建一个字符串，循环里应改 StringBuilder）");
                else
                {
                    var eq = EqConcat.Match(trimmed);
                    if (eq.Success && inLoop)
                        inLoopList.Add($"{relative}:{lineNo} 在循环体内用 {eq.Groups["v"].Value} = … + … 反复拼接（应改 StringBuilder）");
                    if (inLoop && LiteralPrefix.IsMatch(trimmed))
                        invariantInLoop.Add($"{relative}:{lineNo} 循环里拼接的前缀是字面常量，应提到循环外");
                }
                if (trimmed.Length > 80 && trimmed.Count(c => c == '+') >= 3)
                    longConcat.Add($"{relative}:{lineNo} 一行里有 {trimmed.Count(c => c == '+')} 个 + 且超过 80 字符（可读性与性能双差，应改插值或 StringBuilder）");

                var firstIndex = blocks.Count;   // 本行压入的第一帧下标
                var opened = 0;
                foreach (var ch in line)
                {
                    if (ch == '{')
                    {
                        blocks.Add(pendingLoop && !isLoopHeader);
                        opened++;
                    }
                    else if (ch == '}' && blocks.Count > 0)
                    {
                        blocks.RemoveAt(blocks.Count - 1);
                    }
                }
                // 循环体的大括号如果就在本行：把**本行最后一个仍未闭合的** '{' 判成循环体。
                // 注意 `foreach (var x in new[] { 1, 2 })` 这种行——本行的 '{' 属于数组
                // 初始化且当场就闭合了，循环体其实在下一行；这时不能去改那一帧（它已经出栈），
                // 必须让 pendingLoop 继续等着，由下一行的 '{' 兑现。
                if (isLoopHeader && opened > 0 && blocks.Count > firstIndex + opened - 1)
                {
                    blocks[firstIndex + opened - 1] = true;
                    pendingLoop = false;
                }
            }
        }
        if (files == 0)
            return "字符串拼接性能报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"字符串拼接性能报告: {files} 个 .cs 文件");
        Report(output, "循环内的 += 拼接", inLoopList, maxResults);
        Report(output, "超长多段拼接", longConcat, maxResults);
        Report(output, "循环里重复的常量前缀", invariantInLoop, maxResults);
        if (inLoopList.Count == 0 && longConcat.Count == 0 && invariantInLoop.Count == 0)
            output.AppendLine("未发现字符串拼接性能问题");
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
