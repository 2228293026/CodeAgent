using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 API 兼容性：公开方法签名变更标记缺失、已废弃 API 仍在使用、以及可空注解不一致。</summary>
public sealed class ApiCompatibilityReportTool : ITool
{
    public string Name => "api_compatibility_report";
    public string Description =>
        "只读检查 API 兼容性：公开签名变更没有配套的 Obsolete 标记、仍在使用已废弃成员、以及可空注解前后不一致。";

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
        var deprecatedUse = new List<string>();
        var obsoleteWithoutDoc = new List<string>();
        var nullableDrift = new List<string>();
        var files = 0;
        var deprecatedNames = new HashSet<string>(StringComparer.Ordinal);
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
                // 先收集所有被标记为废弃的成员名，供后续文件使用。
                // 成员声明在属性的**下一行**，文档注释在属性的**上一行**——两个方向都要看。
                if (trimmed.Contains("[Obsolete", StringComparison.Ordinal))
                {
                    var name = Regex.Match(trimmed, @"(?:class|record|struct|interface|enum|void|int|bool|string)\s+(\w+)");
                    if (!name.Success)
                        name = Regex.Match(string.Join("\n", lines.Skip(i + 1).Take(3)),
                            @"(?:class|record|struct|interface|enum|void|int|bool|string)\s+(\w+)");
                    if (name.Success)
                        deprecatedNames.Add(name.Groups[1].Value);
                    var nearby = string.Join("\n", lines.Skip(Math.Max(0, i - 2)).Take(6));
                    if (!nearby.Contains("///", StringComparison.Ordinal))
                        obsoleteWithoutDoc.Add($"{relative}:{lineNo} [Obsolete] 没有说明替代方案的文档注释");
                }
                // 仍在调用已废弃成员
                foreach (var name in deprecatedNames)
                {
                    if (name.Length > 0 && Regex.IsMatch(trimmed, $@"\b{Regex.Escape(name)}\s*\(")
                        && !trimmed.Contains("[Obsolete", StringComparison.Ordinal)
                        && !trimmed.Contains($"nameof({name})", StringComparison.Ordinal))
                        deprecatedUse.Add($"{relative}:{lineNo} 仍在调用已废弃成员 {name}()");
                }
                // 可空标注漂移：同一参数名在一个文件里既出现 ? 又没有
                if (Regex.IsMatch(trimmed, @"^public\s+.*\(.*\w+\?\s+\w+.*\)"))
                {
                    var param = Regex.Match(trimmed, @"(\w+)\?\s+\w+").Groups[1].Value;
                    if (param.Length > 0 && lines.Any(l => Regex.IsMatch(l, $@"\b{Regex.Escape(param)}\s+\w+[,)]") && !l.Contains("?", StringComparison.Ordinal)))
                        nullableDrift.Add($"{relative}:{lineNo} 可空标注不一致（{param}）");
                }
            }
        }
        if (files == 0)
            return "API 兼容性报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"API 兼容性报告: {files} 个 .cs 文件，{deprecatedNames.Count} 个已废弃成员");
        Report(output, "仍在使用已废弃成员", deprecatedUse, maxResults);
        Report(output, "Obsolete 缺说明", obsoleteWithoutDoc, maxResults);
        Report(output, "可空标注漂移", nullableDrift, maxResults);
        if (deprecatedUse.Count == 0 && obsoleteWithoutDoc.Count == 0 && nullableDrift.Count == 0)
            output.AppendLine("未发现 API 兼容性问题");
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
