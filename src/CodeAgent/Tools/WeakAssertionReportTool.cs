using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查单元测试自身的质量陷阱：没有断言、断言恒真、
/// 捕获所有异常后仍通过、测试之间共享可变静态状态。</summary>
public sealed class WeakAssertionReportTool : ITool
{
    public string Name => "weak_assertion_report";
    public string Description =>
        "只读检查弱断言：测试方法里没有 Assert、Assert.True(true) 恒真、catch 里吞掉异常仍算通过、测试类之间共享可变静态状态。";

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
        var noAssert = new List<string>();
        var alwaysTrue = new List<string>();
        var swallowInTest = new List<string>();
        var sharedStatic = new List<string>();
        var files = 0;
        // 静态可变字段：跨测试实例共享，xUnit 每次新建类实例也躲不掉
        var mutableStatics = new HashSet<string>(StringComparer.Ordinal);
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
            var text = string.Join("\n", lines);
            foreach (Match m in Regex.Matches(text, @"\bstatic\s+(?:readonly\s+)?(?!\w+\s+\w+\s*\()[A-Za-z_][\w<>\[\],\.]*\s+(?<n>\w+)\s*(?:=|;)"))
            {
                if (!m.Groups["n"].Value.StartsWith("const", StringComparison.Ordinal))
                    mutableStatics.Add(m.Groups["n"].Value);
            }
            var inTestFile = Regex.IsMatch(text, @"\b(?:class|record)\s+\w*Tests?\b") || text.Contains("Assert.");
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;
                if (Regex.IsMatch(trimmed, @"Assert\.(?:True|False)\s*\(\s*(?:true|false)\s*\)"))
                    alwaysTrue.Add($"{relative}:{lineNo} {trimmed} 恒真断言——无论实现怎么坏都会通过");
                // 测试方法：带 Fact/Theory 标记的下一个方法
                if (Regex.IsMatch(trimmed, @"\[(?:Fact|Theory|InlineData)"))
                {
                    var sigIdx = -1;
                    for (var j = i + 1; j < Math.Min(i + 6, lines.Length); j++)
                    {
                        if (Regex.IsMatch(lines[j].Trim(), @"\b(?:public|private|internal)\s+(?:async\s+)?[\w<>\[\],\?]+\s+\w+\s*\("))
                        {
                            sigIdx = j;
                            break;
                        }
                    }
                    if (sigIdx >= 0)
                    {
                        var name = Regex.Match(lines[sigIdx].Trim(), @"\b(\w+)\s*\(").Groups[1].Value;
                        var body = new List<string>();
                        var d2 = 0;
                        var started = false;
                        for (var k = sigIdx + 1; k < lines.Length && k < sigIdx + 80; k++)
                        {
                            foreach (var ch in lines[k])
                            {
                                if (ch == '{') { d2++; started = true; }
                                else if (ch == '}') d2--;
                            }
                            body.Add(lines[k]);
                            if (started && d2 <= 0) break;
                        }
                        var bt = string.Join("\n", body);
                        if (started && !Regex.IsMatch(bt, @"\bAssert\.|\bAssertAsync|Should\(\)|FluentAssertions"))
                            noAssert.Add($"{relative}:{sigIdx + 1} {name}() 里没有任何断言——它永远不会失败（应至少断言返回值或副作用）");
                        var c = Regex.Match(bt, @"(?s)catch\s*(?:\([^)]*\))?\s*\{(.*?)\}");
                        if (c.Success && !Regex.IsMatch(c.Groups[1].Value, @"(?i)(Assert|Fail|throw)"))
                            swallowInTest.Add($"{relative}:{sigIdx + 1} {name}() 的 catch 既不 Assert 也不 Fail——测试里的异常被当成通过");
                    }
                }
                if (inTestFile)
                {
                    foreach (var st in mutableStatics)
                    {
                        if (Regex.IsMatch(trimmed, $@"\b{Regex.Escape(st)}\s*(?:\+\+|--|\.\w+\(|\[)"))
                        {
                            sharedStatic.Add($"{relative}:{lineNo} 测试代码里改动了静态可变状态 {st}（xUnit 跨测试类并行跑，顺序不可靠）");
                            break;
                        }
                    }
                }
            }
        }
        if (files == 0)
            return "弱断言报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"弱断言报告: {files} 个 .cs 文件");
        Report(output, "测试里没有断言", noAssert, maxResults);
        Report(output, "恒真断言", alwaysTrue, maxResults);
        Report(output, "测试内吞掉异常", swallowInTest, maxResults);
        Report(output, "测试共享可变静态状态", sharedStatic, maxResults);
        if (noAssert.Count == 0 && alwaysTrue.Count == 0 && swallowInTest.Count == 0 && sharedStatic.Count == 0)
            output.AppendLine("未发现弱断言");
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
