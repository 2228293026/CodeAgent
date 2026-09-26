using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查事件订阅：+= 订阅从未 -= 解绑、lambda 订阅无法解绑、
/// 以及静态事件持有类实例导致的存活泄漏。</summary>
public sealed class EventSubscriptionReportTool : ITool
{
    public string Name => "event_subscription_report";
    public string Description =>
        "只读检查事件订阅：+= 订阅没有对应 -= 解绑、lambda 订阅无法解绑、静态事件持有实例引用。";

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
        var noUnsubscribe = new List<string>();
        var lambdaSubscribe = new List<string>();
        var staticEvent = new List<string>();
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
                // += 订阅。不能要求订阅出现在行首——`void M(Source s) => s.Changed += H;`
                // 这类表达式体成员前面还有一堆内容，锚定行首会一条都匹配不上。
                var sub = Regex.Match(trimmed, @"(?<name>[A-Za-z_][\w\.]*)\s*\+=\s*");
                if (sub.Success)
                {
                    var name = sub.Groups["name"].Value;
                    // 解绑写在事件名**之后**：`s.Changed -= OnChanged;`
                    var unsub = new Regex(Regex.Escape(name) + @"\s*-=");
                    if (!lines.Any(l => unsub.IsMatch(l)))
                        noUnsubscribe.Add($"{relative}:{lineNo} 订阅 {name} += 后全文未见 -= 解绑（事件源会一直持有处理器）");
                    if (Regex.IsMatch(trimmed, @"\+=\s*\(?\s*[\w\s]*\s*\)?\s*=>"))
                        lambdaSubscribe.Add($"{relative}:{lineNo} 用 lambda/临时委托订阅（后续无法用 -= 解绑）");
                }
                // 静态事件：长期存活，会把订阅者实例一起钉住
                if (Regex.IsMatch(trimmed, @"\bstatic\s+.*\bevent\b"))
                    staticEvent.Add($"{relative}:{lineNo} 静态事件（生命周期长于订阅者，易造成对象无法回收）");
            }
        }
        if (files == 0)
            return "事件订阅报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"事件订阅报告: {files} 个 .cs 文件");
        Report(output, "缺少解绑", noUnsubscribe, maxResults);
        Report(output, "lambda 订阅", lambdaSubscribe, maxResults);
        Report(output, "静态事件", staticEvent, maxResults);
        if (noUnsubscribe.Count == 0 && lambdaSubscribe.Count == 0 && staticEvent.Count == 0)
            output.AppendLine("未发现事件订阅问题");
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
