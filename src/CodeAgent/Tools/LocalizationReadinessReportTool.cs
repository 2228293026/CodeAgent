using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查国际化可用性：硬编码中文字符串、缺失本地化资源、以及未统一的日期/数字格式。</summary>
public sealed class LocalizationReadinessReportTool : ITool
{
    public string Name => "localization_readiness_report";
    public string Description =>
        "只读检查国际化准备度：面向用户的硬编码中文字符串、缺少本地化资源文件、以及直接拼接的日期格式。";

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
        var hardcoded = new List<string>();
        var adHocDate = new List<string>();
        var hasResource = false;
        var hasChinese = false;
        var files = 0;
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            if (name.EndsWith(".resx", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".resources.json", StringComparison.OrdinalIgnoreCase))
                hasResource = true;
            if (!name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
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
                if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                hasChinese |= Regex.IsMatch(trimmed, @"[\u4e00-\u9fff]");
                // 写给用户看的中文字面量（Console/Exception/提示语），没有走资源文件
                var literal = Regex.Match(trimmed, @"""([^""\\]*[\u4e00-\u9fff][^""\\]*)""");
                if (literal.Success
                    && (trimmed.Contains("Console.", StringComparison.Ordinal)
                        || trimmed.Contains("throw new", StringComparison.Ordinal)
                        || trimmed.Contains("ToolException", StringComparison.Ordinal)
                        || trimmed.Contains("Description", StringComparison.Ordinal)))
                    hardcoded.Add($"{relative}:{lineNo} 用户可见文案硬编码中文（{TextUtil.TruncateLine(literal.Groups[1].Value, 30)}）");
                // 自己拼日期格式：不同区域下顺序不同。格式串是**组合式**的（"yyyy/MM/dd"），
                // 只认单个 yyyy/MM/dd 会漏掉绝大多数实际写法。
                if (Regex.IsMatch(trimmed, @"(?:yyyy|dd|MM)[/.\-](?:yyyy|MM|dd)[/.\-](?:yyyy|MM|dd)\s*\+")
                    || Regex.IsMatch(trimmed, @"ToString\s*\(\s*""[^""]*(?:yyyy|MM|dd|HH)[^""]*"""))
                    adHocDate.Add($"{relative}:{lineNo} 日期格式硬编码（应走区域化格式）");
            }
        }
        if (files == 0)
            return "国际化准备度报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"国际化准备度报告: {files} 个 .cs 文件{(hasResource ? "（已找到本地化资源）" : "（未找到 .resx 等本地化资源）")}");
        Report(output, "用户文案硬编码中文", hardcoded, maxResults);
        Report(output, "日期格式硬编码", adHocDate, maxResults);
        if (hasChinese && !hasResource)
            output.AppendLine("提示: 代码含中文字面量但仓库没有本地化资源，尚未具备多语言能力");
        if (hardcoded.Count == 0 && adHocDate.Count == 0)
            output.AppendLine("未发现国际化准备度问题");
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
