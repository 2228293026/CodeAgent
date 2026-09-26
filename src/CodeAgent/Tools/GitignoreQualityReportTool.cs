using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 .gitignore 质量：忽略 bin/obj 却漏掉 .vs/.idea 等、
/// 用绝对路径或 Windows 反斜杠（跨平台失效）、以及只忽略目录却没忽略其内容。</summary>
public sealed class GitignoreQualityReportTool : ITool
{
    public string Name => "gitignore_quality_report";
    public string Description =>
        "只读检查 .gitignore 质量：跨平台失效的反斜杠、绝对路径、通配符过宽、常见构建产物漏忽略。";

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
        var backslash = new List<string>();
        var absolute = new List<string>();
        var tooBroad = new List<string>();
        var missing = new List<string>();
        var file = Path.Combine(root, ".gitignore");
        if (!File.Exists(file))
            return "gitignore 质量报告: 仓库根目录没有 .gitignore（构建产物与本地配置可能被提交）";
        string[] lines;
        try { lines = await File.ReadAllLinesAsync(file, ct); }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { return "gitignore 质量报告: 无法读取 .gitignore"; }
        catch (UnauthorizedAccessException) { return "gitignore 质量报告: 无权读取 .gitignore"; }
        var patterns = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.Trim();
            var lineNo = i + 1;
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            patterns.Add(line);
            if (line.Contains('\\', StringComparison.Ordinal))
                backslash.Add($"第 {lineNo} 行 {line}（反斜杠是 Windows 路径分隔符，Linux/macOS 上不匹配）");
            if (line.StartsWith('/') || Regex.IsMatch(line, @"^[A-Za-z]:") || line.StartsWith("~", StringComparison.Ordinal))
                absolute.Add($"第 {lineNo} 行 {line}（绝对路径/家目录只在某一台机器上成立，换机器就失效）");
            if (Regex.IsMatch(line, @"^\*+$") || line == "*" || line == "**")
                tooBroad.Add($"第 {lineNo} 行 {line}（忽略一切，等于没有 .gitignore）");
        }
        // 常见构建产物 / 本地配置漏忽略
        foreach (var (pattern, why) in new[]
        {
            ("bin/", "编译输出"), ("obj/", "中间产物"), (".vs/", "Visual Studio 本地状态"),
            (".idea/", "JetBrains 本地状态"), ("*.user", "个人化项目设置"),
        })
        {
            var covered = patterns.Any(p =>
                p.TrimEnd('/') == pattern.TrimEnd('/') ||
                p == pattern || p.TrimEnd('/') == pattern.TrimEnd('/') ||
                (pattern.EndsWith('/') && p == pattern.TrimEnd('/') + "/") ||
                (pattern == "*.user" && (p == "*.user" || p == "*.suo")));
            if (!covered)
                missing.Add($"未忽略 {pattern}（{why}）");
        }
        var output = new StringBuilder();
        output.AppendLine($"gitignore 质量报告: {lines.Length} 行，{patterns.Count} 条有效规则");
        Report(output, "反斜杠（跨平台失效）", backslash, maxResults);
        Report(output, "绝对路径/家目录", absolute, maxResults);
        Report(output, "通配符过宽", tooBroad, maxResults);
        Report(output, "常见产物漏忽略", missing, maxResults);
        if (backslash.Count == 0 && absolute.Count == 0 && tooBroad.Count == 0 && missing.Count == 0)
            output.AppendLine("未发现 .gitignore 质量问题");
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
