using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 API/契约演进风险：新增可选参数却没给默认值、
/// 公开方法返回可变集合引用、以及接口新增成员没有默认实现（破坏调用方）。</summary>
public sealed class ContractEvolutionReportTool : ITool
{
    public string Name => "contract_evolution_report";
    public string Description =>
        "只读检查契约演进风险：公开成员缺默认值、返回内部可变集合的引用、接口新增成员没有默认实现。";

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
        var leakedCollection = new List<string>();
        var breakingMember = new List<string>();
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
            var text = string.Join("\n", lines);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;
                // public 方法的参数既没有 = 默认值，也不是 params
                var sig = Regex.Match(trimmed, @"^public\s+(?:static\s+)?(?:async\s+)?[\w\.<>\[\],\?]+\s+\w+\s*\((?<p>[^)]*)\)");
                if (sig.Success && sig.Groups["p"].Value.Trim().Length > 0)
                {
                    foreach (var piece in SplitParams(sig.Groups["p"].Value))
                    {
                        var p = piece.Trim();
                        if (p.Length == 0 || p.Contains("=>"))
                            continue;
                        if (p.StartsWith("params ", StringComparison.Ordinal) || p.Contains('='))
                            continue;
                        // out/ref 参数不算破坏性新增
                        if (p.StartsWith("out ", StringComparison.Ordinal) || p.StartsWith("ref ", StringComparison.Ordinal)
                            || p.StartsWith("this ", StringComparison.Ordinal) || p.StartsWith("CancellationToken", StringComparison.Ordinal))
                            continue;
                        noDefault.Add($"{relative}:{lineNo} public 方法的必需参数 {p} 没有默认值（新增/改签名就会破坏所有调用方）");
                    }
                }
                // 返回内部集合的引用
                var ret = Regex.Match(trimmed, @"^public\s+(?:static\s+)?(?:IEnumerable|IReadOnlyList|IReadOnlyCollection|List|Collection|Dictionary|HashSet|ReadOnlyCollection)<?[\w\.,<>\s]*>?\s+\w+\s*\([^)]*\)\s*$");
                if (ret.Success)
                {
                    var bodyStart = i + 1;
                    var body = new List<string>();
                    var d2 = 0;
                    var started = false;
                    for (var j = bodyStart; j < lines.Length && j < bodyStart + 25; j++)
                    {
                        foreach (var ch in lines[j])
                        {
                            if (ch == '{') { d2++; started = true; }
                            else if (ch == '}') d2--;
                        }
                        body.Add(lines[j]);
                        if (started && d2 <= 0) break;
                    }
                    var bt = string.Join("\n", body);
                    if (started && Regex.IsMatch(bt, @"\breturn\s+_?\w+;") && !Regex.IsMatch(bt, @"(?i)(\.ToList\(\)|\.ToArray\(\)|\.AsReadOnly\(\)|\[\.\.\]|new\s+(?:List|ReadOnlyCollection|Array))"))
                        leakedCollection.Add($"{relative}:{lineNo} 集合类型 public 方法直接 return 内部字段（调用方一改就改到内部状态）");
                }
                // 接口成员没有默认实现
                if (Regex.IsMatch(trimmed, @"^interface\s+"))
                {
                    var inInterface = false;
                    var d3 = 0;
                    for (var j = i; j < lines.Length && j < i + 60; j++)
                    {
                        var t = lines[j].Trim();
                        if (Regex.IsMatch(t, @"^interface\s+"))
                            inInterface = true;
                        if (!inInterface)
                            continue;
                        if (Regex.IsMatch(t, @"^(?:[A-Za-z_<>\[\],\.\?\s]+)\s+\w+\s*\([^)]*\)\s*;\s*$")
                            && !Regex.IsMatch(t, @"(?i)(static|=>|default|where)"))
                        {
                            // 一条规则只报一次
                            if (!breakingMember.Any(x => x.StartsWith(relative + " ", StringComparison.Ordinal)))
                                breakingMember.Add($"{relative} 接口成员没有默认实现（新增成员会强制所有实现方一起改）");
                            break;
                        }
                        foreach (var ch in lines[j])
                        {
                            if (ch == '{') d3++;
                            else if (ch == '}') d3--;
                        }
                        if (d3 <= 0 && j > i)
                            break;
                    }
                }
            }
        }
        if (files == 0)
            return "契约演进报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"契约演进报告: {files} 个 .cs 文件");
        Report(output, "必需参数没有默认值", noDefault, maxResults);
        Report(output, "返回内部集合引用", leakedCollection, maxResults);
        Report(output, "接口成员无默认实现", breakingMember, maxResults);
        if (noDefault.Count == 0 && leakedCollection.Count == 0 && breakingMember.Count == 0)
            output.AppendLine("未发现契约演进风险");
        return output.ToString().TrimEnd();
    }

    /// <summary>按顶层逗号切分参数——不能直接 Split(',')，
    /// 泛型/数组/元组参数内部的逗号不是分隔符。</summary>
    internal static List<string> SplitParams(string p)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < p.Length; i++)
        {
            var c = p[i];
            if (c is '<' or '[' or '(')
                depth++;
            else if (c is '>' or ']' or ')')
                depth--;
            else if (c == ',' && depth == 0)
            {
                result.Add(p[start..i]);
                start = i + 1;
            }
        }
        result.Add(p[start..]);
        return result;
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
