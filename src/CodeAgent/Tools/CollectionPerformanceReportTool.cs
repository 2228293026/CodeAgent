using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查集合使用：可改参数的返回集合、Select 多次遍历、Contains 线性查找、以及在循环里重复构造集合。</summary>
public sealed class CollectionPerformanceReportTool : ITool
{
    public string Name => "collection_performance_report";
    public string Description =>
        "只读检查集合性能：返回可被调用方改动的内部集合、循环内的 LINQ、频繁的 Contains/IndexOf 线性查找、以及循环里重复 ToList()。";

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
        var leaked = new List<string>();
        var linqInLoop = new List<string>();
        var toListInLoop = new List<string>();
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
            var loopIndent = -1; // >=0 表示正处于循环体内（记录循环头缩进）
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();
                var indent = line.Length - line.TrimStart().Length;
                var lineNo = i + 1;
                // 直接把内部集合按引用返回：调用方一次 Add/Remove 就改掉了对象内部状态。
                // 表达式体成员 `=> _items;` 是同样常见的写法，不能只认 return。
                if (Regex.IsMatch(trimmed, @"(?:return\s+|=>\s*)(?:\(\s*)?_(items|list|map|dict|set|cache|entries|values|keys)\s*;"))
                    leaked.Add($"{relative}:{lineNo} 直接返回内部集合（调用方可改坏对象状态）");
                if (Regex.IsMatch(trimmed, @"^(?:for|foreach|while)\b"))
                {
                    loopIndent = indent;
                    continue;
                }
                if (loopIndent < 0)
                    continue;
                // 缩进回落到循环头之上：循环体结束
                if (trimmed.Length > 0 && indent <= loopIndent)
                {
                    loopIndent = -1;
                    continue;
                }
                // 循环体内的 LINQ / 重复物化：每次迭代都重算一次
                if (Regex.IsMatch(trimmed, @"\.(Where|Select|OrderBy|FirstOrDefault|Any|All|Count)\s*\("))
                    linqInLoop.Add($"{relative}:{lineNo} 循环体内 LINQ（每次迭代重复枚举）");
                if (Regex.IsMatch(trimmed, @"\.ToList\s*\(\)|\.ToArray\s*\(\)|\.ToHashSet\s*\(\)"))
                    toListInLoop.Add($"{relative}:{lineNo} 循环体内重复物化集合（应提到循环外）");
            }
        }
        if (files == 0)
            return "集合性能报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"集合性能报告: {files} 个 .cs 文件");
        Report(output, "泄漏内部集合", leaked, maxResults);
        Report(output, "循环体内 LINQ", linqInLoop, maxResults);
        Report(output, "循环体内重复物化", toListInLoop, maxResults);
        if (leaked.Count == 0 && linqInLoop.Count == 0 && toListInLoop.Count == 0)
            output.AppendLine("未发现集合性能问题");
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
