using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查配置/常量里的单位陷阱：毫秒与秒混用、超时值没有后缀、
/// 以及同一份配置里同一语义出现两种单位（30 与 30000 并存）。</summary>
public sealed class TimeUnitConfusionReportTool : ITool
{
    public string Name => "time_unit_confusion_report";
    public string Description =>
        "只读检查时间单位的陷阱：Timeout/Interval/Delay 名字配毫秒值、没带单位的魔法超时、同语义混用秒与毫秒。";

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

    /// <summary>常见的"毫秒"误用值（秒的数字被当成毫秒填进去，等于超时被缩短 1000 倍）。</summary>
    internal static readonly int[] SuspiciousMillisecondValues = { 5, 10, 15, 20, 30, 60, 90, 120 };

    /// <summary>常见"秒"值（看起来像毫秒，实际是秒）。</summary>
    internal static readonly int[] SuspiciousSecondValues = { 30000, 60000, 300000, 600000, 900000 };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 30), 1, 300);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        var msNamedSeconds = new List<string>();
        var sNamedMillis = new List<string>();
        var unitless = new List<string>();
        var files = 0;
        // 名字带 Timeout/Interval/Delay/Idle 的成员 → 实际单位不一致
        var timeMembers = new Dictionary<string, (string Unit, int Line)>(StringComparer.Ordinal);
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
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;
                // 名字里明确写了单位，赋值却用了另一种单位。
                // 类型在名字**前面**（`int Timeout = 5000`）——早先把类型组放在名字后面，
                // 于是"没有单位的裸整数"这一类永远匹配不到。
                var m = Regex.Match(trimmed, @"(?<type>TimeSpan|int|long|double)?\s*(?<n>\w*(?:Timeout|Interval|Delay|Idle|Backoff)\w*)\s*=\s*(?<v>\d+)");
                if (m.Success)
                {
                    var name = m.Groups["n"].Value;
                    var value = int.Parse(m.Groups["v"].Value);
                    if (name.EndsWith("Ms", StringComparison.Ordinal) || name.EndsWith("Millis", StringComparison.Ordinal))
                    {
                        if (Array.IndexOf(SuspiciousMillisecondValues, value) >= 0)
                            msNamedSeconds.Add($"{relative}:{lineNo} {name} = {value}（名字说毫秒，值像秒——超时被缩短了 {1000 / Math.Max(1, value)} 倍）");
                    }
                    else if (name.EndsWith("Seconds", StringComparison.Ordinal) || name.EndsWith("Secs", StringComparison.Ordinal))
                    {
                        if (Array.IndexOf(SuspiciousSecondValues, value) >= 0)
                            sNamedMillis.Add($"{relative}:{lineNo} {name} = {value}（名字说秒，值像毫秒——超时被拉长了 {value / 30} 倍）");
                    }
                    else if (m.Groups["type"].Success && (m.Groups["type"].Value == "int" || m.Groups["type"].Value == "long"))
                        unitless.Add($"{relative}:{lineNo} {name} 是裸整数且名字没带单位（读代码时无从判断是秒还是毫秒）");
                    timeMembers[name] = (m.Groups["type"].Success ? m.Groups["type"].Value : "?", lineNo);
                }
                // TimeSpan.FromSeconds(30) 与 FromMilliseconds(30000) 混用同一语义。
                // 只有**同一行出现 Ms 命名**时才报：光看 FromSeconds(30) 是正常的，
                // 必须有"名字说毫秒"的旁证才能判定为单位写反了。
                var hasMsNaming = Regex.IsMatch(trimmed, @"\w(Ms|Millis)\b");
                var fs = Regex.Match(trimmed, @"TimeSpan\.FromSeconds\s*\(\s*(?<v>\d+)\s*\)");
                var fm = Regex.Match(trimmed, @"TimeSpan\.FromMilliseconds\s*\(\s*(?<v>\d+)\s*\)");
                if (hasMsNaming && fs.Success && Array.IndexOf(SuspiciousMillisecondValues, int.Parse(fs.Groups["v"].Value)) >= 0)
                    msNamedSeconds.Add($"{relative}:{lineNo} TimeSpan.FromSeconds({fs.Groups["v"].Value}) 却配 *Ms 命名（多半是把秒当毫秒写）");
                if (hasMsNaming && fm.Success && Array.IndexOf(SuspiciousSecondValues, int.Parse(fm.Groups["v"].Value)) >= 0)
                    sNamedMillis.Add($"{relative}:{lineNo} TimeSpan.FromMilliseconds({fm.Groups["v"].Value}) 却配 *Ms 命名（看着像把毫秒当秒写）");
            }
        }
        if (files == 0)
            return "时间单位报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"时间单位报告: {files} 个 .cs 文件");
        Report(output, "名字说毫秒、值像秒", msNamedSeconds, maxResults);
        Report(output, "名字说秒、值像毫秒", sNamedMillis, maxResults);
        Report(output, "超时值没有单位", unitless, maxResults);
        if (msNamedSeconds.Count == 0 && sNamedMillis.Count == 0 && unitless.Count == 0)
            output.AppendLine("未发现时间单位陷阱");
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
