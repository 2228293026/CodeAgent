using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查资源释放：using 缺失、Stream/Process 未释放、以及 IDisposable 字段未在终结器释放。</summary>
public sealed class ResourceLeakReportTool : ITool
{
    public string Name => "resource_leak_report";
    public string Description =>
        "只读审查资源释放：using 块缺失的 Stream/Process 创建、缺少 Dispose 调用的字段，以及未关闭的 StreamReader/Writer。";

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

    private static readonly string[] Tracked =
    [
        "FileStream", "StreamReader", "StreamWriter", "MemoryStream", "Process",
        "FileInfo", "DirectoryInfo", "Socket", "HttpClient", "SqlConnection",
    ];

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 30), 1, 300);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        var untracked = new List<string>();
        var noDispose = new List<string>();
        var files = 0;
        var creations = 0;
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
                var line = lines[i];
                foreach (var type in Tracked)
                {
                    // new FileStream(...) / new StreamReader(...) 形式
                    if (!Regex.IsMatch(line, $@"new\s+{type}\s*\("))
                        continue;
                    creations++;
                    var lineNo = i + 1;
                    // 已用 using（含 using var / await using）或有显式 Dispose
                    if (Regex.IsMatch(line, @"\busing\b") || Regex.IsMatch(line, @"\bDispose\s*\("))
                        continue;
                    // 下一行若是 using(...) 包裹同样算已处理
                    if (i + 1 < lines.Length
                        && (lines[i + 1].Contains("using (", StringComparison.Ordinal)
                            || lines[i + 1].Contains("using var", StringComparison.Ordinal)))
                        continue;
                    untracked.Add($"{relative}:{lineNo} new {type} 未见 using/Dispose");
                    break;
                }
                // 持有 IDisposable 的字段却从未 Dispose
                var field = Regex.Match(line, @"private\s+(?:readonly\s+)?([A-Za-z_][A-Za-z0-9_]*)\s+_\w+\s*;");
                if (!field.Success || !Tracked.Contains(field.Groups[1].Value))
                    continue;
                var name = Regex.Match(line, @"_\w+").Value;
                if (name.Length == 0)
                    continue;
                var disposed = lines.Any(l => l.Contains($"{name}?.Dispose", StringComparison.Ordinal)
                    || l.Contains($"{name}.Dispose", StringComparison.Ordinal)
                    || l.Contains($"~{field.Groups[1].Value}", StringComparison.Ordinal));
                if (!disposed)
                    noDispose.Add($"{relative}:{i + 1} 字段 {name}（{field.Groups[1].Value}）未见 Dispose/终结器");
            }
        }
        if (files == 0)
            return "资源泄漏报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"资源释放报告: {files} 个文件，{creations} 处资源创建");
        Report(output, "缺少 using/Dispose", untracked, maxResults);
        Report(output, "字段未释放", noDispose, maxResults);
        if (untracked.Count == 0 && noDispose.Count == 0)
            output.AppendLine("未发现疑似资源泄漏");
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
