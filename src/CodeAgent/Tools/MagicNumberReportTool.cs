using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查魔法数字：比较/算术里的裸字面量本应是命名常量、
/// 重复出现的同一字面量、以及可疑的量级（端口号、超时毫秒、缓冲区大小）。</summary>
public sealed class MagicNumberReportTool : ITool
{
    public string Name => "magic_number_report";
    public string Description =>
        "只读检查魔法数字：重复出现的裸字面量、比较/算术里的未命名常量、可疑的端口/超时/缓冲区量级。";

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

    /// <summary>同一字面量至少出现这么多次才算「重复」。</summary>
    internal const int MinOccurrences = 3;

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 30), 1, 300);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        var repeated = new List<string>();
        var inExpressions = new List<string>();
        var suspicious = new List<string>();
        var files = 0;
        // 明显是「有意选值」的数字，不该报：0/1/-1、常用进制换算、测试断言的
        var skipValues = new HashSet<string> { "0", "1", "-1", "2", "2.0", "100", "1000", "1024", "60", "1000" };
        var occurrences = new Dictionary<string, List<string>>(StringComparer.Ordinal);
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
                var line = lines[i].Trim();
                if (line.StartsWith("//", StringComparison.Ordinal) || line.Length == 0)
                    continue;
                foreach (Match m in Regex.Matches(line, @"(?<![\w\.])(?<n>\d{2,})(?![\w\.])"))
                {
                    var value = m.Groups["n"].Value;
                    if (skipValues.Contains(value))
                        continue;
                    if (!occurrences.TryGetValue(value, out var ats))
                    {
                        ats = new List<string>();
                        occurrences[value] = ats;
                    }
                    ats.Add($"{relative}:{i + 1}");
                    // 比较/算术里的裸字面量：应当是命名常量
                    if (Regex.IsMatch(line, @"[<>=!+\-*/%]+\s*" + Regex.Escape(value) + @"\b|\b" + Regex.Escape(value) + @"\s*[<>=!+\-*/%]"))
                        inExpressions.Add($"{relative}:{i + 1} 比较/算术里出现裸字面量 {value}（应提为命名常量）");
                    // 可疑量级：端口、毫秒超时、缓冲区
                    if (int.TryParse(value, out var numeric))
                    {
                        if (numeric is >= 1 and <= 65535 && numeric is >= 1024)
                            suspicious.Add($"{relative}:{i + 1} {value} 像端口号（应命名，如 Port = {value}）");
                        if (numeric is 1000 or 3000 or 5000 or 10000 or 30000 or 60000)
                            suspicious.Add($"{relative}:{i + 1} {value} 像超时毫秒数（应命名，如 TimeoutMs）");
                        if (numeric is 1024 or 4096 or 8192 or 65536 or 1048576)
                            suspicious.Add($"{relative}:{i + 1} {value} 像缓冲区大小（应命名，如 BufferSize）");
                    }
                }
            }
        }
        if (files == 0)
            return "魔法数字报告: 未找到 .cs 文件";
        foreach (var (value, ats) in occurrences)
        {
            if (ats.Count < MinOccurrences)
                continue;
            repeated.Add($"字面量 {value} 出现 {ats.Count} 次（前几处：{string.Join(", ", ats.Take(3))}）——应提为常量");
        }
        var output = new StringBuilder();
        output.AppendLine($"魔法数字报告: {files} 个 .cs 文件");
        Report(output, "重复出现的字面量", repeated, maxResults);
        Report(output, "比较/算术里的裸字面量", inExpressions, maxResults);
        Report(output, "可疑量级（端口/超时/缓冲区）", suspicious, maxResults);
        if (repeated.Count == 0 && inExpressions.Count == 0 && suspicious.Count == 0)
            output.AppendLine("未发现魔法数字问题");
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
