using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读提取代码里的中文字符串字面量，评估 i18n 就绪度。</summary>
public sealed class I18nStringReportTool : ITool
{
    private static readonly Regex Literal = new(
        @"(?<quote>""|')(?<text>[^""'\r\n]*[\u4e00-\u9fff][^""'\r\n]*)(?:\k<quote>)",
        RegexOptions.Compiled);

    public string Name => "i18n_string_report";
    public string Description =>
        "只读提取源码中的中文字符串字面量，按文件汇总并给出样例，用于评估 i18n 改造范围。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["samples"] = new JsonObject { ["type"] = "integer", ["description"] = "每个文件显示的样例数（默认 3，最大 20）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多列出文件数（默认 50，最大 500）" },
            ["max_depth"] = new JsonObject { ["type"] = "integer", ["description"] = "扫描深度上限（默认 8，最大 32）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var samples = Math.Clamp(ToolArgs.GetInt(args, "samples", 3), 0, 20);
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", 50), 1, 500);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 8), 1, 32);
        var perFile = new List<(string File, List<string> Texts)>();
        var total = 0;
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                continue;
            string text;
            try { text = await File.ReadAllTextAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var texts = new List<string>();
            foreach (Match match in Literal.Matches(text))
            {
                var value = match.Groups["text"].Value.Trim();
                if (value.Length == 0 || value.StartsWith("///", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal))
                    continue;
                texts.Add(value);
            }
            if (texts.Count == 0)
                continue;
            total += texts.Count;
            perFile.Add((Path.GetRelativePath(root, file).Replace('\\', '/'), texts));
        }
        if (total == 0)
            return "中文字符串报告: 未发现中文字符串字面量";
        perFile.Sort((a, b) => b.Texts.Count.CompareTo(a.Texts.Count));
        var output = new StringBuilder();
        output.AppendLine($"中文字符串报告: {total:N0} 处，分布在 {perFile.Count} 个文件");
        foreach (var (file, texts) in perFile.Take(maxFiles))
        {
            output.AppendLine($"  {file}: {texts.Count}");
            foreach (var sample in texts.Distinct(StringComparer.Ordinal).Take(samples))
                output.AppendLine($"    「{Truncate(sample, 48)}」");
        }
        if (perFile.Count > maxFiles)
            output.AppendLine($"…（另有 {perFile.Count - maxFiles} 个文件未显示）");
        return output.ToString().TrimEnd();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
