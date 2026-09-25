using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 C# 编码习惯：var 使用、显式类型、括号风格、区域与 #region 混用、文件头缺失。</summary>
public sealed class CodeStyleReportTool : ITool
{
    public string Name => "code_style_report";
    public string Description =>
        "只读统计 C# 编码风格分布：var 与显式类型的比例、Allman 与 K&R 大括号风格、文件头注释缺失。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
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
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        int varDecls = 0, explicitDecls = 0, allman = 0, kr = 0, missingHeader = 0, files = 0;
        var samples = new List<string>();
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
            if (!lines.Any(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal)))
            {
                missingHeader++;
                if (samples.Count < 20)
                    samples.Add($"{relative}（无文件头注释）");
            }
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (Regex.IsMatch(line, @"^\s*var\s+\w+"))
                    varDecls++;
                else if (Regex.IsMatch(line, @"^\s*(?:private|public|internal|protected)\s+(?:static\s+|readonly\s+|const\s+)*[A-Za-z_][A-Za-z0-9_<>\[\],\.]*\s+\w+\s*[=;]"))
                    explicitDecls++;
                // Allman：{ 独占一行；其余含 { 的行都算 K&R——
                // 单行块 `void M() { var a = 1; }` 以 } 结尾，只看 EndsWith('{') 会漏掉它
                var trimmed = line.Trim();
                if (trimmed == "{")
                    allman++;
                else if (trimmed.Contains('{'))
                    kr++;
            }
        }
        if (files == 0)
            return "代码风格报告: 未找到 .cs 文件";
        var totalDecls = varDecls + explicitDecls;
        var output = new StringBuilder();
        output.AppendLine($"代码风格报告: {files} 个 .cs 文件");
        output.AppendLine($"  类型声明: var {varDecls:N0} / 显式 {explicitDecls:N0}"
            + (totalDecls > 0 ? $"（var 占比 {varDecls * 100.0 / totalDecls:0.#}%）" : ""));
        output.AppendLine($"  大括号: K&R（行尾）{kr:N0} / Allman（独占行）{allman:N0}");
        if (kr > 0 && allman > 0)
            output.AppendLine("  ⚠ 两种大括号风格混用：同一仓库内应统一，便于扫读");
        output.AppendLine($"  缺文件头注释: {missingHeader:N0} 个");
        foreach (var sample in samples)
            output.AppendLine($"    {sample}");
        return output.ToString().TrimEnd();
    }
}
