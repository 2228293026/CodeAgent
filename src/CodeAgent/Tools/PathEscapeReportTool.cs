using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查文件访问权限的宽松写法：路径拼接未做根目录校验、
/// 允许 .. 逃逸、以及在同一处混用"工作区内"与"任意路径"两种假设。</summary>
public sealed class PathEscapeReportTool : ITool
{
    public string Name => "path_escape_report";
    public string Description =>
        "只读检查路径逃逸：Path.Combine 后没有校验是否仍在根目录内、允许 .. 上跳、路径未规范化就做前缀判断。";

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
        var unvalidatedCombine = new List<string>();
        var parentTraversal = new List<string>();
        var stringPrefixCheck = new List<string>();
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
                // Path.Combine(...) 之后没有 GetFullPath / 根目录校验。
                // 匹配**调用点**而不是赋值：`return Path.Combine(root, rel);`
                // 这种最常见的写法根本没有左值，只认赋值会漏掉一大半。
                var comb = Regex.Match(trimmed, @"(?:(?:var|[\w\.]+)\s+(?<v>\w+)\s*=\s*)?Path\.Combine\s*\(");
                if (comb.Success)
                {
                    var after = string.Join("\n", lines.Skip(i).Take(4));
                    var guarded = Regex.IsMatch(after, @"GetFullPath\s*\(")
                        || Regex.IsMatch(after, @"IsPathWithin\s*\(")
                        || Regex.IsMatch(after, @"Normalize\s*\(")
                        || Regex.IsMatch(after, @"\.\.?(?:[\\/]([^;]*))?[""']\s*\)");
                    if (!guarded)
                    {
                        var v = comb.Groups["v"].Success ? comb.Groups["v"].Value : "返回值";
                        unvalidatedCombine.Add($"{relative}:{lineNo} {v} = Path.Combine(...) 之后没做 GetFullPath/根目录校验（输入含 .. 时会逃出工作区）");
                    }
                }
                // 直接拼 ".." 上跳
                if (Regex.IsMatch(trimmed, @"[\\/.]\s*\.\.[\\/]") || trimmed.Contains("..", StringComparison.Ordinal)
                    && Regex.IsMatch(trimmed, @"(?i)(root|dir|base|workspace)\s*\+"))
                    parentTraversal.Add($"{relative}:{lineNo} 路径里出现 .. 上跳，且附近没有看到规范化调用");
                // 用字符串前缀判断"是否在目录下"——大小写与分隔符不敏感。
                // 要同时认普通串与逐字串（`@"D:\work"`）：Windows 路径**几乎总是**
                // 写成逐字串，只认一种就等于大半场景匹配不到。
                var prefix = Regex.Match(trimmed, @"(?<v>[\w\.]+)\.(?:StartsWith|Contains)\s*\(\s*@?""(?<p>[^""]*)""");
                if (prefix.Success && Regex.IsMatch(prefix.Groups["p"].Value, @"[\\/]"))
                    stringPrefixCheck.Add($"{relative}:{lineNo} 用字符串前缀判断路径归属（StartsWith 会被 ../root2 骗过，且在大小写不敏感的文件系统上不成立）");
            }
        }
        if (files == 0)
            return "路径逃逸报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"路径逃逸报告: {files} 个 .cs 文件");
        Report(output, "Combine 后未校验根目录", unvalidatedCombine, maxResults);
        Report(output, "疑似 .. 上跳", parentTraversal, maxResults);
        Report(output, "用字符串前缀判断路径归属", stringPrefixCheck, maxResults);
        if (unvalidatedCombine.Count == 0 && parentTraversal.Count == 0 && stringPrefixCheck.Count == 0)
            output.AppendLine("未发现路径逃逸风险");
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
