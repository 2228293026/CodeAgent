using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查字符串构造：字符串插值/拼接未用 StringBuilder 或 FormattableString、
/// 循环内重复拼接、以及 string.Format 的格式串与参数个数不匹配。</summary>
public sealed class StringFormatReportTool : ITool
{
    public string Name => "string_format_report";
    public string Description =>
        "只读检查字符串构造：循环内 += 拼接、字符串插值未走 FormattableString、以及 string.Format 占位符与参数个数不匹配。";

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
        var loopConcat = new List<string>();
        var unusedFormattable = new List<string>();
        var formatArity = new List<string>();
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
            var depth = 0;
            var loopDepth = 0;
            var loopBraceDepth = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                var lineNo = i + 1;
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                if (loopDepth > 0 && depth < loopBraceDepth)
                    loopDepth = 0;
                var opens = Regex.Matches(trimmed, @"{").Count;
                var closes = Regex.Matches(trimmed, @"}").Count;
                if (loopDepth == 0 && Regex.IsMatch(trimmed, @"\b(foreach|for|while)\s*\("))
                {
                    loopBraceDepth = depth;
                    loopDepth = 1;
                }
                // 循环体内 += 拼接字符串
                if (loopDepth > 0)
                {
                    var concat = Regex.Match(trimmed, @"^(?<name>\w+)\s*\+=\s*(?!\w+\s*\+=)");
                    // 确认这个变量确实是字符串：`string sb`、`StringBuilder sb`，
                    // 或最常见的 `var sb = ""`（只认初始化为空串的 var，
                    // 免得把任意 int 累加也当成字符串拼接）。
                    if (concat.Success && lines.Skip(Math.Max(0, i - 20)).Take(24).Any(l =>
                            Regex.IsMatch(l, @"^\s*(string|StringBuilder)\s+" + Regex.Escape(concat.Groups["name"].Value) + @"\b")
                            || Regex.IsMatch(l, @"^\s*var\s+" + Regex.Escape(concat.Groups["name"].Value) + @"\s*=\s*""")))
                        loopConcat.Add($"{relative}:{lineNo} 循环内 += 拼接字符串变量 {concat.Groups["name"].Value}（应改 StringBuilder）");
                }
                depth += opens - closes;
                // string.Format 占位符与参数个数不匹配（运行期才炸）
                if (trimmed.Contains("string.Format", StringComparison.Ordinal))
                {
                    var placeholders = Regex.Matches(trimmed, @"\{[0-9][^}]*\}").Count;
                    var argCount = CountFormatArgs(trimmed);
                    if (placeholders > 0 && argCount >= 0 && argCount != placeholders)
                        formatArity.Add($"{relative}:{lineNo} string.Format 有 {placeholders} 个占位符但 {argCount} 个参数（运行期 FormatException）");
                }
            }
            if (lines.Any(l => l.Contains("IFormattable", StringComparison.Ordinal))
                && !lines.Any(l => l.Contains("FormattableString", StringComparison.Ordinal)))
                unusedFormattable.Add($"{relative} 提到 IFormattable 但未见 FormattableString");
        }
        if (files == 0)
            return "字符串构造报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"字符串构造报告: {files} 个 .cs 文件");
        Report(output, "循环内 += 拼接", loopConcat, maxResults);
        Report(output, "格式串参数不匹配", formatArity, maxResults);
        Report(output, "IFormattable 未配合", unusedFormattable, maxResults);
        if (loopConcat.Count == 0 && formatArity.Count == 0 && unusedFormattable.Count == 0)
            output.AppendLine("未发现字符串构造问题");
        return output.ToString().TrimEnd();
    }

    /// <summary>粗略统计 Format 调用的实参个数：数顶层逗号。返回 -1 表示无法判定。</summary>
    private static int CountFormatArgs(string line)
    {
        var start = line.IndexOf("string.Format", StringComparison.Ordinal);
        if (start < 0)
            return -1;
        var open = line.IndexOf('(', start);
        if (open < 0)
            return -1;
        var depth = 0;
        var commas = 0;
        var inString = false;
        for (var i = open; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"' && (i == 0 || line[i - 1] != '\\'))
                inString = !inString;
            if (inString)
                continue;
            if (c == '(' || c == '[')
                depth++;
            else if (c == ')' || c == ']')
            {
                depth--;
                if (depth == 0)
                    return commas;
            }
            else if (c == ',' && depth == 1)
                commas++;
        }
        return -1;
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
