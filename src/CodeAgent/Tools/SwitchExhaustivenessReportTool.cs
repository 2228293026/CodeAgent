using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 switch 的完备性：缺少 default 分支、enum switch 漏了成员、
/// 以及同一分支写了两遍（编译器不报错但第二个永远不可达）。</summary>
public sealed class SwitchExhaustivenessReportTool : ITool
{
    public string Name => "switch_exhaustiveness_report";
    public string Description =>
        "只读检查 switch 完备性：缺少 default 分支、对 enum 的 switch 漏掉成员、同一分支被重复书写。";

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
        var noDefault = new List<string>();
        var duplicateCases = new List<string>();
        var missingEnumMembers = new List<string>();
        var files = 0;
        var enumMembers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        // 先收集所有 enum 的成员
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            string? current = null;
            foreach (var raw in lines)
            {
                var t = raw.Trim();
                var open = Regex.Match(t, @"^\s*(?:public|private|internal|protected)?\s*enum\s+(?<n>\w+)");
                if (open.Success)
                {
                    current = open.Groups["n"].Value;
                    if (!enumMembers.TryGetValue(current, out var list))
                    {
                        list = new List<string>();
                        enumMembers[current] = list;
                    }
                    continue;
                }
                if (current is null)
                    continue;
                if (t.StartsWith('}') || t.Length == 0)
                {
                    if (t.StartsWith('}')) current = null;
                    continue;
                }
                var m = Regex.Match(t, @"^(?<n>[A-Za-z_]\w*)\s*(?:=\s*[^,]+)?\s*,?$");
                if (m.Success && m.Groups["n"].Value != "current")
                    enumMembers[current].Add(m.Groups["n"].Value);
            }
        }
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
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
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                var sw = Regex.Match(trimmed, @"^\s*switch\s*\(\s*(?<e>[\w\.\(\)]+)\s*\)");
                if (!sw.Success)
                    continue;
                // switch 的 '{' 常常在**下一行**，只在本行找会漏掉整个 switch。
                var open = -1;
                for (var j = i; j < lines.Length && j < i + 5; j++)
                {
                    open = lines[j].IndexOf('{', StringComparison.Ordinal);
                    if (open >= 0) break;
                }
                if (open < 0)
                    continue;
                var depth = 0;
                var end = -1;
                for (var j = i; j < lines.Length && j < i + 400; j++)
                {
                    foreach (var ch in lines[j])
                    {
                        if (ch == '{') depth++;
                        else if (ch == '}') { depth--; if (depth == 0) { end = j; break; } }
                    }
                    if (end >= 0) break;
                }
                if (end < 0)
                    continue;
                var body = string.Join("\n", lines.Skip(i).Take(end - i + 1));
                var cases = Regex.Matches(body, @"(?m)^\s*case\s+(?<v>[^:]+):")
                    .Select(m => m.Groups["v"].Value.Trim())
                    .ToList();
                var hasDefault = Regex.IsMatch(body, @"(?m)^\s*default\s*:");
                if (!hasDefault)
                    noDefault.Add($"{relative}:{i + 1} switch ({sw.Groups["e"].Value}) 没有 default 分支（将来新增成员会静默走空）");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var c in cases)
                {
                    if (!seen.Add(c))
                        duplicateCases.Add($"{relative}:{i + 1} switch 里 case {c} 重复书写（第二个永远不可达）");
                }
                // 对 enum 的 switch 漏成员。
                // switch 里往往是个裸变量名（`switch (l)`），要先把这个标识符
                // 解析回它的 enum 类型——否则 `enumMembers.TryGetValue("l")` 永远失败，
                // 整项检查静默变成空跑。
                var expr = sw.Groups["e"].Value;
                var target = expr.Contains('.') ? expr.Split('.').First() : ResolveEnumType(lines, enumMembers, expr);
                if (target is not null && enumMembers.TryGetValue(target, out var members) && members.Count > 0)
                {
                    // 两边都要归一到**裸成员名**：case 标签通常带限定（`Level.Low`），
                    // 而 enum 成员表里是裸名（`Low`）。不归一就会把每个成员都误报成漏掉。
                    var covered = new HashSet<string>(
                        cases.SelectMany(c => c.Split(',').Select(x => x.Trim()))
                             .Select(x => x.Contains('.') ? x[(x.LastIndexOf('.') + 1)..] : x),
                        StringComparer.Ordinal);
                    foreach (var member in members)
                    {
                        if (!covered.Contains(member))
                            missingEnumMembers.Add($"{relative}:{i + 1} switch ({expr}) 漏了 {target}.{member}");
                    }
                }
            }
        }
        if (files == 0)
            return "switch 完备性报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"switch 完备性报告: {files} 个 .cs 文件（识别到 {enumMembers.Count} 个 enum）");
        Report(output, "缺 default", noDefault, maxResults);
        Report(output, "漏掉的 enum 成员", missingEnumMembers, maxResults);
        Report(output, "重复的 case", duplicateCases, maxResults);
        if (noDefault.Count == 0 && missingEnumMembers.Count == 0 && duplicateCases.Count == 0)
            output.AppendLine("未发现 switch 完备性问题");
        return output.ToString().TrimEnd();
    }

    /// <summary>把 switch 里的裸标识符解析回它声明的 enum 类型。
    /// 只做文本级的"`<EnumType> <ident>`"配对（形参、形参默认值、局部变量都算），
    /// 足以覆盖 `int M(Level l) { switch (l) … }` 这种最常见的写法；
    /// 解析不到就返回 null——宁可漏报，也不要把不相干的 enum 算进来。</summary>
    private static string? ResolveEnumType(string[] lines, Dictionary<string, List<string>> enumMembers, string identifier)
    {
        foreach (var line in lines)
        {
            var m = Regex.Match(line, @"\b(?<t>[A-Z]\w*)\s+(?<id>" + Regex.Escape(identifier) + @")\s*(?:[,)=;{]|$)");
            if (m.Success && enumMembers.ContainsKey(m.Groups["t"].Value))
                return m.Groups["t"].Value;
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
