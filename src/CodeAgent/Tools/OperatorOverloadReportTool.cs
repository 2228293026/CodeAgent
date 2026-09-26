using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查运算符重载：成对运算符只重载了一半（== 却没重载 !=）、
/// 重载 Equals 却漏了 == 或 ==，以及重载了 &gt;/&lt; 却没重载 &lt;=/>=。</summary>
public sealed class OperatorOverloadReportTool : ITool
{
    public string Name => "operator_overload_report";
    public string Description =>
        "只读检查运算符重载：== 与 != 只重载其一、Equals/GetHashCode 与 == 不一致、> 与 >= 只重载其一。";

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
        var halfPair = new List<string>();
        var equalsMismatch = new List<string>();
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
            var text = string.Join("\n", lines);
            foreach (Match m in Regex.Matches(text, @"(?:class|record|struct)\s+(?<name>\w+)"))
            {
                var name = m.Groups["name"].Value;
                var body = TypeBody(lines, m.Index);
                if (body.Length == 0)
                    continue;
                // 成对运算符只重载一半
                foreach (var (a, b, label) in new[] { ("==", "!=", "== / !="), (">", ">=", "> / >="), ("<", "<=", "< / <=") })
                {
                    var esc = Regex.Escape(a);
                    var escB = Regex.Escape(b);
                    var hasA = Regex.IsMatch(body, @"operator\s+" + esc + @"\s*\(");
                    var hasB = Regex.IsMatch(body, @"operator\s+" + escB + @"\s*\(");
                    if (hasA != hasB)
                        halfPair.Add($"{relative} {name} 只重载了 {label} 之一（成对运算符应一起重载）");
                }
                // 重载了 Equals 却没重载 ==（或反过来）
                var hasEquals = Regex.IsMatch(body, @"\b(?:override\s+)?bool\s+Equals\s*\(");
                var hasEqOp = Regex.IsMatch(body, @"operator\s+==\s*\(");
                var hasNeqOp = Regex.IsMatch(body, @"operator\s+!=\s*\(");
                var hasHash = Regex.IsMatch(body, @"override\s+int\s+GetHashCode\s*\(");
                if (hasEquals && !hasEqOp && !hasNeqOp)
                    equalsMismatch.Add($"{relative} {name} 重写 Equals 但未重载 ==/!=（a.Equals(b) 与 a == b 行为不一致）");
                if (hasEqOp && !hasEquals)
                    equalsMismatch.Add($"{relative} {name} 重载 == 但未重写 Equals（与集合的相等语义不一致）");
                if (hasEqOp && hasNeqOp && !hasHash)
                    equalsMismatch.Add($"{relative} {name} 重载 ==/!= 但未见 GetHashCode（在 HashSet/字典键里失效）");
            }
        }
        if (files == 0)
            return "运算符重载报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"运算符重载报告: {files} 个 .cs 文件");
        Report(output, "成对运算符只重载一半", halfPair, maxResults);
        Report(output, "相等语义不一致", equalsMismatch, maxResults);
        if (halfPair.Count == 0 && equalsMismatch.Count == 0)
            output.AppendLine("未发现运算符重载问题");
        return output.ToString().TrimEnd();
    }

    /// <summary>取类型声明的整个大括号体（从声明行往后按深度配平）。</summary>
    private static string TypeBody(string[] lines, int approxLine)
    {
        var open = -1;
        for (var i = approxLine; i < Math.Min(lines.Length, approxLine + 3); i++)
        {
            var at = lines[i].IndexOf('{');
            if (at >= 0)
            {
                open = i;
                break;
            }
        }
        if (open < 0)
            return string.Empty;
        var depth = 0;
        var end = open;
        for (var i = open; i < lines.Length && i < open + 400; i++)
        {
            foreach (var ch in lines[i])
            {
                if (ch == '{') depth++;
                else if (ch == '}') depth--;
            }
            if (depth <= 0)
            {
                end = i;
                break;
            }
        }
        return string.Join("\n", lines.Skip(open).Take(end - open + 1));
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
