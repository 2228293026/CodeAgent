using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查输入校验：工具参数未校验就使用、命令拼接未转义、以及缺少上限的输入。</summary>
public sealed class InputSanitizationReportTool : ITool
{
    public string Name => "input_sanitization_report";
    public string Description =>
        "只读检查输入处理：工具参数直接使用未校验、shell 命令参数未加引号、以及没有长度/范围上限的输入。";

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
        var shellUnquoted = new List<string>();
        var unboundedInput = new List<string>();
        var unvalidatedArg = new List<string>();
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
                var trimmed = lines[i].Trim();
                var lineNo = i + 1;
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                // 变量直接插进 shell 命令串（含空格/引号就会改变命令结构）
                var shell = Regex.Match(trimmed, @"(?:Process\.Start|StartInfo|/bin/(?:ba)?sh|powershell|cmd(?:\.exe)?)\b");
                if (shell.Success && Regex.IsMatch(trimmed, @"""[^""]*""\s*\+"))
                {
                    var hasQuotedVar = Regex.IsMatch(trimmed, @"""[^""]*\{[^}]+\}[^""]*""");
                    if (!hasQuotedVar)
                        shellUnquoted.Add($"{relative}:{lineNo} shell 命令字符串拼接未加引号（{TextUtil.TruncateLine(trimmed, 45)}）");
                }
                // 没有长度/范围上限的外部输入
                if (Regex.IsMatch(trimmed, @"(?:Console\.ReadLine|ReadKey|args\[0\])")
                    && !lines.Skip(i).Take(4).Any(l => l.Contains("Length", StringComparison.Ordinal) || l.Contains("Clamp", StringComparison.Ordinal)))
                    unboundedInput.Add($"{relative}:{lineNo} 外部输入未校验长度/范围（{TextUtil.TruncateLine(trimmed, 45)}）");
                // 工具参数取出来直接用，没有空值/边界检查
                var arg = Regex.Match(trimmed, @"(?:ToolArgs\.GetString|ToolArgs\.GetInt)\s*\([^)]*\)\s*;\s*$");
                if (arg.Success)
                {
                    var varName = Regex.Match(trimmed, @"(?:var|[\w<>\[\]?]+)\s+(\w+)\s*=\s*(?:ToolArgs)").Groups[1].Value;
                    if (varName.Length > 0
                        && !lines.Skip(i).Take(3).Any(l => l.Contains("IsNullOrWhiteSpace", StringComparison.Ordinal)
                            || l.Contains("Clamp", StringComparison.Ordinal) || l.Contains("Throw", StringComparison.Ordinal)))
                        unvalidatedArg.Add($"{relative}:{lineNo} 工具参数取出后未校验（{varName}）");
                }
            }
        }
        if (files == 0)
            return "输入处理报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"输入处理报告: {files} 个 .cs 文件");
        Report(output, "shell 拼接未加引号", shellUnquoted, maxResults);
        Report(output, "外部输入无上限", unboundedInput, maxResults);
        Report(output, "工具参数未校验", unvalidatedArg, maxResults);
        if (shellUnquoted.Count == 0 && unboundedInput.Count == 0 && unvalidatedArg.Count == 0)
            output.AppendLine("未发现输入处理问题");
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
