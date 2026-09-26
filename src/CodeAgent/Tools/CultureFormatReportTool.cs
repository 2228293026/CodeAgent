using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查区域性设置的陷阱：硬编码日期/数字格式（未用 InvariantCulture）、
/// 解析时用了当前区域、ToUpper/ToLower 未指定文化导致土耳其语 i 问题。</summary>
public sealed class CultureFormatReportTool : ITool
{
    public string Name => "culture_format_report";
    public string Description =>
        "只读检查区域性设置陷阱：ToString/Parse 未指定 CultureInfo、ToUpper/ToLower 未用不变文化、字符串比较用了 == 而非 Ordinal。";

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
        var toStringNoCulture = new List<string>();
        var parseNoCulture = new List<string>();
        var casedMethods = new List<string>();
        var ordinalCompare = new List<string>();
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
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;
                var code = trimmed;

                // 数值/日期 ToString 未指定文化：同一份数据在不同机器上格式不同
                var ts = Regex.Match(code, @"\b(?<v>[A-Za-z_][\w\.]*)\.ToString\s*\(\s*\)");
                if (ts.Success && !ts.Groups["v"].Value.EndsWith("AsSpan", StringComparison.Ordinal))
                {
                    var call = code.Contains("CultureInfo", StringComparison.Ordinal)
                        || code.Contains("InvariantGlobalization", StringComparison.Ordinal);
                    if (!call)
                        toStringNoCulture.Add($"{relative}:{lineNo} {ts.Groups["v"].Value}.ToString() 没指定文化（同一数字在不同区域格式不同）");
                }
                // Parse/TryParse 未指定文化
                var ps = Regex.Match(code, @"\b(?<t>int|double|decimal|DateTime|bool|float|long|short)\.(?:Try)?Parse\s*\(\s*(?<v>[A-Za-z_][\w\.]*)\s*\)");
                if (ps.Success)
                    parseNoCulture.Add($"{relative}:{lineNo} {ps.Groups["t"].Value}.Parse({ps.Groups["v"].Value}) 用的是当前区域（读配置文件/命令行参数时会随机器语言变化）");
                // ToUpper/ToLower 未用不变文化：土耳其语 i→İ
                var cm = Regex.Match(code, @"\.(?<m>ToUpper|ToLower)\s*\(\s*\)");
                if (cm.Success)
                    casedMethods.Add($"{relative}:{lineNo} .{cm.Groups["m"].Value}() 用了当前区域（土耳其语环境下 i 会变成 İ，标识符比较随之出错；应加 InvariantCulture）");
                // 标识符/路径比较用 == 而不是 Ordinal
                var eq = Regex.Match(code, @"\b(?<a>[A-Za-z_]\w*)\s*==\s*(?<b>""[^""]*""|[A-Za-z_]\w*)\s*;?$");
                if (eq.Success && (eq.Groups["b"].Value.StartsWith('"') || eq.Groups["b"].Value.StartsWith("\"", StringComparison.Ordinal)))
                    ordinalCompare.Add($"{relative}:{lineNo} 用 == 比较字符串（应 string.Equals(..., StringComparison.Ordinal)）");
            }
        }
        if (files == 0)
            return "区域设置报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"区域设置报告: {files} 个 .cs 文件");
        Report(output, "ToString 未指定文化", toStringNoCulture, maxResults);
        Report(output, "Parse 未指定文化", parseNoCulture, maxResults);
        Report(output, "ToUpper/ToLower 未用不变文化", casedMethods, maxResults);
        Report(output, "字符串 == 比较", ordinalCompare, maxResults);
        if (toStringNoCulture.Count == 0 && parseNoCulture.Count == 0 && casedMethods.Count == 0 && ordinalCompare.Count == 0)
            output.AppendLine("未发现区域设置陷阱");
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
