using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读扫描测试代码中的易波动因素（跳过、固定等待、轮询、环境依赖），提示潜在不稳定测试。</summary>
public sealed class TestFlakinessReportTool : ITool
{
    private static readonly (string Label, string Pattern, string Advice)[] Signals =
    {
        ("跳过测试", @"Skip\s*=", "被跳过的测试会在环境变化后长期静默失效"),
        ("Thread.Sleep", @"Thread\.Sleep\s*\(", "固定等待是慢且不稳定的根因，改用轮询 + 超时"),
        ("短 Task.Delay", @"Task\.Delay\s*\(\s*\d{1,3}\s*\)", "毫秒级延时在 CI 机器上极易不够用"),
        ("轮询重试", @"(?i)\b(retry|eventually|poll|waituntil|spin)\b", "轮询逻辑应有明确超时上限"),
        ("环境变量依赖", @"Environment\.GetEnvironmentVariable", "CI/本地环境变量不同会导致结果不一致"),
        ("时间依赖", @"(?i)(DateTime\.(Now|UtcNow|Today)|Date\.Today)", "依赖当前时间会在跨日/时区时失败"),
        ("随机数", @"(?i)\b(Random\s*\(|Guid\.NewGuid\s*\(\s*\)|Next\w*\s*\(\s*\))", "随机/GUID 顺序依赖会导致偶发失败"),
    };

    public string Name => "test_flakiness_report";
    public string Description =>
        "只读扫描测试代码中的易波动因素（跳过测试、固定等待、轮询、环境/时间/随机依赖），按信号给出改进建议。";

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
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示命中行数（默认 100，最大 2000）" },
            ["max_depth"] = new JsonObject { ["type"] = "integer", ["description"] = "扫描深度上限（默认 8，最大 32）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 100), 1, 2_000);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 8), 1, 32);
        var wanted = ToolArgs.GetStringList(args, "signals") ?? new List<string>();
        var selected = wanted.Count == 0
            ? Signals
            : Signals.Where(s => wanted.Any(tag => string.Equals(tag, s.Label, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (selected.Length == 0)
            throw new ToolException("未识别的信号（可用: " + string.Join(", ", Signals.Select(s => s.Label)) + "）");
        var counts = selected.ToDictionary(s => s.Label, _ => 0, StringComparer.OrdinalIgnoreCase);
        var hits = new List<(string Label, string File, int Line)>();
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!LooksLikeTest(file))
                continue;
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
                    hits.Add((signal.Label, relative, i + 1));
                    break; // 同一行只归入第一个命中的信号
                }
            }
        }
        var total = counts.Values.Sum();
        var output = new StringBuilder();
        output.AppendLine($"测试稳定性信号报告: {total} 处命中");
        foreach (var pair in counts.Where(p => p.Value > 0).OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var advice = selected.First(s => string.Equals(s.Label, pair.Key, StringComparison.OrdinalIgnoreCase)).Advice;
            output.AppendLine($"  {pair.Key}: {pair.Value:N0} — {advice}");
        }
        if (total == 0)
            return output.ToString().TrimEnd();
        output.AppendLine("明细:");
        foreach (var hit in hits.OrderBy(h => h.File, StringComparer.Ordinal).ThenBy(h => h.Line).Take(maxResults))
            output.AppendLine($"  [{hit.Label}] {hit.File}:{hit.Line}");
        if (hits.Count > maxResults)
            output.AppendLine($"…（另有 {hits.Count - maxResults} 处未显示）");
        return output.ToString().TrimEnd();
    }

    /// <summary>按路径与命名判断是否像测试文件。</summary>
    internal static bool LooksLikeTest(string path)
    {
        var lower = path.ToLowerInvariant().Replace('\\', '/');
        if (!lower.EndsWith(".cs", StringComparison.Ordinal))
            return false;
        return lower.Contains("/test", StringComparison.Ordinal)
            || lower.Contains("tests/", StringComparison.Ordinal)
            || lower.Contains(".test.", StringComparison.Ordinal)
            || lower.Contains("tests.cs", StringComparison.Ordinal);
    }
}
