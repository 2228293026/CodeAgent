using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 partial 成员：partial 声明与实现不在同一文件、partial 修饰符只写一半、
/// 以及 partial 方法声明了但没有实现。</summary>
public sealed class PartialMethodReportTool : ITool
{
    public string Name => "partial_method_report";
    public string Description =>
        "只读检查 partial 成员：partial 修饰符只写一半、partial 方法只有声明没有实现、partial 类型跨文件拆分。";

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
        var halfModifier = new List<string>();
        var unimplemented = new List<string>();
        var multiFileType = new List<string>();
        var files = 0;
        // 类型名 → 出现过的文件集合
        var typeFiles = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
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
                // 记下每个 partial 类型落在哪些文件里
                foreach (Match m in Regex.Matches(trimmed, @"\bpartial\s+(?:class|struct|record|interface)\s+(?<name>\w+)"))
                {
                    var name = m.Groups["name"].Value;
                    if (!typeFiles.TryGetValue(name, out var set))
                    {
                        set = new HashSet<string>(StringComparer.Ordinal);
                        typeFiles[name] = set;
                    }
                    set.Add(relative);
                    // partial 只写一半：声明处没有 partial，另一处却有
                    var bare = Regex.Match(trimmed, @"(?:class|struct|record|interface)\s+" + Regex.Escape(name) + @"\b");
                    if (bare.Success)
                        halfModifier.Add($"{relative}:{i + 1} 类型 {name} 这一处没写 partial（同一类型其它片段写了，编译会报重复定义）");
                }
                // partial 方法只有声明没有实现
                var pm = Regex.Match(trimmed, @"\bpartial\s+(?:void|[\w\.<>]+)\s+(?<name>\w+)\s*\((?<args>[^)]*)\)\s*;");
                if (pm.Success)
                    unimplemented.Add($"{relative}:{i + 1} partial 方法 {pm.Groups["name"].Value}({pm.Groups["args"].Value}) 只有声明没有实现（编译器会提示，从不调用）");
                // partial 方法声明了签名却以 ; 结尾，但既没 partial 也没 void
                if (Regex.IsMatch(trimmed, @"\bpartial\s") && !trimmed.Contains(';') && !trimmed.Contains('{'))
                    halfModifier.Add($"{relative}:{i + 1} partial 成员声明形态异常：{Truncate(trimmed, 60)}");
            }
        }
        foreach (var (name, set) in typeFiles)
        {
            if (set.Count > 1)
                multiFileType.Add($"{name} 拆在 {set.Count} 个文件里：{string.Join(", ", set.OrderBy(x => x, StringComparer.Ordinal))}");
        }
        if (files == 0)
            return "partial 成员报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"partial 成员报告: {files} 个 .cs 文件");
        Report(output, "partial 只写一半", halfModifier, maxResults);
        Report(output, "partial 方法未实现", unimplemented, maxResults);
        Report(output, "类型跨文件拆分", multiFileType, maxResults);
        if (halfModifier.Count == 0 && unimplemented.Count == 0 && multiFileType.Count == 0)
            output.AppendLine("未发现 partial 成员问题");
        return output.ToString().TrimEnd();
    }

    private static string Truncate(string s, int budget) =>
        s.Length <= budget ? s : s[..Math.Max(0, budget - 1)] + "…";

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
