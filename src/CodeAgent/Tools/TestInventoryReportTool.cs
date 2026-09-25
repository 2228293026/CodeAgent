using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读发现测试文件并按测试框架和断言习惯汇总。</summary>
public sealed class TestInventoryReportTool : ITool
{
    public string Name => "test_inventory_report";
    public string Description =>
        "只读发现常见测试文件，按 xUnit、pytest、Jest/Node、Go、Rust 等框架汇总测试数量和断言线索。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "工作区内目录，默认根目录" },
            ["depth"] = new JsonObject { ["type"] = "integer", ["description"] = "递归深度（默认 8，最大 30）" },
            ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含隐藏目录（默认 false）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多扫描文件数（默认 10000，最大 100000）" },
            ["max_paths"] = new JsonObject { ["type"] = "integer", ["description"] = "最多列出的测试文件数（默认 100，最大 1000）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var depth = Math.Clamp(ToolArgs.GetInt(args, "depth", 8), 0, 30);
        var includeHidden = ToolArgs.GetBool(args, "include_hidden", false);
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", 10_000), 1, 100_000);
        var maxPaths = Math.Clamp(ToolArgs.GetInt(args, "max_paths", 100), 1, 1_000);
        var candidates = new List<string>();
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, depth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!includeHidden && SkipDirs.IsHiddenPath(file, root))
                continue;
            if (IsLikelyTestPath(file, root))
                candidates.Add(file);
            if (candidates.Count >= maxFiles)
                break;
        }
        var frameworks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var assertions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var details = new List<string>();
        foreach (var file in candidates)
        {
            ct.ThrowIfCancellationRequested();
            string text;
            try
            {
                if (new FileInfo(file).Length > 1_048_576)
                    continue;
                text = await File.ReadAllTextAsync(file, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var framework = DetectFramework(text);
            if (framework is not null)
                frameworks[framework] = frameworks.GetValueOrDefault(framework) + 1;
            var assertionCount = CountMatches(text, "Assert", "assert", "expect(", "should", "assert_that");
            if (assertionCount > 0)
                assertions[framework ?? "未识别"] = assertions.GetValueOrDefault(framework ?? "未识别") + assertionCount;
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            details.Add($"{relative} [{(framework ?? "未识别")}]，断言线索 {assertionCount:N0}");
        }
        var output = new StringBuilder();
        output.AppendLine($"测试清单报告: {candidates.Count:N0} 个候选文件，已分析 {details.Count:N0} 个");
        foreach (var pair in frameworks.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            output.AppendLine($"  {pair.Key}: {pair.Value:N0} 个文件");
        output.AppendLine("断言线索:");
        foreach (var pair in assertions.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            output.AppendLine($"  {pair.Key}: {pair.Value:N0}");
        output.AppendLine("文件:");
        foreach (var detail in details.Take(maxPaths))
            output.AppendLine($"  {detail}");
        if (details.Count > maxPaths)
            output.AppendLine($"…（另有 {details.Count - maxPaths} 个文件未显示）");
        return output.ToString().TrimEnd();
    }

    private static bool IsLikelyTestPath(string file, string root)
    {
        var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
        var lower = relative.ToLowerInvariant();
        var name = Path.GetFileName(lower);
        return lower.Contains("/test/") || lower.Contains("/tests/") || lower.Contains("/spec/")
            || name.StartsWith("test_") || name.EndsWith("_test.py") || name.EndsWith("_test.go")
            || name.Contains(".test.") || name.Contains(".spec.") || name.EndsWith("tests.cs") || name.EndsWith("test.cs");
    }

    private static string? DetectFramework(string text)
    {
        if (text.Contains("[Fact]", StringComparison.OrdinalIgnoreCase) || text.Contains("Xunit", StringComparison.OrdinalIgnoreCase))
            return "xUnit";
        if (text.Contains("pytest", StringComparison.OrdinalIgnoreCase) || text.Contains("def test_", StringComparison.OrdinalIgnoreCase))
            return "pytest";
        if (text.Contains("describe(", StringComparison.Ordinal) || text.Contains("it(", StringComparison.Ordinal) || text.Contains("test(", StringComparison.Ordinal))
            return "Node/Jest";
        if (text.Contains("#[test]", StringComparison.Ordinal) || text.Contains("#[cfg(test)]", StringComparison.Ordinal))
            return "Rust";
        if (text.Contains("testing.T", StringComparison.Ordinal) || text.Contains("func Test", StringComparison.Ordinal))
            return "Go";
        return null;
    }

    private static int CountMatches(string text, params string[] needles)
    {
        var count = 0;
        foreach (var needle in needles)
        {
            var index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                index += needle.Length;
            }
        }
        return count;
    }
}
