using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查实参传递安全：入参直接拼进文件路径、公开方法用入参做索引/截取却没有校验、
/// 以及启动外部进程时用字符串插值拼接参数（应改用 ArgumentList）。</summary>
/// 与 <see cref="InputValidationReportTool"/> 分工：后者查环境变量/命令行/配置读入，
/// 本工具查**方法之间传递的实参**如何被使用。</summary>
public sealed class ArgumentSafetyReportTool : ITool
{
    public string Name => "argument_safety_report";
    public string Description =>
        "只读检查实参传递安全：入参直接拼进文件路径、公开方法用入参索引/截取却无校验、启动进程用字符串插值拼参数。";

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
        var unvalidatedPath = new List<string>();
        var missingGuard = new List<string>();
        var unescapedCommand = new List<string>();
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
                var lineNo = i + 1;
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                // 入参直接当文件路径用，且附近没有校验
                if (Regex.IsMatch(trimmed, @"(File|Directory)\.(ReadAll|WriteAll|Open|Create|Delete|Exists|Enumerate)[A-Za-z]*\s*\(\s*\w*path"))
                {
                    var nearby = lines.Skip(Math.Max(0, i - 6)).Take(10).Any(l =>
                        l.Contains("ArgumentException", StringComparison.Ordinal)
                        || l.Contains("IsNullOrWhiteSpace", StringComparison.Ordinal)
                        || l.Contains("GetFullPath", StringComparison.Ordinal)
                        || l.Contains("Validate", StringComparison.Ordinal));
                    if (!nearby)
                        unvalidatedPath.Add($"{relative}:{lineNo} 入参直接用于文件路径且附近未见校验（{TextUtil.TruncateLine(trimmed, 40)}）");
                }
                // 公开方法用入参做索引/截取却未见校验
                if (Regex.IsMatch(trimmed, @"\b(public|internal)\s+[\w<>\[\],\s\?]+\s+\w+\s*\([^)]*\b(string|int|long)\s+\w+")
                    && Regex.IsMatch(trimmed, @"\w+\s*\[\s*\w+\s*\]|Substring\s*\("))
                {
                    var body = string.Join("\n", lines.Skip(i).Take(12));
                    var guarded = body.Contains("ArgumentException", StringComparison.Ordinal)
                        || body.Contains("ArgumentNullException", StringComparison.Ordinal)
                        || body.Contains("IsNullOrEmpty", StringComparison.Ordinal)
                        || body.Contains("ArgumentOutOfRange", StringComparison.Ordinal)
                        || body.Contains("TryGetValue", StringComparison.Ordinal);
                    if (!guarded)
                        missingGuard.Add($"{relative}:{lineNo} 公开方法用入参做索引/截取但未见校验");
                }
                // 启动进程时用字符串插值拼参数。
                // ProcessStartInfo 与 Arguments 赋值常分处两行（对象初始化器），必须看上下文行，
                // 只在单行里同时找会永远匹配不上。
                var context = string.Join("\n", lines.Skip(Math.Max(0, i - 6)).Take(14));
                if (Regex.IsMatch(context, @"(ProcessStartInfo|Process\.Start)")
                    && Regex.IsMatch(trimmed, @"(Arguments|CommandLine)\s*=")
                    && Regex.IsMatch(trimmed, @"\$""[^""]*\b\w*(arg|input|query|path|name)\w*\b")
                    && !context.Contains("ArgumentList", StringComparison.Ordinal))
                    unescapedCommand.Add($"{relative}:{lineNo} 命令参数用字符串插值拼接（应改用 ArgumentList）");
            }
        }
        if (files == 0)
            return "实参安全报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"实参安全报告: {files} 个 .cs 文件");
        Report(output, "入参直用路径", unvalidatedPath, maxResults);
        Report(output, "缺参数校验", missingGuard, maxResults);
        Report(output, "命令未用 ArgumentList", unescapedCommand, maxResults);
        if (unescapedCommand.Count == 0 && missingGuard.Count == 0 && unvalidatedPath.Count == 0)
            output.AppendLine("未发现实参安全问题");
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
