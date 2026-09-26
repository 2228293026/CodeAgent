using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查时间/日期运算陷阱：DateTime.Now 与 UtcNow 混用、
/// 时区被当成本地、以及用 Kind 丢失的 DateTime 做差。</summary>
public sealed class TimeZoneReportTool : ITool
{
    public string Name => "time_zone_report";
    public string Description =>
        "只读检查时区陷阱：DateTime.Now 与 UtcNow 混用、Kind 被重置后做时区换算、DateTime 存时刻而不是日期。";

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
        var mixedNow = new List<string>();
        var kindReset = new List<string>();
        var dateAsTimestamp = new List<string>();
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
            // 同一表达式里 Now 和 UtcNow 混用
            foreach (Match m in Regex.Matches(text, @"(?i)(?<expr>[^\n;{}]*(?:DateTime|DateTimeOffset)\.Now[^\n;{}]*(?:DateTime|DateTimeOffset)\.UtcNow[^\n;{}]*|(?:DateTime|DateTimeOffset)\.UtcNow[^\n;{}]*(?:DateTime|DateTimeOffset)\.Now[^\n;{}]*)"))
            {
                if (m.Success)
                    mixedNow.Add($"{relative} 同一表达式里混用 Now 与 UtcNow（相减会得到一个看似合理的错误值）");
            }
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;
                // 把 DateTime 换成新的 DateTime（Kind 丢失）再做 ToLocalTime
                if (Regex.IsMatch(trimmed, @"(?i)(?:DateTime\.Parse|ToString\(\))\s*\(")
                    && Regex.IsMatch(trimmed, @"(?i)To(?:Local|Universal)Time"))
                    kindReset.Add($"{relative}:{lineNo} 先经 Parse/ToString 再做时区换算，Kind 已是 Unspecified——换算结果依赖机器时区");
                // 用 DateTime 存时刻
                if (Regex.IsMatch(trimmed, @"\b(?:createdAt|updatedAt|timestamp|expiresAt|occurredAt)\s*[:=]\s*(?:DateTime\b(?!Offset))")
                    || Regex.IsMatch(trimmed, @"(?i)Dictionary<[^>]*string[^>]*,\s*DateTime>"))
                    dateAsTimestamp.Add($"{relative}:{lineNo} 用 DateTime（无 Kind/无时区）存时刻——跨时区后含义会漂移，应存 UTC 或 DateTimeOffset");
            }
        }
        if (files == 0)
            return "时区报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"时区报告: {files} 个 .cs 文件");
        Report(output, "Now/UtcNow 混用", mixedNow, maxResults);
        Report(output, "Kind 丢失后换算", kindReset, maxResults);
        Report(output, "用 DateTime 存时刻", dateAsTimestamp, maxResults);
        if (mixedNow.Count == 0 && kindReset.Count == 0 && dateAsTimestamp.Count == 0)
            output.AppendLine("未发现时区问题");
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
