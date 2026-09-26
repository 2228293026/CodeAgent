using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 LINQ 的延迟执行陷阱：把查询结果存进字段却没 ToList()、
/// 多次枚举同一个查询、以及在查询中途修改底层集合。</summary>
public sealed class DeferredQueryReportTool : ITool
{
    public string Name => "deferred_query_report";
    public string Description =>
        "只读检查 LINQ 延迟执行的陷阱：把 IQueryable/IEnumerable 存成字段没物化、同一查询被枚举两次、在 foreach 里修改被枚举的集合。";

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
        var unmaterialised = new List<string>();
        var foreachMutation = new List<string>();
        var repeatedEnumeration = new List<string>();
        var files = 0;
        // 记录"查询变量被枚举了几次"，跨行统计
        var enumerations = new Dictionary<string, List<(string File, int Line)>>(StringComparer.Ordinal);
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
            var pendingForeach = "";
            var blockDepth = new List<bool>();
            var pendingLoop = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;

                // 字段/局部变量直接接一个 LINQ 查询但没有物化。
                // 注意 `=>` 表达式体属性（`private IEnumerable<int> X => src.Where(...)`）
                // 是最常见的写法，只认 `=` 会把整类漏掉。
                var store = Regex.Match(trimmed, @"^(?:(?:public|private|protected|internal|static|readonly|final|\s)*)(?:IEnumerable|IQueryable|IReadOnlyList|IReadOnlyCollection|IReadOnlySet|List|var)\s*<?[^>]*>?\s+(?<v>\w+)\s*=>?\s*[\w\.]+\.(Where|Select|OrderBy|GroupBy|ToLookup|OfType)\s*\(");
                if (store.Success)
                    unmaterialised.Add($"{relative}:{lineNo} {store.Groups["v"].Value} 存的是一个**未物化**的查询（底层集合一变它就跟着变，多次枚举会重复计算；应 .ToList() 固定下来）");

                // foreach 里修改被枚举的集合
                var fe = Regex.Match(trimmed, @"^foreach\s*\(\s*(?:var\s+)?\w+\s+in\s+(?<c>[\w\.]+)\s*\)");
                if (fe.Success)
                    pendingForeach = fe.Groups["c"].Value;
                if (pendingForeach.Length > 0)
                {
                    var mut = Regex.Match(trimmed, @"^(?:(?<c>[\w\.]+)\.(?:Add|Remove|Clear|Insert|RemoveAt|AddRange|Clear\(\))\s*\()");
                    if (mut.Success && mut.Groups["c"].Value == pendingForeach)
                        foreachMutation.Add($"{relative}:{lineNo} 正在 foreach {pendingForeach} 时修改它（枚举器已失效，运行时会抛 InvalidOperationException）");
                    if (trimmed.StartsWith('}')) pendingForeach = "";
                }

                // 记录枚举点
                var use = Regex.Match(trimmed, @"\b(?<v>[A-Za-z_]\w*)\.(Count|Any|First|FirstOrDefault|Sum|Max|Min|ToList|ToArray|Where|Select)\s*[\(\.]");
                if (use.Success && !use.Groups["v"].Value.StartsWith("SafeColor", StringComparison.Ordinal))
                {
                    var v = use.Groups["v"].Value;
                    if (!enumerations.TryGetValue(v, out var ats))
                    {
                        ats = new List<(string, int)>();
                        enumerations[v] = ats;
                    }
                    ats.Add((relative, lineNo));
                }

                var isLoop = Regex.IsMatch(trimmed, @"^\s*(for|foreach|while|do|if|else|try|lock|using|switch)\b");
                if (Regex.IsMatch(trimmed, @"^\s*(for|foreach|while|do)\b")) pendingLoop = true;
                var firstIndex = blockDepth.Count;
                var opened = 0;
                foreach (var ch in line)
                {
                    if (ch == '{') { blockDepth.Add(false); opened++; }
                    else if (ch == '}' && blockDepth.Count > 0) { blockDepth.RemoveAt(blockDepth.Count - 1); }
                }
                _ = isLoop; _ = firstIndex; _ = pendingLoop;
                if (opened == 0) pendingLoop = false;
            }
        }
        foreach (var (v, ats) in enumerations)
        {
            if (ats.Count < 3) continue;
            repeatedEnumeration.Add($"{v} 被枚举 {ats.Count} 次（{string.Join(", ", ats.Take(3).Select(a => $"{a.Item1}:{a.Item2}"))}）——若是查询表达式，每次都会重新计算");
        }
        if (files == 0)
            return "延迟执行报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"延迟执行报告: {files} 个 .cs 文件");
        Report(output, "未物化的查询", unmaterialised, maxResults);
        Report(output, "foreach 中修改集合", foreachMutation, maxResults);
        Report(output, "同一变量被反复枚举", repeatedEnumeration, maxResults);
        if (unmaterialised.Count == 0 && foreachMutation.Count == 0 && repeatedEnumeration.Count == 0)
            output.AppendLine("未发现延迟执行陷阱");
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
