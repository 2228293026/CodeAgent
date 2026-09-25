using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查测试断言的强度问题：恒真断言、无消息断言、过度依赖具体文案。</summary>
public sealed class TestAssertionReportTool : ITool
{
    public string Name => "test_assertion_report";
    public string Description =>
        "只读审查测试断言强度：恒真断言（Assert.True(true)）、无消息的空断言、以及断言中硬编码了易变文案。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示每类问题数（默认 30，最大 300）" },
            ["max_depth"] = new JsonObject { ["type"] = "integer", ["description"] = "扫描深度上限（默认 8，最大 32）" },
        },
    };

    private static readonly (string Label, string Pattern, string Advice)[] Signals =
    {
        ("恒真断言", @"Assert\.(True|False)\s*\(\s*(true|false)\s*[,)]", "无论代码怎么改都会通过，等于没有测试"),
        ("空断言", @"Assert\.(True|False)\s*\(\s*[^,)]*\)\s*;", "缺少失败信息，出错时无法定位"),
        ("仅非空断言", @"Assert\.(NotEmpty|NotNull)\s*\(\s*[A-Za-z_][A-Za-z0-9_]*\s*\)", "只验证非空，语义几乎恒真"),
        ("精确文案断言", @"Assert\.(Contains|Equal|StartsWith)\s*\(\s*""[^""]{12,}""", "文案一改就红，建议断言稳定字段"),
        ("TODO 测试", @"\[(Fact|Theory)\s*\(?\s*Skip\s*=", "跳过的测试长期静默失效"),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 30), 1, 300);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 8), 1, 32);
        var files = 0;
        var assertTotal = 0;
        var counts = Signals.ToDictionary(s => s.Label, _ => 0, StringComparer.Ordinal);
        var hits = new List<(string Label, string File, int Line, string Text)>();
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!TestFlakinessReportTool.LooksLikeTest(file))
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
                var line = lines[i].Trim();
                if (line.Length == 0)
                    continue;
                if (line.Contains("Assert.", StringComparison.Ordinal))
                    assertTotal++;
                foreach (var signal in Signals)
                {
                    if (!Regex.IsMatch(line, signal.Pattern))
                        continue;
                    counts[signal.Label]++;
                    hits.Add((signal.Label, relative, i + 1, TextUtil.TruncateLine(line, 90)));
                    break; // 同一行只归入第一个命中的信号，避免重复计数
                }
            }
        }
        if (files == 0)
            return "测试断言报告: 未找到测试文件";
        var total = counts.Values.Sum();
        var output = new StringBuilder();
        output.AppendLine($"测试断言报告: {files} 个测试文件，{assertTotal} 个断言，{total} 处可疑");
        foreach (var pair in counts.Where(p => p.Value > 0).OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            var advice = Signals.First(s => s.Label == pair.Key).Advice;
            output.AppendLine($"  {pair.Key}: {pair.Value} —— {advice}");
        }
        if (total > 0)
        {
            output.AppendLine("明细:");
            foreach (var hit in hits.OrderBy(h => h.File, StringComparer.Ordinal).ThenBy(h => h.Line).Take(maxResults))
                output.AppendLine($"  [{hit.Label}] {hit.File}:{hit.Line}  {hit.Text}");
            if (hits.Count > maxResults)
                output.AppendLine($"…（另有 {hits.Count - maxResults} 处未显示）");
        }
        return output.ToString().TrimEnd();
    }
}
