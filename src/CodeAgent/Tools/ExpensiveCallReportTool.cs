using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 API 调用卫生：重复调用同一昂贵方法、缺少缓存的纯函数、以及循环内的正则编译。</summary>
public sealed class ExpensiveCallReportTool : ITool
{
    public string Name => "expensive_call_report";
    public string Description =>
        "只读检查调用开销：循环内重复调用同一方法、同一个昂贵调用散落多处（应收敛）、以及每次调用都新建正则。";

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
        var inLoop = new List<string>();
        var regexInLoop = new List<string>();
        var scattered = new List<string>();
        var callCounts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
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
            // 昂贵调用的判据：命名暗示代价高（Get*/Resolve*/Load*/Parse*/Build*/Render*/Search*）
            var Expensive = new Regex(@"^(Get|Resolve|Load|Parse|Build|Render|Search|Compute|Query)[A-Z]\w*$");
            // 循环区间用**花括号深度**跟踪：此前用「上方 4 行内出现以 { 开头的行」判断，
            // 结果类体与方法体的开括号也被当成循环体，任何表达式体方法都会误报。
            var depth = 0;
            var loopDepth = 0;       // >0 = 当前在循环体内
            var loopBraceDepth = 0; // 进入循环时所在深度
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                var lineNo = i + 1;
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                // 先判断是否已退出上一个循环（用**上一行结束后的**深度）
                if (loopDepth > 0 && depth < loopBraceDepth)
                    loopDepth = 0;
                var opens = Regex.Matches(trimmed, @"{").Count;
                var closes = Regex.Matches(trimmed, @"}").Count;
                // `foreach (…)` 的 `{` 常在下一行：看到循环头就进入循环态
                if (loopDepth == 0 && Regex.IsMatch(trimmed, @"\b(foreach|for|while)\s*\("))
                {
                    loopBraceDepth = depth;
                    loopDepth = 1;
                }
                var calls = Regex.Matches(trimmed, @"\b(?<name>[A-Za-z_]\w*)\s*\(");
                foreach (Match call in calls)
                {
                    var name = call.Groups["name"].Value;
                    if (!Expensive.IsMatch(name))
                        continue;
                    if (!callCounts.TryGetValue(name, out var sites))
                    {
                        sites = new List<string>();
                        callCounts[name] = sites;
                    }
                    if (sites.Count < 50)
                        sites.Add($"{relative}:{lineNo}");
                }
                // 循环体内的昂贵调用
                if (loopDepth > 0)
                {
                    var expensive = calls.Cast<Match>()
                        .Select(c => c.Groups["name"].Value)
                        .FirstOrDefault(n => Expensive.IsMatch(n));
                    if (expensive is not null)
                        inLoop.Add($"{relative}:{lineNo} 循环内调用 {expensive}()（应提升到循环外或加缓存）");
                }
                // 循环内 new Regex：每次迭代都构造一次
                if (loopDepth > 0
                    && Regex.IsMatch(trimmed, @"\bnew\s+(?:[\w.]+\.)?Regex\s*\(")
                    && !lines.Any(l => l.Contains("RegexOptions.Compiled", StringComparison.Ordinal)))
                    regexInLoop.Add($"{relative}:{lineNo} 循环内 new Regex（每次迭代都重新构造）");
                depth += opens - closes;
            }
        }
        // 同一个昂贵调用散落在多个文件
        foreach (var (name, sites) in callCounts)
        {
            var files2 = sites.Select(s => s.Split(':')[0]).Distinct(StringComparer.Ordinal).Count();
            if (files2 >= 3)
                scattered.Add($"{name}() 散落在 {files2} 个文件、{sites.Count} 处（应收敛到一处或加缓存层）");
        }
        if (files == 0)
            return "调用开销报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"调用开销报告: {files} 个 .cs 文件，{callCounts.Count} 个昂贵调用名");
        Report(output, "循环内昂贵调用", inLoop, maxResults);
        Report(output, "循环内 new Regex", regexInLoop, maxResults);
        Report(output, "调用散落多处", scattered, maxResults);
        if (inLoop.Count == 0 && regexInLoop.Count == 0 && scattered.Count == 0)
            output.AppendLine("未发现调用开销问题");
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
