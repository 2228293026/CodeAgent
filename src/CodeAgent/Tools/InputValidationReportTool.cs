using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查输入校验：外部输入（argv/环境变量/配置）直接使用而未经 null/长度/范围检查。</summary>
public sealed class InputValidationReportTool : ITool
{
    public string Name => "input_validation_report";
    public string Description =>
        "只读检查输入校验：环境变量直接使用未判空、命令行参数未校验、配置读取后未做范围检查。";

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
        var envWithoutCheck = new List<string>();
        var argvWithoutCheck = new List<string>();
        var parseWithoutTry = new List<string>();
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
                var line = lines[i];
                var trimmed = line.Trim();
                // 环境变量取值后直接传给可能不接受 null 的调用
                if (Regex.IsMatch(trimmed, @"Environment\.GetEnvironmentVariable\s*\("))
                {
                    var guarded = (trimmed.Contains("??", StringComparison.Ordinal)
                        || trimmed.Contains("string.IsNullOr", StringComparison.Ordinal)
                        || trimmed.Contains("IsNullOrEmpty", StringComparison.Ordinal)
                        || trimmed.Contains("IsNullOrWhiteSpace", StringComparison.Ordinal)
                        || i > 0 && lines[i - 1].Contains("IsNullOr", StringComparison.Ordinal));
                    if (!guarded)
                        envWithoutCheck.Add($"{relative}:{i + 1} 环境变量未判空即使用（{TextUtil.TruncateLine(trimmed, 60)}）");
                }
                // Parse/TryParse：Parse 失败会抛异常，用户输入不可信
                if (Regex.IsMatch(trimmed, @"\b(int|long|double|decimal|bool|DateTime)\.Parse\s*\(")
                    && !Regex.IsMatch(trimmed, @"TryParse|IsNullOr"))
                    parseWithoutTry.Add($"{relative}:{i + 1} 用 Parse 而非 TryParse（不可信输入会抛异常）");
                // args[i] 直接解引用：索引越界风险
                if (Regex.IsMatch(trimmed, @"\bargs\s*\[\s*\w+\s*\]\s*(\.|\))") && !trimmed.Contains("Length", StringComparison.Ordinal))
                    argvWithoutCheck.Add($"{relative}:{i + 1} 命令行参数未查 Length 即取值（{TextUtil.TruncateLine(trimmed, 60)}）");
            }
        }
        if (files == 0)
            return "输入校验报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"输入校验报告: {files} 个 .cs 文件");
        Report(output, "环境变量未判空", envWithoutCheck, maxResults);
        Report(output, "Parse 而非 TryParse", parseWithoutTry, maxResults);
        Report(output, "命令行参数未查长度", argvWithoutCheck, maxResults);
        if (envWithoutCheck.Count == 0 && parseWithoutTry.Count == 0 && argvWithoutCheck.Count == 0)
            output.AppendLine("未发现输入校验缺口");
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
