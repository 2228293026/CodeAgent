using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查命名规范：文件名与类型名不一致、下划线命名、误导性类型名（Helper/Utils）。</summary>
public sealed class NamingConventionReportTool : ITool
{
    public string Name => "naming_convention_report";
    public string Description =>
        "只读检查命名规范：文件名与主类型名不一致、下划线/短横线命名文件、以及 Helper/Utils/Manager 之类含义模糊的类型名。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示每类问题数（默认 40，最大 400）" },
            ["max_depth"] = new JsonObject { ["type"] = "integer", ["description"] = "扫描深度上限（默认 10，最大 32）" },
        },
    };

    private static readonly string[] Vague = ["Helper", "Helpers", "Utils", "Utility", "Util", "Misc", "Manager", "Manager2"];

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 40), 1, 400);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        var mismatch = new List<string>();
        var oddFiles = new List<string>();
        var vagueTypes = new List<string>();
        var files = 0;
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;
            files++;
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Contains('_', StringComparison.Ordinal) || name.Contains('-', StringComparison.Ordinal))
                oddFiles.Add($"{relative}（{name}）");
            string text;
            try { text = await File.ReadAllTextAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var types = Regex.Matches(text, @"\b(?:public|internal|sealed|abstract|static|partial|\s)*\b(class|record|struct|interface|enum)\s+([A-Za-z_][A-Za-z0-9_]*)")
                .Select(m => m.Groups[2].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (types.Count == 0)
                continue;
            // 文件名应与首个（或最贴近的）主类型名一致，否则 IDE 跳转与代码阅读都要重新定位
            if (!types.Any(t => string.Equals(t, name, StringComparison.Ordinal)))
                mismatch.Add($"{relative}：类型 {string.Join(" / ", types.Take(3))}");
            foreach (var type in types)
            {
                var vague = Vague.FirstOrDefault(v => type.EndsWith(v, StringComparison.Ordinal) && type != v);
                if (vague is not null)
                    vagueTypes.Add($"{relative}：{type} 以「{vague}」结尾（名字未说明职责）");
            }
        }
        if (files == 0)
            return "命名规范报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"命名规范报告: {files} 个 .cs 文件");
        Report(output, "文件名与类型名不一致", mismatch, maxResults);
        Report(output, "非 PascalCase 文件名", oddFiles, maxResults);
        Report(output, "含义模糊的类型名", vagueTypes, maxResults);
        if (mismatch.Count == 0 && oddFiles.Count == 0 && vagueTypes.Count == 0)
            output.AppendLine("未发现命名规范问题");
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
