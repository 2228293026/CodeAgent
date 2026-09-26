using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查集合与字符串操作：+= 循环拼接、Contains 做线性查找、以及 ToList/Take 在热路径。</summary>
public sealed class CollectionUsageReportTool : ITool
{
    public string Name => "collection_usage_report";
    public string Description =>
        "只读检查集合/字符串使用：循环内 += 拼接字符串、List.Contains 做成员查找（应换 HashSet）、以及 LINQ 结果被反复物化。";

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
        var stringConcat = new List<string>();
        var linearContains = new List<string>();
        var repeatedMaterialize = new List<string>();
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
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                var lineNo = i + 1;
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                // 循环内 += 拼接字符串：每次都产生新对象
                if (Regex.IsMatch(trimmed, @"^\w+\s*\+=\s*"))
                {
                    var varName = Regex.Match(trimmed, @"^(\w+)\s*\+=").Groups[1].Value;
                    if (varName.Length > 0)
                    {
                        var inLoop = lines.Skip(Math.Max(0, i - 12)).Take(12)
                            .Any(l => Regex.IsMatch(l, @"\b(foreach|for|while)\s*\(") || Regex.IsMatch(l, @"=>\s*\w+\s*=>\s*$"));
                        var inMethod = inLoop || lines.Skip(i).Take(6).Any(l => Regex.IsMatch(l, @"\b(foreach|for|while)\s*\("));
                        if (inMethod)
                            stringConcat.Add($"{relative}:{lineNo} 循环附近用 += 拼接 {varName}（应改 StringBuilder）");
                    }
                }
                // List.Contains 做成员查找：O(n)，频繁查找应换 HashSet
                if (Regex.IsMatch(trimmed, @"\w+\s*\.\s*Contains\s*\(")
                    && Regex.IsMatch(trimmed, @"(?i)\b(list|items|all|seen|results|names|keys)\b")
                    && !lines.Any(l => l.Contains("HashSet", StringComparison.Ordinal) || l.Contains("FrozenSet", StringComparison.Ordinal)))
                    linearContains.Add($"{relative}:{lineNo} 疑似用 List.Contains 做成员查找（应换 HashSet）");
                // 同一表达式被反复 ToList/ToArray 物化。
                // 必须匹配**整条链**（items.Where(…).ToList()）：只取接收者 `items` 去搜
                // `items.ToList(` 永远匹配不上——链式调用中间还隔着 Where/Select。
                var assign = Regex.Match(trimmed, @"=\s*(?<expr>[\w.]+(?:\s*\([^;]*?\))?(?:\s*\.\s*\w+\s*\([^;]*?\))*)\s*;");
                if (assign.Success && Regex.IsMatch(assign.Groups["expr"].Value, @"\.(ToList|ToArray|ToDictionary|ToHashSet|OrderBy|Where|Select|Sort)\s*\("))
                {
                    var norm = Regex.Replace(assign.Groups["expr"].Value, @"\s+", string.Empty);
                    // 比较时也必须把**候选行**同样去空白：规范化的表达式里没有空格，
                    // 直接拿去匹配原样文本永远匹配不上。
                    var repeats = lines.Skip(i).Take(8)
                        .Count(l => Regex.Replace(l, @"\s+", string.Empty).Contains("=" + norm + ";", StringComparison.Ordinal));
                    if (repeats >= 3)
                        repeatedMaterialize.Add($"{relative}:{lineNo} 同一表达式被反复物化（应缓存结果）：{TextUtil.TruncateLine(norm, 40)}");
                }
            }
        }
        if (files == 0)
            return "集合使用报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"集合使用报告: {files} 个 .cs 文件");
        Report(output, "循环内 += 拼接", stringConcat, maxResults);
        Report(output, "List.Contains 查找", linearContains, maxResults);
        Report(output, "反复物化", repeatedMaterialize, maxResults);
        if (stringConcat.Count == 0 && linearContains.Count == 0 && repeatedMaterialize.Count == 0)
            output.AppendLine("未发现集合使用问题");
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
