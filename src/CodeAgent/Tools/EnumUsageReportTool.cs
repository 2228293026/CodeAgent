using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查枚举使用：switch 缺 default/未覆盖所有成员、枚举参与位运算却没有 [Flags]、
/// 以及枚举被当作整数比较大小。</summary>
public sealed class EnumUsageReportTool : ITool
{
    public string Name => "enum_usage_report";
    public string Description =>
        "只读检查枚举：switch 缺 default 分支、switch 未覆盖全部成员、位运算枚举缺少 [Flags]、枚举直接比大小。";

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
        var missingDefault = new List<string>();
        var bitwiseWithoutFlags = new List<string>();
        var orderedCompare = new List<string>();
        var files = 0;
        // 文件内出现的所有枚举名（含成员数）
        var enumMembers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var flagsEnums = new HashSet<string>(StringComparer.Ordinal);
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
            // 收集枚举定义与成员
            foreach (Match m in Regex.Matches(text, @"enum\s+(?<name>\w+)(?<attrs>[^\n{]*)"))
            {
                var name = m.Groups["name"].Value;
                // [Flags] 常写在 enum **上一行**（`[System.Flags]\nenum Perm { …`），
                // 只看 enum 同一行后面的属性会漏掉最常见的写法。
                // 注意 `text[..m.Index]` 结尾带换行，Split 出来的最后一项是空串——
                // 要取最后一个**非空**行，否则永远读到 ""。
                var before = text[..m.Index].Split('\n');
                var prevLine = before.LastOrDefault(s => s.Trim().Length > 0)?.Trim() ?? "";
                if (m.Groups["attrs"].Value.Contains("Flags", StringComparison.Ordinal)
                    || prevLine.Contains("Flags", StringComparison.Ordinal))
                    flagsEnums.Add(name);
                var members = new List<string>();
                var open = text.IndexOf('{', m.Index);
                if (open < 0) continue;
                var depth = 0;
                var close = -1;
                for (var i = open; i < text.Length && i < open + 4000; i++)
                {
                    if (text[i] == '{') depth++;
                    else if (text[i] == '}' && --depth == 0) { close = i; break; }
                }
                if (close < 0) continue;
                foreach (var part in text[(open + 1)..close].Split(','))
                {
                    var token = part.Trim().Split('=')[0].Trim();
                    if (token.Length > 0 && Regex.IsMatch(token, @"^\w+$"))
                        members.Add(token);
                }
                if (members.Count > 0)
                    enumMembers[name] = members;
            }
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                var lineNo = i + 1;
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                // switch (x) 缺 default
                if (Regex.IsMatch(trimmed, @"^\s*switch\s*\("))
                {
                    var depth = 0;
                    var hasDefault = false;
                    for (var j = i; j < lines.Length && j < i + 80; j++)
                    {
                        var t = lines[j].Trim();
                        if (Regex.IsMatch(t, @"^\s*(default)\s*:"))
                            hasDefault = true;
                        foreach (var ch in lines[j])
                        {
                            if (ch == '{') depth++;
                            else if (ch == '}') depth--;
                        }
                        if (j > i && depth <= 0)
                            break;
                    }
                    if (!hasDefault)
                        missingDefault.Add($"{relative}:{lineNo} switch 未见 default 分支（新增枚举成员时会被静默漏掉）");
                }
                // 位运算的枚举缺少 [Flags]。
                // 判据不能只看运算符两侧的**变量名**（`var c = a | b;` 两侧是 a/b，不是枚举名），
                // 要看**所在方法的签名里有没有出现该枚举类型**。
                if (Regex.IsMatch(trimmed, @"\w+\s*\|\s*\w+"))
                {
                    foreach (var (name, _) in enumMembers)
                    {
                        if (flagsEnums.Contains(name) || !MentionsEnum(lines, i, name))
                            continue;
                        bitwiseWithoutFlags.Add($"{relative}:{lineNo} 枚举 {name} 做位运算但未标记 [Flags]");
                    }
                }
                // 枚举直接比大小。同样按所在方法里出现的枚举类型判定。
                // 注意不能排除含 `=>` 的行——表达式体成员（`=> a > b;`）正是要抓的形态。
                if (Regex.IsMatch(trimmed, @"\w+\s*[<>]=?\s*\w+")
                    && !Regex.IsMatch(trimmed, @"Length|Count|Index|Version|Major|Minor"))
                {
                    foreach (var (name, _) in enumMembers)
                    {
                        if (MentionsEnum(lines, i, name))
                            orderedCompare.Add($"{relative}:{lineNo} 枚举 {name} 直接比大小（顺序没有语义，换成员顺序就会静默改变行为）");
                    }
                }
            }
        }
        if (files == 0)
            return "枚举使用报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"枚举使用报告: {files} 个 .cs 文件，{enumMembers.Count} 个枚举");
        Report(output, "switch 缺 default", missingDefault, maxResults);
        Report(output, "位运算缺 [Flags]", bitwiseWithoutFlags, maxResults);
        Report(output, "枚举比大小", orderedCompare, maxResults);
        if (missingDefault.Count == 0 && bitwiseWithoutFlags.Count == 0 && orderedCompare.Count == 0)
            output.AppendLine("未发现枚举使用问题");
        return output.ToString().TrimEnd();
    }

    /// <summary>第 i 行所在的方法（向上找最近的带括号的签名行）里是否出现了该枚举类型。
    /// 形参与局部变量的类型都写在签名/声明行上，而运算符两侧只是变量名。</summary>
    private static bool MentionsEnum(string[] lines, int i, string enumName)
    {
        var pattern = @"\b" + Regex.Escape(enumName) + @"\b";
        for (var j = i; j >= 0 && j >= i - 25; j--)
        {
            var t = lines[j].Trim();
            if (j < i && Regex.IsMatch(t, @"^(public|private|internal|protected|static).*\(") && t.Contains(')'))
                return Regex.IsMatch(t, pattern);
        }
        return lines.Skip(Math.Max(0, i - 25)).Take(26).Any(l => Regex.IsMatch(l, pattern));
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
