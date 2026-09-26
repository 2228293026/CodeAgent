using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查缓存策略：重复计算未缓存、无界缓存、以及缓存键选择不当导致命中率低。</summary>
public sealed class CacheStrategyReportTool : ITool
{
    public string Name => "cache_strategy_report";
    public string Description =>
        "只读检查缓存策略：无界缓存（可能无限增长）、纯函数被反复重算、以及缓存键缺少关键区分维度。";

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
        var unbounded = new List<string>();
        var recomputed = new List<string>();
        var weakKey = new List<string>();
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
                // 无界缓存：static Dictionary/List 缓存但看不到任何容量/清理。
                // 类型名可能带命名空间限定（System.Collections.Generic.Dictionary<…>）。
                if (Regex.IsMatch(trimmed, @"static\s+(?:readonly\s+)?(?:[\w.]+\.)?(Dictionary|ConcurrentDictionary|List|HashSet|ConcurrentBag|ConcurrentQueue)<[^>]*>")
                    && trimmed.Contains('='))
                {
                    var nearby = string.Join("\n", lines.Skip(i).Take(12));
                    var bounded = nearby.Contains("Capacity", StringComparison.Ordinal)
                        || nearby.Contains("MaxSize", StringComparison.Ordinal)
                        || nearby.Contains("Count >", StringComparison.Ordinal)
                        || nearby.Contains("Clear()", StringComparison.Ordinal)
                        || nearby.Contains("Remove", StringComparison.Ordinal)
                        || nearby.Contains("Evict", StringComparison.Ordinal);
                    if (!bounded)
                        unbounded.Add($"{relative}:{lineNo} 无界静态缓存（长期运行会持续增长）");
                }
                // 纯函数在同一方法里被重复调用。
                // 必须按**调用表达式**比较：三次调用写在 `var a/b/c = …` 下，整行文本并不相同。
                var call = Regex.Match(trimmed, @"\b(?<name>(?:Get|Compute|Find|Lookup|Resolve|Parse)[A-Z]\w*)\s*\(\s*(?<arg>\w+)\s*\)");
                if (call.Success)
                {
                    var name = call.Groups["name"].Value;
                    var sameArg = call.Groups["arg"].Value;
                    var repeats = lines.Skip(i).Take(10).Count(l =>
                        Regex.IsMatch(l.Trim(), $@"\b{Regex.Escape(name)}\s*\(\s*{Regex.Escape(sameArg)}\s*\)"));
                    if (repeats >= 3)
                        recomputed.Add($"{relative}:{lineNo} 同一参数重复调用 {name}({sameArg})（可提升为局部变量）");
                }
                // 缓存键只用了易变/不唯一的维度
                if (Regex.IsMatch(trimmed, @"(?:cache|dict|map)\s*\[\s*\w+\s*\]\s*=")
                    || Regex.IsMatch(trimmed, @"GetOrAdd\s*\(\s*\w+\s*,"))
                {
                    var key = Regex.Match(trimmed, @"(?:cache|dict|map|GetOrAdd)\s*\[\(?(?<k>[\w\.]+)");
                    var keyName = key.Groups["k"].Value;
                    if (keyName.Length > 0 && Regex.IsMatch(keyName, @"^(i|index|n|count|len|idx)$", RegexOptions.IgnoreCase))
                        weakKey.Add($"{relative}:{lineNo} 缓存键用循环下标 {keyName}（跨调用会互相覆盖）");
                }
            }
        }
        if (files == 0)
            return "缓存策略报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"缓存策略报告: {files} 个 .cs 文件");
        Report(output, "无界缓存", unbounded, maxResults);
        Report(output, "重复计算", recomputed, maxResults);
        Report(output, "缓存键不唯一", weakKey, maxResults);
        if (unbounded.Count == 0 && recomputed.Count == 0 && weakKey.Count == 0)
            output.AppendLine("未发现缓存策略问题");
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
