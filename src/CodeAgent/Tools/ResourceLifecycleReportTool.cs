using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查资源释放：using/await using 缺失、事件处理器未退订、以及终结器依赖。</summary>
public sealed class ResourceLifecycleReportTool : ITool
{
    public string Name => "resource_lifecycle_report";
    public string Description =>
        "只读检查资源生命周期：IDisposable 未 using/await using、事件订阅未退订、以及定义了终结器却未走 Dispose 模式。";

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
        var undisposed = new List<string>();
        var missingUnsubscribe = new List<string>();
        var finalizerOnly = new List<string>();
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
                // new 出可释放对象却直接赋给字段/局部变量，没有 using。
                // 类型名可能带命名空间限定（new System.IO.MemoryStream()）。
                if (Regex.IsMatch(trimmed, @"=\s*new\s+(?:[\w.]+\.)?(FileStream|StreamReader|StreamWriter|MemoryStream|Socket|TcpClient|SqlConnection|HttpClient|CancellationTokenSource|SemaphoreSlim|Process)\s*\(")
                    && !trimmed.Contains("using", StringComparison.Ordinal)
                    && !lines.Skip(i).Take(6).Any(l => l.Contains("Dispose", StringComparison.Ordinal) || l.Contains("using ", StringComparison.Ordinal)))
                    undisposed.Add($"{relative}:{lineNo} 创建可释放对象但未见 using/Dispose（{TextUtil.TruncateLine(trimmed, 50)}）");
                // 订阅事件却没有对应的退订：目标对象会一直持有订阅者
                var subscribe = Regex.Match(trimmed, @"\.\s*(\w+)\s*\+=");
                if (subscribe.Success)
                {
                    var eventName = subscribe.Groups[1].Value;
                    // 退订写法有空格变体（`-= Handler`），不能用字面 `-=Name` 匹配
                    var unsubscribed = eventName.Length > 0
                        && lines.Any(l => Regex.IsMatch(l, $@"\.\s*{Regex.Escape(eventName)}\s*-="));
                    if (!unsubscribed && !lines.Any(l => l.Contains("Dispose", StringComparison.Ordinal)))
                        missingUnsubscribe.Add($"{relative}:{lineNo} 订阅 {eventName} 但没有退订（对象无法回收）");
                }
                // 有终结器却没有 Dispose：只能等 GC 终结，资源释放不及时
                if (Regex.IsMatch(trimmed, @"~\w+\s*\(")
                    && !lines.Any(l => l.Contains("Dispose", StringComparison.Ordinal) && !l.Contains("~", StringComparison.Ordinal)))
                    finalizerOnly.Add($"{relative}:{lineNo} 定义了终结器但没有 Dispose（资源释放依赖 GC）");
            }
        }
        if (files == 0)
            return "资源生命周期报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"资源生命周期报告: {files} 个 .cs 文件");
        Report(output, "可释放对象未托管", undisposed, maxResults);
        Report(output, "订阅未退订", missingUnsubscribe, maxResults);
        Report(output, "只有终结器无 Dispose", finalizerOnly, maxResults);
        if (undisposed.Count == 0 && missingUnsubscribe.Count == 0 && finalizerOnly.Count == 0)
            output.AppendLine("未发现资源生命周期问题");
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
