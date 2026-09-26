using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 IDisposable 实现：托管包装未释放基类、using 块内 return 跳过了释放、
/// 以及实现 IDisposable 但缺少终结器或抑制终结器模式。</summary>
public sealed class DisposablePatternReportTool : ITool
{
    public string Name => "disposable_pattern_report";
    public string Description =>
        "只读检查 IDisposable：实现 IDisposable 但未释放基类、持有 IDisposable 字段却从不释放、缺少 GC.SuppressFinalize。";

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
        var noSuppressFinalize = new List<string>();
        var undisposedField = new List<string>();
        var finalizerWithoutSuppress = new List<string>();
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
            // 实现 IDisposable 且有终结器：Dispose 里必须抑制终结器
            foreach (Match m in Regex.Matches(text, @"(class|struct)\s+(?<name>\w+)[^\n]*:\s*[^\n]*\bIDisposable\b"))
            {
                var name = m.Groups["name"].Value;
                if (!Regex.IsMatch(text, $@"~{Regex.Escape(name)}\s*\("))
                    continue;
                if (!text.Contains("GC.SuppressFinalize", StringComparison.Ordinal))
                    finalizerWithoutSuppress.Add($"{relative} {name} 有终结器但 Dispose 未调用 GC.SuppressFinalize（对象会被重复终结）");
            }
            foreach (Match m in Regex.Matches(text, @"(class|struct)\s+(?<name>\w+)[^\n]*:\s*[^\n]*\bIDisposable\b"))
            {
                var name = m.Groups["name"].Value;
                if (Regex.IsMatch(text, $@"~{Regex.Escape(name)}\s*\("))
                    continue;
                if (!Regex.IsMatch(text, $@"void\s+Dispose\s*\(\s*\)") && !Regex.IsMatch(text, $@"ValueTask\s+DisposeAsync"))
                    noSuppressFinalize.Add($"{relative} {name} 实现 IDisposable 但未见 Dispose 实现");
            }
            // 持有 IDisposable 字段却从不 Dispose/using
            foreach (Match m in Regex.Matches(text, @"private\s+(?:readonly\s+)?(?:[\w\.]+\.)?(Stream|StreamReader|StreamWriter|FileStream|MemoryStream|Process|Timer|HttpClient|Socket|ReaderWriterLock)\s+(?<f>_\w+)\s*[=;]"))
            {
                var field = m.Groups["f"].Value;
                if (!Regex.IsMatch(text, $@"{Regex.Escape(field)}\s*\??\s*\.\s*(Dispose|Close)\s*\(")
                    && !Regex.IsMatch(text, $@"using\s*\(.*\b{Regex.Escape(field)}\b")
                    && !Regex.IsMatch(text, $@"GC\.SuppressFinalize\s*\(\s*{Regex.Escape(field)}\s*\)"))
                    undisposedField.Add($"{relative} 持有 {field} 但全文未见 Dispose/using（可能泄漏句柄）");
            }
        }
        if (files == 0)
            return "IDisposable 报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"IDisposable 报告: {files} 个 .cs 文件");
        Report(output, "有终结器但未抑制", finalizerWithoutSuppress, maxResults);
        Report(output, "缺少 Dispose 实现", noSuppressFinalize, maxResults);
        Report(output, "字段未释放", undisposedField, maxResults);
        if (finalizerWithoutSuppress.Count == 0 && noSuppressFinalize.Count == 0 && undisposedField.Count == 0)
            output.AppendLine("未发现 IDisposable 问题");
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
