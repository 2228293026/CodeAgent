using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查同一份逻辑被复制多份：等价的常量/字符串重复定义、
/// 同一个方法被复制粘贴（除名不同外完全一样）、以及同一个魔法数字散落各处。</summary>
public sealed class DuplicateLogicReportTool : ITool
{
    public string Name => "duplicate_logic_report";
    public string Description =>
        "只读检查复制粘贴：等价的常量/字面量重复定义、除名字外完全相同的方法、同一个魔法数字散落多处。";

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
        var duplicateValues = new List<string>();
        var duplicateBodies = new List<string>();
        var magicNumbers = new List<string>();
        var files = 0;
        var valueSites = new Dictionary<string, List<(string File, int Line, string Name)>>(StringComparer.Ordinal);
        var bodySites = new Dictionary<string, List<(string File, int Line, string Name)>>(StringComparer.Ordinal);
        var numberSites = new Dictionary<string, List<(string File, int Line)>>(StringComparer.Ordinal);
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
            // 方法体：按大括号配对收集
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;

                // 同一字面量赋给不同的常量名
                var lit = Regex.Match(trimmed, @"\b(?:const|static\s+readonly)\s+[\w\.<>\[\],\?]+\s+(?<n>\w+)\s*=\s*""(?<v>[^""]{8,})""\s*;");
                if (lit.Success)
                {
                    var v = lit.Groups["v"].Value;
                    if (!valueSites.TryGetValue(v, out var vs))
                    {
                        vs = new List<(string, int, string)>();
                        valueSites[v] = vs;
                    }
                    vs.Add((relative, lineNo, lit.Groups["n"].Value));
                }

                // 魔法数字：裸的 >= 1000 的字面量，且同值出现 3 次以上
                var num = Regex.Match(trimmed, @"(?<![\w.])(\d{4,})(?![\w.])");
                if (num.Success && !Regex.IsMatch(trimmed, @"(?:const|static\s+readonly|Version|TimeoutMs)"))
                {
                    var v = num.Groups[1].Value;
                    if (!numberSites.TryGetValue(v, out var ns))
                    {
                        ns = new List<(string, int)>();
                        numberSites[v] = ns;
                    }
                    ns.Add((relative, lineNo));
                }

                // 方法体：单行方法体或紧随其后的 { }
                var sig = Regex.Match(trimmed, @"(?:[\w\.<>\[\],\?]+\s+)(?<n>\w+)\s*\([^)]*\)\s*$");
                if (!sig.Success) continue;
                var bodyLines = new List<string>();
                var depth = 0;
                var started = false;
                for (var j = i + 1; j < lines.Length && j < i + 40; j++)
                {
                    foreach (var ch in lines[j])
                    {
                        if (ch == '{') { depth++; started = true; }
                        else if (ch == '}') depth--;
                    }
                    bodyLines.Add(lines[j].Trim());
                    if (started && depth <= 0) break;
                }
                if (!started || bodyLines.Count < 3) continue;
                // 归一化：抹掉标识符名，只留结构——这样"只改了函数名"的复制粘贴能被抓到。
                // 阈值 20 字符（去掉空格后）：早先设 40，把 `var y = x*2+1; var z = y-3; return y+z;`
                // 这种**真实存在**的短方法体整个漏掉，等于最常见的复制粘贴场景检测不到。
                var normalized = string.Join("\n", bodyLines).Replace(" ", "");
                if (normalized.Length < 20) continue;
                var key = Regex.Replace(normalized, @"\b[A-Za-z_]\w*\b", "x");
                if (!bodySites.TryGetValue(key, out var bs))
                {
                    bs = new List<(string, int, string)>();
                    bodySites[key] = bs;
                }
                bs.Add((relative, i + 1, sig.Groups["n"].Value));
            }
        }
        foreach (var (v, sites) in valueSites.Where(kv => kv.Value.Count > 1))
            duplicateValues.Add($"同一个字面量给了 {sites.Count} 个不同的常量名（{string.Join(", ", sites.Select(s => s.Name))}）——改一处漏一处");
        foreach (var (key, sites) in bodySites.Where(kv => kv.Value.Count > 1).Take(maxResults))
            duplicateBodies.Add($"除名字外完全相同的方法体：{string.Join(", ", sites.Select(s => $"{s.Name}@{s.File}:{s.Line}"))}");
        foreach (var (n, sites) in numberSites.Where(kv => kv.Value.Count >= 3))
            magicNumbers.Add($"裸数字 {n} 出现 {sites.Count} 次（{string.Join(", ", sites.Take(3).Select(s => $"{s.File}:{s.Line}"))}）——应提成命名常量");
        if (files == 0)
            return "重复逻辑报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"重复逻辑报告: {files} 个 .cs 文件");
        Report(output, "同一字面量多个常量名", duplicateValues, maxResults);
        Report(output, "复制粘贴的方法体", duplicateBodies, maxResults);
        Report(output, "散落的魔法数字", magicNumbers, maxResults);
        if (duplicateValues.Count == 0 && duplicateBodies.Count == 0 && magicNumbers.Count == 0)
            output.AppendLine("未发现重复逻辑");
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
