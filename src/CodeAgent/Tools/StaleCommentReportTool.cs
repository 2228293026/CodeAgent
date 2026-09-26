using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查注释质量的具体缺陷：注释与被注释代码严重脱节
/// （注释内容是函数名的复述）、注释掉的死代码、以及行内尾注里混入的待办。</summary>
public sealed class StaleCommentReportTool : ITool
{
    public string Name => "stale_comment_report";
    public string Description =>
        "只读检查注释与代码是否脱节：注释只是函数名的复述、注释掉的死代码、注释里的过期标记。";

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

    /// <summary>注释里不算"有实义"的虚词——用于识别"只是在复述标识符"。</summary>
    private static readonly HashSet<string> FillerWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "of", "to", "for", "all", "and", "or", "is", "in", "on", "at",
        "get", "set", "this", "that", "it", "be", "by", "with", "from", "into",
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
        var restates = new List<string>();
        var commentedCode = new List<string>();
        var staleMarkers = new List<string>();
        var files = 0;
        // 注释里常见的过期/待办标记
        var marker = new Regex(@"\b(TODO|FIXME|XXX|HACK|BUG)\b|待办|待定|临时|以后再|将来", RegexOptions.IgnoreCase);
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
                if (!trimmed.StartsWith("//")) continue;
                var text = trimmed.TrimStart('/').Trim();
                if (text.Length == 0)
                    continue;
                if (marker.IsMatch(text))
                    staleMarkers.Add($"{relative}:{i + 1} {text}");
                // 注释掉的死代码：有语句形状（分号/赋值/调用/控制流关键字）
                if (Regex.IsMatch(text, @"(;|=>|\breturn\b|\bif\s*\(|\bfor\s*\(|\bwhile\s*\(|=\s*[^=]|^\s*[A-Za-z_]\w*\s*\.)") && !text.EndsWith('。') && !text.EndsWith('.'))
                    commentedCode.Add($"{relative}:{i + 1} 注释掉的代码：{text}");
                // 复述式注释：注释里除了名字本身和虚词什么都没说。
                // 判据是「所有有实义的词都来自标识符」——所以 `// Total` 与
                // `// The Total of all items` 结论不同：后者解释了「Total 是什么」，
                // 不是复述。用"注释长度"当判据会把后者也误杀。
                if (i + 1 >= lines.Length) continue;
                var next = lines[i + 1].Trim();
                var decl = Regex.Match(next, @"\b(?:class|struct|interface|enum|record)\s+(?<n>\w+)|(?:\w[\w\.<>\[\],\?]*)\s+(?<n2>\w+)\s*\(");
                if (!decl.Success) continue;
                var name = decl.Groups["n"].Success ? decl.Groups["n"].Value : decl.Groups["n2"].Value;
                if (name.Length < 3) continue;
                var tokens = Regex.Matches(text, @"[A-Za-z_]\w+").Select(t => t.Value).ToList();
                if (tokens.Count == 0) continue;
                var meaningful = tokens.Where(t => !FillerWords.Contains(t.ToLowerInvariant())).ToList();
                if (meaningful.Count == 0) continue;
                if (meaningful.All(t => name.Contains(t, StringComparison.OrdinalIgnoreCase)
                                     || t.Contains(name, StringComparison.OrdinalIgnoreCase)))
                    restates.Add($"{relative}:{i + 1} 注释只是复述 {name}：{text}");
            }
        }
        if (files == 0)
            return "脱节注释报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"脱节注释报告: {files} 个 .cs 文件");
        Report(output, "复述式注释", restates, maxResults);
        Report(output, "注释掉的死代码", commentedCode, maxResults);
        Report(output, "待办/过期标记", staleMarkers, maxResults);
        if (restates.Count == 0 && commentedCode.Count == 0 && staleMarkers.Count == 0)
            output.AppendLine("未发现脱节注释");
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
