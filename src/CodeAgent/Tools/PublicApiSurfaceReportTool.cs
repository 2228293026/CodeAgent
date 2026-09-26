using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查公开 API 的稳定性风险：可变默认值（列表/字典字面量）、
/// 公开字段、以及没有命名参数的布尔参数（调用方只能靠位置猜含义）。</summary>
public sealed class PublicApiSurfaceReportTool : ITool
{
    public string Name => "public_api_surface_report";
    public string Description =>
        "只读检查公开 API 稳定性：可选参数用了可变默认值、公开字段、只能靠位置传的布尔参数。";

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
        var mutableDefaults = new List<string>();
        var publicFields = new List<string>();
        var boolParams = new List<string>();
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
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                var lineNo = i + 1;
                // 可变对象做默认值：每次调用共享同一个实例。
                // 判据只看两件事——这一行是 **public 成员**，且初值是 new 一个可变容器。
                // 早先用一条大正则把整行串起来，结果被 `{ get; set; }` 里的 ';' 卡住：
                // [^=;()] 走不到 '=' 后面，属性（最常见的写法）反而全部漏掉。
                if (trimmed.StartsWith("public ", StringComparison.Ordinal)
                    && Regex.IsMatch(trimmed, @"=\s*new\s+(?:List|Dictionary|HashSet|Queue|Stack|StringBuilder)\s*(<[^=]*>)?\s*\(\s*\)|=\s*\[\s*\]", RegexOptions.None))
                {
                    var name = Regex.Match(trimmed, @"(?<n>\w+)\s*(?:\{|=|;)").Groups["n"].Value;
                    mutableDefaults.Add($"{relative}:{lineNo} {name} 的默认值是可变对象（每次调用共享同一实例，应改 null / 空数组 / 工厂方法）");
                }
                // 公开字段：外部可随意改，破坏封装
                var f = Regex.Match(trimmed, @"^\s*public\s+(?!static\s+(?:readonly\s+)?class|sealed|abstract|partial|override|virtual|async|event|delegate)(?:static\s+)?(?:readonly\s+)?[\w\.<>\[\],\?]+\s+(?<n>\w+)\s*(=|;)");
                if (f.Success && !trimmed.Contains("get;") && !trimmed.Contains("=>"))
                    publicFields.Add($"{relative}:{lineNo} 公开字段 {f.Groups["n"].Value}（应改成属性）");
                // 布尔参数：调用方 `M(true)` 完全读不出含义
                var b = Regex.Match(trimmed, @"\bpublic\b[^=;]*\s+(?<n>\w+)\s*\((?<args>[^)]*bool[^)]*)\)");
                if (b.Success)
                {
                    var boolCount = Regex.Matches(b.Groups["args"].Value, @"\bbool\b").Count;
                    if (boolCount > 0)
                        boolParams.Add($"{relative}:{lineNo} {b.Groups["n"].Value} 有 {boolCount} 个 bool 参数（调用方只能 `M(true)`，读不出含义）");
                }
            }
        }
        if (files == 0)
            return "公开 API 报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"公开 API 报告: {files} 个 .cs 文件");
        Report(output, "可变默认值", mutableDefaults, maxResults);
        Report(output, "公开字段", publicFields, maxResults);
        Report(output, "只能靠位置传的 bool 参数", boolParams, maxResults);
        if (mutableDefaults.Count == 0 && publicFields.Count == 0 && boolParams.Count == 0)
            output.AppendLine("未发现公开 API 稳定性问题");
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
