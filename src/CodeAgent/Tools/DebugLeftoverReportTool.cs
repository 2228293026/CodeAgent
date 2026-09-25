using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查调试残留：断点语句、Console.WriteLine、临时的 Console.ReadKey 与被注释掉的测试。</summary>
public sealed class DebugLeftoverReportTool : ITool
{
    private static readonly (string Label, string Pattern, string Advice)[] Signals =
    {
        // 不加 ^ 锚点：if (x) Debugger.Break(); 同样致命，行中出现也必须报
        ("断点语句", @"Debugger\.Break\s*\(\s*\)\s*;", "会在运行时直接中断进程，绝不能提交"),
        ("等待按键", @"Console\.ReadKey\s*\(", "调试残留，会在自动化环境永久阻塞"),
        ("Console.WriteLine", @"Console\.Write(Line)?\s*\(", "确认是有意输出还是调试残留"),
        ("临时测试跳过", @"//\s*\[(Fact|Theory)", "被注释掉的测试等于删掉了覆盖"),
        ("Write-Host/Console 混用", @"Write-Host\b", "PowerShell 与 .NET 输出混用，管道会取不到"),
    };

    public string Name => "debug_leftover_report";
    public string Description =>
        "只读扫描调试残留：Debugger.Break、Console.ReadKey、Console.WriteLine、被注释掉的测试与 Write-Host 混用。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["signals"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "只看指定信号，默认全部",
            },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示命中行数（默认 50，最大 500）" },
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
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 50), 1, 500);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        var wanted = ToolArgs.GetStringList(args, "signals") ?? new List<string>();
        var selected = wanted.Count == 0
            ? Signals
            : Signals.Where(s => wanted.Any(tag => string.Equals(tag, s.Label, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (selected.Length == 0)
            throw new ToolException("未识别的信号（可用: " + string.Join(", ", Signals.Select(s => s.Label)) + "）");
        var counts = selected.ToDictionary(s => s.Label, _ => 0, StringComparer.OrdinalIgnoreCase);
        var hits = new List<(string Label, string File, int Line, string Text)>();
        var files = 0;
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(file);
            if (!ext.Equals(".cs", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
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
                foreach (var signal in selected)
                {
                    if (!Regex.IsMatch(lines[i], signal.Pattern))
                        continue;
                    counts[signal.Label]++;
                    hits.Add((signal.Label, relative, i + 1, TextUtil.TruncateLine(lines[i].Trim(), 80)));
                    break;
                }
            }
        }
        if (files == 0)
            return "调试残留报告: 未找到 .cs/.ps1 文件";
        var total = counts.Values.Sum();
        var output = new StringBuilder();
        output.AppendLine($"调试残留报告: 扫描 {files} 个文件，{total} 处");
        foreach (var pair in counts.Where(p => p.Value > 0).OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var advice = selected.First(s => string.Equals(s.Label, pair.Key, StringComparison.OrdinalIgnoreCase)).Advice;
            output.AppendLine($"  {pair.Key}: {pair.Value} —— {advice}");
        }
        if (total == 0)
            return output.Append("未发现调试残留").ToString();
        output.AppendLine("明细:");
        foreach (var hit in hits.OrderBy(h => h.File, StringComparer.Ordinal).ThenBy(h => h.Line).Take(maxResults))
            output.AppendLine($"  [{hit.Label}] {hit.File}:{hit.Line}  {hit.Text}");
        if (hits.Count > maxResults)
            output.AppendLine($"…（另有 {hits.Count - maxResults} 处未显示）");
        return output.ToString().TrimEnd();
    }
}
