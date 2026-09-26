using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查状态机漏洞：状态字段被赋了枚举外的字符串、
/// switch 没覆盖全部枚举值、状态变更没有校验前置状态。</summary>
public sealed class StateMachineReportTool : ITool
{
    public string Name => "state_machine_report";
    public string Description =>
        "只读检查状态机漏洞：状态字段被赋枚举外的值、switch 漏了枚举分支、状态变更没有校验当前状态。";

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
        var literalOutsideEnum = new List<string>();
        var missingCase = new List<string>();
        var unguardedTransition = new List<string>();
        var files = 0;
        // 枚举名 → 成员
        var enums = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        // 字段名 → 枚举类型
        var stateFields = new Dictionary<string, string>(StringComparer.Ordinal);
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
            foreach (Match e in Regex.Matches(text, @"\benum\s+(?<n>\w+)[^\{\n]*\{(?<b>[^}]*)\}"))
            {
                var name = e.Groups["n"].Value;
                var members = new HashSet<string>(StringComparer.Ordinal);
                foreach (var raw in e.Groups["b"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var m = Regex.Match(raw, @"^(?<v>\w+)");
                    if (m.Success)
                        members.Add(m.Groups["v"].Value);
                }
                if (members.Count > 0)
                    enums[name] = members;
            }
            // 状态字段：`public Status CurrentState { get; set; }` —— **类型名**才是枚举名。
            // 早先把捕获到的名字（字段名 CurrentState）当成枚举名去查表，
            // 永远查不到，于是"赋了枚举外的值"这条规则对所有代码都静默失效。
            foreach (Match f in Regex.Matches(text, @"\b(?<t>[A-Z]\w*)\s+(?<n>\w+)\s*\{\s*get"))
            {
                if (enums.ContainsKey(f.Groups["t"].Value))
                    stateFields[f.Groups["n"].Value] = f.Groups["t"].Value;
            }
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;
                // 枚举字段被赋字面量字符串
                var assign = Regex.Match(trimmed, @"\b(?<f>\w+)\s*=\s*""(?<v>[^""]*)""\s*;");
                if (assign.Success && stateFields.ContainsKey(assign.Groups["f"].Value)
                    && !enums[stateFields[assign.Groups["f"].Value]].Contains(assign.Groups["v"].Value))
                    literalOutsideEnum.Add($"{relative}:{lineNo} {assign.Groups["f"].Value} = \"{assign.Groups["v"].Value}\" 不在 {stateFields[assign.Groups["f"].Value]} 的枚举里（拼错就静默变成未知状态）");
                // switch 语句：枚举成员覆盖
                var sw = Regex.Match(trimmed, @"^switch\s*\(\s*(?<v>[\w\.]+)\s*\)");
                if (sw.Success && sw.Groups["v"].Value.EndsWith("State", StringComparison.Ordinal))
                {
                    var varName = sw.Groups["v"].Value.Split('.').Last();
                    var cases = new HashSet<string>(StringComparer.Ordinal);
                    var hasDefault = false;
                    var d2 = 0;
                    var started = false;
                    for (var j = i + 1; j < lines.Length && j < i + 80; j++)
                    {
                        var t = lines[j].Trim();
                        if (t.StartsWith("//", StringComparison.Ordinal) || t.Length == 0)
                            continue;
                        var c = Regex.Match(t, @"^case\s+(?:[\w\.]+\.)?(?<v>\w+)\s*:");
                        if (c.Success)
                            cases.Add(c.Groups["v"].Value);
                        if (t.StartsWith("default:", StringComparison.Ordinal) || t.StartsWith("default :", StringComparison.Ordinal))
                            hasDefault = true;
                        foreach (var ch in lines[j])
                        {
                            if (ch == '{') { d2++; started = true; }
                            else if (ch == '}') d2--;
                        }
                        if (started && d2 <= 0) break;
                    }
                    if (started && !hasDefault)
                        missingCase.Add($"{relative}:{lineNo} switch({sw.Groups["v"].Value}) 没有 default（枚举新增成员时这里会静默什么都不做）");
                }
                // 状态变更没有前置校验。
                // 前置校验通常写在**前几行**（`if (state == X) { state = Y; }`），
                // 只看赋值那一行永远看不到守卫，整条规则会把所有正经写法都报一遍。
                var before = string.Join("\n", lines.Skip(Math.Max(0, i - 3)).Take(Math.Min(4, i + 1)));
                var context = before + "\n" + trimmed;
                var set = Regex.Match(trimmed, @"\b(?<f>\w*State|\w*Status|\w*Phase)\s*=\s*(?!==)");
                if (set.Success && set.Groups["f"].Value.EndsWith("State", StringComparison.Ordinal)
                    && !context.Contains("==", StringComparison.Ordinal)
                    && !Regex.IsMatch(context, @"(?i)(switch|allowed|transition|can[A-Z]|valid|guard|if\s*\()"))
                    unguardedTransition.Add($"{relative}:{lineNo} {set.Groups["f"].Value} 在没有前置校验的情况下被改写（非法状态转换不会被拦下）");
            }
        }
        if (files == 0)
            return "状态机报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"状态机报告: {files} 个 .cs 文件，{enums.Count} 个枚举");
        Report(output, "赋了枚举外的值", literalOutsideEnum, maxResults);
        Report(output, "switch 缺 default", missingCase, maxResults);
        Report(output, "状态变更无前置校验", unguardedTransition, maxResults);
        if (literalOutsideEnum.Count == 0 && missingCase.Count == 0 && unguardedTransition.Count == 0)
            output.AppendLine("未发现状态机漏洞");
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
