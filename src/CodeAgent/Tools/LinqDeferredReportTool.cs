using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 LINQ 延迟执行的坑：查询被存成 IQueryable/IEnumerable 后才用、
/// 在循环里反复枚举同一个查询、以及查询引用了会被中途改写的集合。</summary>
public sealed class LinqDeferredReportTool : ITool
{
    public string Name => "linq_deferred_report";
    public string Description =>
        "只读检查 LINQ 延迟执行：把查询存进变量后反复使用（每次都重跑）、在循环里枚举同一个查询、查询结果被当成固定列表使用。";

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

    /// <summary>延迟算子：它们返回查询而不是结果。</summary>
    internal static readonly string[] Deferred =
    {
        "Where", "Select", "SelectMany", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending",
        "GroupBy", "Join", "GroupJoin", "Distinct", "Skip", "Take", "Cast", "OfType", "Concat", "Union",
    };

    /// <summary>终端算子：它们真正把查询跑一遍。</summary>
    internal static readonly string[] Terminal =
    {
        "ToList", "ToArray", "ToDictionary", "ToHashSet", "AsEnumerable", "AsQueryable",
        "Count", "Any", "All", "LongCount", "First", "FirstOrDefault", "Single", "SingleOrDefault",
        "Last", "LastOrDefault", "ElementAt", "Sum", "Max", "Min", "Average", "Aggregate",
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
        var reusedQuery = new List<string>();
        var queryInLoop = new List<string>();
        var mutationAfterQuery = new List<string>();
        var files = 0;
        var deferredPattern = string.Join('|', Deferred);
        var terminalPattern = string.Join('|', Terminal);
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
            // 存进变量的延迟查询：`var q = …Where(…)` 且这一行**没有**终端算子
            var queryVars = new HashSet<string>(StringComparer.Ordinal);
            var queryDeclLine = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;
                // 建立延迟查询之后又改动了集合。
                // 这一条**不能**放在"本行含延迟算子"的守卫之后——`items.Add(x)` 这一行
                // 自己没有 Where/Select，早先被守卫挡掉，规则对所有代码静默失效。
                if (Regex.IsMatch(trimmed, @"\.(?:Add|Remove|Clear|RemoveAt|AddRange|RemoveRange)\s*\(")
                    && queryDeclLine.Count > 0)
                {
                    var beforeMut = string.Join("\n", lines.Skip(Math.Max(0, i - 8)).Take(Math.Min(9, i + 1)));
                    if (Regex.IsMatch(beforeMut, $@"\.(?:{deferredPattern})\s*\(")
                        && !mutationAfterQuery.Any(x => x.StartsWith(relative + ":", StringComparison.Ordinal)))
                        mutationAfterQuery.Add($"{relative}:{lineNo} 建立延迟查询之后又改动了集合——结果会随枚举时机漂移（同一个查询两次枚举可能不同）");
                }
                var body = trimmed.TrimEnd(';', '{', '}');
                if (!Regex.IsMatch(body, $@"\.(?:{deferredPattern})\s*\("))
                    continue;
                var hasTerminal = Regex.IsMatch(body, $@"\.(?:{terminalPattern})\s*\(");
                var decl = Regex.Match(body, $@"^(?:var|IEnumerable<[^>]+>|IQueryable<[^>]+>|List<[^>]+>)\s+(?<n>\w+)\s*=");
                if (decl.Success)
                {
                    var name = decl.Groups["n"].Value;
                    // 是列表就是物化过的，安全
                    if (decl.Value.Contains("List<", StringComparison.Ordinal))
                        continue;
                    if (!hasTerminal && !queryVars.Contains(name))
                    {
                        queryVars.Add(name);
                        queryDeclLine[name] = lineNo;
                    }
                }
                // 循环里直接枚举延迟查询
                var inLoop = InLoop(lines, i);
                if (inLoop.HasValue)
                {
                    var before = string.Join("\n", lines.Skip(Math.Max(0, i - 6)).Take(Math.Min(7, i + 1)));
                    if (Regex.IsMatch(before, $@"\.(?:{deferredPattern})\s*\(")
                        && !hasTerminal
                        && Regex.IsMatch(trimmed, @"foreach\s*\(|\.ToList\s*\(|\.Count\s*\("))
                        queryInLoop.Add($"{relative}:{lineNo} 在循环里反复枚举同一条延迟查询（每次迭代都会重跑整条查询；应先用 ToList 物化一次）");
                }
            }
            foreach (var name in queryVars.OrderBy(x => x, StringComparer.Ordinal))
            {
                // 该查询变量在同一文件里被用了两次以上
                var uses = 0;
                for (var i = 0; i < lines.Length; i++)
                {
                    var t = lines[i].Trim();
                    if (Regex.IsMatch(t, $@"\b{Regex.Escape(name)}\b") && !Regex.IsMatch(t, $@"\b{Regex.Escape(name)}\s*="))
                        uses++;
                }
                if (uses >= 2)
                    reusedQuery.Add($"{relative}:{queryDeclLine[name]} {name} 是延迟查询且在文件里被用了 {uses} 次（每次使用都会重跑底层查询）");
            }
        }
        if (files == 0)
            return "LINQ 延迟执行报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"LINQ 延迟执行报告: {files} 个 .cs 文件");
        Report(output, "延迟查询被反复使用", reusedQuery, maxResults);
        Report(output, "循环里枚举延迟查询", queryInLoop, maxResults);
        Report(output, "查询后改写集合", mutationAfterQuery, maxResults);
        if (reusedQuery.Count == 0 && queryInLoop.Count == 0 && mutationAfterQuery.Count == 0)
            output.AppendLine("未发现 LINQ 延迟执行问题");
        return output.ToString().TrimEnd();
    }

    /// <summary>这一行是否处在循环里。返回循环头的行号，不在则 null。</summary>
    private static int? InLoop(string[] lines, int index)
    {
        for (var k = index; k >= 0 && k > index - 25; k--)
        {
            var t = lines[k].Trim();
            if (Regex.IsMatch(t, @"^(?:for|foreach|while)\b"))
                return k + 1;
            if (t.StartsWith("}", StringComparison.Ordinal) && Regex.IsMatch(t, @"^\}\s*$"))
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
