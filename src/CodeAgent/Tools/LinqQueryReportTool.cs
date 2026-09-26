using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 LINQ 查询的性能陷阱：在循环里反复查询同一序列、
/// Count() 与 Any() 连续调用（两次枚举）、以及在热路径上用 LINQ 代替直接循环。</summary>
public sealed class LinqQueryReportTool : ITool
{
    public string Name => "linq_query_report";
    public string Description =>
        "只读检查 LINQ 查询陷阱：循环体内重复查询、Count() 后又 Any()/First()（重复枚举）、在循环里用 LINQ 代替简单循环。";

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
        var inLoop = new List<string>();
        var doubleEnumerate = new List<string>();
        var loopLinq = new List<string>();
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
            var blocks = new List<bool>();   // 每层大括号是不是循环体
            var pendingLoop = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var isLoopHeader = Regex.IsMatch(trimmed, @"^\s*(for|foreach|while|do)\b");
                if (isLoopHeader)
                    pendingLoop = true;
                var inside = blocks.Exists(x => x);
                var lineNo = i + 1;

                if (inside)
                {
                    // 循环体里反复查同一个源：每轮迭代都重新枚举
                    var q = Regex.Match(trimmed, @"\b(?<src>[\w\.]+)\.(?<op>Where|Select|OrderBy|First|Any|Count|Sum|Max|Min)\s*\(");
                    if (q.Success)
                        inLoop.Add($"{relative}:{lineNo} 循环体内对 {q.Groups["src"].Value} 反复 {q.Groups["op"].Value}（每轮迭代都重新枚举，应提到循环外）");
                    // 循环里用 LINQ 干一件简单的事
                    if (Regex.IsMatch(trimmed, @"\.(Where|Select|Any|All|Count)\s*\(\s*\w+\s*=>"))
                        loopLinq.Add($"{relative}:{lineNo} 循环体里的 LINQ 只有一条简单谓词（改写成普通 if 可以省掉每次迭代的委托分配）");
                }
                // 先 Count() 再 Any()/First()：把序列枚举两遍
                if (i + 1 < lines.Length)
                {
                    var next = lines[i + 1].Trim();
                    var cnt = Regex.Match(trimmed, @"\b(?<src>[\w\.]+)\.(?<op>Count|Length)\b");
                    var nxt = Regex.Match(next, @"\b(?<src2>[\w\.]+)\.(?<op2>Any|First|FirstOrDefault|Single)\s*\(");
                    if (cnt.Success && nxt.Success && cnt.Groups["src"].Value == nxt.Groups["src2"].Value)
                        doubleEnumerate.Add($"{relative}:{lineNo} 先 {cnt.Groups["src"].Value}.{cnt.Groups["op"].Value} 又 {nxt.Groups["src2"].Value}.{nxt.Groups["op2"].Value}（序列被枚举两遍）");
                }

                var firstIndex = blocks.Count;
                var opened = 0;
                foreach (var ch in line)
                {
                    if (ch == '{') { blocks.Add(pendingLoop && !isLoopHeader); opened++; }
                    else if (ch == '}' && blocks.Count > 0) { blocks.RemoveAt(blocks.Count - 1); }
                }
                if (isLoopHeader && opened > 0 && blocks.Count > firstIndex + opened - 1)
                {
                    blocks[firstIndex + opened - 1] = true;
                    pendingLoop = false;
                }
            }
        }
        if (files == 0)
            return "LINQ 查询报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"LINQ 查询报告: {files} 个 .cs 文件");
        Report(output, "循环体内重复查询", inLoop, maxResults);
        Report(output, "同一序列枚举两遍", doubleEnumerate, maxResults);
        Report(output, "循环体里的单谓词 LINQ", loopLinq, maxResults);
        if (inLoop.Count == 0 && doubleEnumerate.Count == 0 && loopLinq.Count == 0)
            output.AppendLine("未发现 LINQ 查询陷阱");
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
