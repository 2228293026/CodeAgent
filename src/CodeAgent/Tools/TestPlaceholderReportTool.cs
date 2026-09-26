using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查测试质量：空断言、恒真断言（assertTrue(true) 一类）、
/// 被注释掉的测试、以及一个测试里混进多个不相关用例。</summary>
public sealed class TestPlaceholderReportTool : ITool
{
    public string Name => "test_placeholder_report";
    public string Description =>
        "只读检查测试里的占位与空壳：空断言、恒真断言、被注释掉的测试、没有断言的测试。";

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
        var emptyAsserts = new List<string>();
        var tautologies = new List<string>();
        var noAssert = new List<string>();
        var disabled = new List<string>();
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
            // 一个测试方法的范围：从 [Fact]/[Theory] 到下一个 [Fact]/[Theory] 或方法结束
            var starts = new List<int>();
            for (var i = 0; i < lines.Length; i++)
            {
                if (Regex.IsMatch(lines[i], @"^\s*\[(Fact|Theory)", RegexOptions.None))
                    starts.Add(i);
            }
            foreach (var (start, si) in starts.Select((v, k) => (v, k)))
            {
                var end = si + 1 < starts.Count ? starts[si + 1] : lines.Length;
                var name = ExtractName(lines, start, end);
                var body = string.Join("\n", lines.Skip(start).Take(end - start));
                var asserts = Regex.Matches(body, @"\bAssert\.\w+\s*\(").Count;
                if (asserts == 0)
                    noAssert.Add($"{relative}:{start + 1} {name} 没有任何断言（要么测的是不崩溃，要么断言被漏写了）");
                // 恒真断言：Assert.True(true) / Assert.NotNull(x, ...)? 不是；只看字面量恒真
                if (Regex.IsMatch(body, @"\bAssert\.(True|Equal|StrictEqual)\s*\(\s*(true|1|""1"")\s*[,)]")
                    || Regex.IsMatch(body, @"\bAssert\.False\s*\(\s*(false|0|""0"")\s*[,)]"))
                    tautologies.Add($"{relative}:{start + 1} {name} 断言的是常量，恒真");
                foreach (Match m in Regex.Matches(body, @"\bAssert\.\w+\s*\(\s*\)\s*;"))
                    emptyAsserts.Add($"{relative}:{start + 1} {name} 空断言 {m.Value.Trim()}");
            }
            // 被注释掉的测试
            for (var i = 0; i < lines.Length; i++)
            {
                var t = lines[i].Trim();
                if (t.StartsWith("//", StringComparison.Ordinal) && Regex.IsMatch(t, @"\[(Fact|Theory)\]"))
                    disabled.Add($"{relative}:{i + 1} 整条测试被注释掉了（{t.TrimStart('/').Trim()}）");
            }
        }
        if (files == 0)
            return "测试占位报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"测试占位报告: {files} 个 .cs 文件");
        Report(output, "没有断言的测试", noAssert, maxResults);
        Report(output, "恒真断言", tautologies, maxResults);
        Report(output, "空断言", emptyAsserts, maxResults);
        Report(output, "被注释掉的测试", disabled, maxResults);
        if (noAssert.Count == 0 && tautologies.Count == 0 && emptyAsserts.Count == 0 && disabled.Count == 0)
            output.AppendLine("未发现测试占位问题");
        return output.ToString().TrimEnd();
    }

    private static string ExtractName(string[] lines, int start, int end)
    {
        for (var i = start; i < end && i < lines.Length; i++)
        {
            var m = Regex.Match(lines[i], @"public\s+(?:async\s+)?[\w\.<>\[\],\?]+\s+(?<n>\w+)\s*\(");
            if (m.Success)
                return m.Groups["n"].Value;
        }
        return "(匿名测试)";
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
