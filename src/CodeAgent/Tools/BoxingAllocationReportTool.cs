using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查字符串拼接之外的高频分配：装箱（object/string.Format 到 int）、
/// 字符串插值出现在循环里、以及 LINQ 里重复 ToList 造成的重复分配。</summary>
public sealed class BoxingAllocationReportTool : ITool
{
    public string Name => "boxing_allocation_report";
    public string Description =>
        "只读检查热路径上的隐式分配：装箱（object 参数接值类型）、循环内的字符串插值、ToList() 后立刻又被枚举。";

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
        var boxing = new List<string>();
        var interpolationInLoop = new List<string>();
        var toListThenEnumerate = new List<string>();
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
            var blocks = new List<bool>();
            var pendingLoop = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var isLoop = Regex.IsMatch(trimmed, @"^\s*(for|foreach|while|do)\b");
                if (isLoop) pendingLoop = true;
                var inside = blocks.Exists(x => x);
                var lineNo = i + 1;

                // 值类型直接传给 object 参数 → 装箱
                var b = Regex.Match(trimmed, @"\bobject\s+\w+\s*=\s*(?<v>-?\d+)\s*;");
                if (b.Success)
                    boxing.Add($"{relative}:{lineNo} object o = {b.Groups["v"].Value}; 发生装箱（应写成 (long){b.Groups["v"].Value} 或直接用值类型）");
                var sb2 = Regex.Match(trimmed, @"String\.Format\s*\(\s*""[^""]*""\s*,\s*(?<v>[A-Za-z_]\w*)\s*\)");
                if (sb2.Success && char.IsUpper(sb2.Groups["v"].Value[0]))
                    boxing.Add($"{relative}:{lineNo} String.Format(\"...\", {sb2.Groups["v"].Value}) 会对值类型装箱（改用插值即可）");

                // 循环里的字符串插值：每轮都新建一个字符串
                if (inside && trimmed.Contains("$\"", StringComparison.Ordinal))
                    interpolationInLoop.Add($"{relative}:{lineNo} 循环体里用字符串插值（每轮都新建字符串，可提到循环外）");

                // ToList() 之后立刻又被 LINQ 遍历
                if (Regex.IsMatch(trimmed, @"\.ToList\s*\(\s*\)\s*;"))
                {
                    var varName = Regex.Match(trimmed, @"(?:var|[\w<>,\s]+)\s+(?<v>\w+)\s*=\s*[\w\.]+\.ToList\s*\(\s*\)\s*;").Groups["v"].Value;
                    if (varName.Length > 0 && i + 1 < lines.Length
                        && Regex.IsMatch(lines[i + 1], $@"\b{Regex.Escape(varName)}\.(Where|Select|OrderBy|Count|Any|First)\s*[\(\.]"))
                        toListThenEnumerate.Add($"{relative}:{lineNo} {varName} 先 ToList() 又立刻被 LINQ 遍历（多了一次中间集合）");
                }

                var firstIndex = blocks.Count;
                var opened = 0;
                foreach (var ch in line)
                {
                    if (ch == '{') { blocks.Add(pendingLoop && !isLoop); opened++; }
                    else if (ch == '}' && blocks.Count > 0) { blocks.RemoveAt(blocks.Count - 1); }
                }
                if (isLoop && opened > 0 && blocks.Count > firstIndex + opened - 1)
                {
                    blocks[firstIndex + opened - 1] = true;
                    pendingLoop = false;
                }
            }
        }
        if (files == 0)
            return "隐式分配报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"隐式分配报告: {files} 个 .cs 文件");
        Report(output, "装箱", boxing, maxResults);
        Report(output, "循环体内的字符串插值", interpolationInLoop, maxResults);
        Report(output, "ToList() 后立刻被枚举", toListThenEnumerate, maxResults);
        if (boxing.Count == 0 && interpolationInLoop.Count == 0 && toListThenEnumerate.Count == 0)
            output.AppendLine("未发现隐式分配问题");
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
