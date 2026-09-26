using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查容量与边界：容器用默认容量构造大集合、魔法数字阈值、
/// 以及依赖默认参数长度的数组/缓冲区分配。</summary>
/// 与资源泄漏类报告分工：本工具看的是**容量与边界值**，不是释放时机。</summary>
public sealed class LifetimeBoundsReportTool : ITool
{
    public string Name => "lifetime_bounds_report";
    public string Description =>
        "只读检查容量与边界：集合用默认容量构造大缓冲、代码里的魔法数字阈值、以及按参数默认值直接分配数组。";

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
        var magicNumbers = new List<string>();
        var preallocatedBuffer = new List<string>();
        var unboundedCollect = new List<string>();
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
            var magicNames = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                var lineNo = i + 1;
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                // 按参数直接开数组：参数没上限时一次调用就能吃光内存
                if (Regex.IsMatch(trimmed, @"new\s+\w+\s*\[\s*\w+\s*\]")
                    && Regex.IsMatch(trimmed, @"\b(count|size|length|len|n)\b", RegexOptions.IgnoreCase)
                    && !Regex.IsMatch(trimmed, @"new\s+\w+\s*\[\s*\d{1,4}\s*\]")
                    && !lines.Skip(Math.Max(0, i - 5)).Take(8).Any(l => l.Contains("Math.Clamp", StringComparison.Ordinal)
                        || l.Contains("ArgumentOutOfRange", StringComparison.Ordinal)))
                    preallocatedBuffer.Add($"{relative}:{lineNo} 按入参直接分配数组且未见上限校验（{TextUtil.TruncateLine(trimmed, 40)}）");
                // 条件判断里的裸数字（阈值没有命名常量）：0/1/-1 与常见小值不算。
                // 不限定 if/return 等语句形式——表达式体成员用 `=>`，按关键字过滤会漏掉。
                foreach (Match m in Regex.Matches(trimmed, @"(?:<=|>=|==|!=|<|>)\s*(10|100|256|512|1000|1024|4096|10000|65536)\b"))
                    magicNames.Add(m.Groups[1].Value);
                // LINQ 收集进无上限集合：数据量不可控时可能 OOM
                if (Regex.IsMatch(trimmed, @"\.To(List|Dictionary|HashSet|Array)\s*\(")
                    && Regex.IsMatch(trimmed, @"\b(files|items|results|entries|lines|all)\b", RegexOptions.IgnoreCase)
                    && !lines.Skip(Math.Max(0, i - 4)).Take(8).Any(l => l.Contains("Take(", StringComparison.Ordinal)))
                    unboundedCollect.Add($"{relative}:{lineNo} 收集进无上限集合且未见 Take 限制（{TextUtil.TruncateLine(trimmed, 40)}）");
            }
            if (magicNames.Count > 0)
                magicNumbers.Add($"{relative} 出现魔法数字阈值 {string.Join("、", magicNames.OrderBy(x => x, StringComparer.Ordinal))}（应提为命名常量）");
        }
        if (files == 0)
            return "容量边界报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"容量边界报告: {files} 个 .cs 文件");
        Report(output, "按入参分配数组", preallocatedBuffer, maxResults);
        Report(output, "无上限收集", unboundedCollect, maxResults);
        Report(output, "魔法数字阈值", magicNumbers, maxResults);
        if (preallocatedBuffer.Count == 0 && unboundedCollect.Count == 0 && magicNumbers.Count == 0)
            output.AppendLine("未发现容量边界问题");
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
