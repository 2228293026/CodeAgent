using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查性能反模式：循环内的重复工作、O(n²) 模式、以及无意义的装箱/字符串拼接。</summary>
public sealed class PerformanceAntiPatternReportTool : ITool
{
    public string Name => "performance_anti_pattern_report";
    public string Description =>
        "只读检查性能反模式：循环体内重复计算同一表达式、嵌套循环内的 Contains/StartsWith、以及循环内反复创建相同对象。";

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
        var hoisted = new List<string>();
        var linearInLoop = new List<string>();
        var allocateInLoop = new List<string>();
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
            var loopDepth = 0;
            var inLoopBody = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var trimmed = raw.Trim();
                var lineNo = i + 1;
                var opens = Regex.Matches(raw, @"\b(foreach|for|while)\s*\(").Count;
                var closes = Regex.Matches(raw, @"^\s*\}").Count;
                if (opens > 0)
                    loopDepth += opens;
                inLoopBody = loopDepth > 0;
                if (inLoopBody)
                {
                    // 循环体内的字符串检索：外层循环 × 内层循环 = O(n²)
                    if (Regex.IsMatch(trimmed, @"\.(Contains|StartsWith|EndsWith|IndexOf)\s*\(")
                        && loopDepth >= 2)
                        linearInLoop.Add($"{relative}:{lineNo} 嵌套循环内做线性检索（O(n²)）");
                    // 循环体内反复构造相同对象/正则（类型名可能带命名空间限定）
                    if (Regex.IsMatch(trimmed, @"new\s+(?:[\w.]+\.)?(Regex|StringBuilder|List<|Dictionary<|HashSet<)"))
                        allocateInLoop.Add($"{relative}:{lineNo} 循环体内反复构造对象（应提到循环外）");
                    // 循环体内重复计算同一个纯表达式（不涉及循环变量）
                    var expr = Regex.Match(trimmed, @"=\s*(\w+\.(?:Length|Count|Trim|ToString|Substring)\s*(?:\([^)]*\))?)");
                    if (expr.Success)
                    {
                        var call = Regex.Replace(expr.Groups[1].Value, @"\s+", string.Empty);
                        var repeats = lines.Skip(i).Take(6)
                            .Count(l => Regex.Replace(Regex.Match(l.Trim(), @"=\s*(\w+\.(?:Length|Count|Trim|ToString|Substring)\s*(?:\([^)]*\))?)").Groups[1].Value, @"\s+", string.Empty) == call);
                        if (repeats >= 3)
                            hoisted.Add($"{relative}:{lineNo} 循环体内重复计算 {call}（应提到循环外）");
                    }
                }
                loopDepth = Math.Max(0, loopDepth - closes);
            }
        }
        if (files == 0)
            return "性能反模式报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"性能反模式报告: {files} 个 .cs 文件");
        Report(output, "循环内重复计算", hoisted, maxResults);
        Report(output, "嵌套循环线性检索", linearInLoop, maxResults);
        Report(output, "循环内反复分配", allocateInLoop, maxResults);
        if (hoisted.Count == 0 && linearInLoop.Count == 0 && allocateInLoop.Count == 0)
            output.AppendLine("未发现性能反模式");
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
