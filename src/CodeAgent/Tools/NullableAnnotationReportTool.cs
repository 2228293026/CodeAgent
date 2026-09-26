using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查可空性标注：开了 #nullable 却仍有裸的引用类型字段/参数、
/// 用 #nullable disable 区段、以及可空集合元素（List&lt;string&gt;）写成不可空。</summary>
public sealed class NullableAnnotationReportTool : ITool
{
    public string Name => "nullable_annotation_report";
    public string Description =>
        "只读检查可空性标注：#nullable disable 区段、裸的引用类型字段/参数、可空集合写成 List<string> 而非 List<string?>。";

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
        var disabled = new List<string>();
        var bareReference = new List<string>();
        var nonNullableCollection = new List<string>();
        var files = 0;
        var nullableOn = 0;
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
            var inNullableContext = false;
            var lineNo = 0;
            foreach (var raw in lines)
            {
                lineNo++;
                var trimmed = raw.Trim();
                if (trimmed.StartsWith("#nullable", StringComparison.Ordinal))
                {
                    var isEnable = trimmed.Contains("enable", StringComparison.Ordinal);
                    var isDisable = trimmed.Contains("disable", StringComparison.Ordinal);
                    if (isEnable && !inNullableContext)
                    {
                        nullableOn++;
                        inNullableContext = true;
                    }
                    else if (isDisable)
                    {
                        inNullableContext = false;
                        disabled.Add($"{relative}:{lineNo} #nullable disable（该文件或区段的可空性检查被关掉，警告会被静默）");
                    }
                    continue;
                }
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                if (!inNullableContext)
                    continue;
                // 裸的引用类型字段/参数：string / object / 自定义类型，且没有 ? 与 null 标注
                foreach (Match m in Regex.Matches(trimmed, @"\b(?:public|private|protected|internal)\s+(?:static\s+|readonly\s+|const\s+|new\s+)*(?<t>[A-Z]\w*|string|object|StringBuilder|Dictionary<[^>]+>)\s+(?<f>\w+)\s*(?:[=;{])"))
                {
                    var type = m.Groups["t"].Value;
                    if (type.EndsWith('?'))
                        continue;
                    bareReference.Add($"{relative}:{lineNo} 字段 {m.Groups["f"].Value} 类型 {type} 未标注可空（开了 #nullable 时编译器按非空处理）");
                }
                // List<string> 元素可空却没写 List<string?>。
                // 元素类型必须同时接受 `[A-Z]\w*`（自定义类型）和 string/object 这类
                // 小写别名——`List<string>` 是最常见写法，只认大写开头等于漏掉主战场。
                foreach (Match m in Regex.Matches(trimmed, @"\b(?:List|IList|IReadOnlyList|IEnumerable|HashSet|IReadOnlyCollection|Collection)<(?<e>[A-Za-z_]\w*)>"))
                    nonNullableCollection.Add($"{relative}:{lineNo} 集合 {m.Value} 的元素类型可空时没写 ?（List<string> 与 List<string?> 语义完全不同）");
            }
        }
        if (files == 0)
            return "可空性标注报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"可空性标注报告: {files} 个 .cs 文件，{nullableOn} 个开启 #nullable 的文件");
        Report(output, "#nullable disable", disabled, maxResults);
        Report(output, "裸引用类型", bareReference, maxResults);
        Report(output, "集合元素未标 ?", nonNullableCollection, maxResults);
        if (disabled.Count == 0 && bareReference.Count == 0 && nonNullableCollection.Count == 0)
            output.AppendLine("未发现可空性标注问题");
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
