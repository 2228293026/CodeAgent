using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读提取 README 的标题结构并提示常见章节缺失。</summary>
public sealed class ReadmeSectionReportTool : ITool
{
    private static readonly (string Key, string[] Keywords)[] ExpectedSections =
    {
        ("安装/构建", new[] { "install", "setup", "build", "安装", "构建" }),
        ("使用", new[] { "usage", "quick start", "使用", "快速开始" }),
        ("配置", new[] { "config", "configuration", "配置" }),
    };

    public string Name => "readme_section_report";
    public string Description =>
        "只读提取 README 的标题层级和行号，并提示安装/使用/配置等常见章节是否缺失。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "工作区内文件或目录，默认根目录" },
            ["max_headings"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示标题数（默认 200，最大 5000）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多分析文件数（默认 20，最大 200）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var resolved = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        var maxHeadings = Math.Clamp(ToolArgs.GetInt(args, "max_headings", 200), 1, 5_000);
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", 20), 1, 200);
        var files = new List<string>();
        if (File.Exists(resolved) && IsReadme(Path.GetFileName(resolved)))
        {
            files.Add(resolved);
        }
        else if (Directory.Exists(resolved))
        {
            foreach (var file in SkipDirs.EnumerateFilesPruned(resolved, 3, includeIgnored: false, ct: ct, followSymlinks: false))
            {
                ct.ThrowIfCancellationRequested();
                if (IsReadme(Path.GetFileName(file)))
                {
                    files.Add(file);
                    if (files.Count >= maxFiles)
                        break;
                }
            }
        }
        if (files.Count == 0)
            throw new ToolException("未找到 README 文件");
        var output = new StringBuilder();
        output.AppendLine($"README 章节报告: {files.Count:N0} 个文件");
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(
                Directory.Exists(resolved) ? resolved : Path.GetDirectoryName(resolved) ?? resolved,
                file).Replace('\\', '/');
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var headings = new List<(int Line, int Level, string Text)>();
            var titles = new List<string>();
            var inFence = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
                {
                    inFence = !inFence; // 代码块内的 # 不是标题
                    continue;
                }
                if (inFence || !line.StartsWith('#'))
                    continue;
                var match = Regex.Match(line, @"^(#+)\s+(.*\S)\s*$");
                if (!match.Success)
                    continue;
                var level = match.Groups[1].Value.Length;
                var text = match.Groups[2].Value.Trim();
                titles.Add(text);
                headings.Add((i + 1, level, text));
            }
            output.AppendLine($"  {relative}: {headings.Count:N0} 个标题");
            var shown = 0;
            foreach (var heading in headings)
            {
                if (shown >= maxHeadings)
                    break;
                output.AppendLine($"    L{heading.Line} h{heading.Level} {heading.Text}");
                shown++;
            }
            if (headings.Count > shown)
                output.AppendLine($"    …（另有 {headings.Count - shown} 个标题未显示）");
            var haystack = string.Join("\n", titles);
            var missing = ExpectedSections
                .Where(section => !section.Keywords.Any(keyword => haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                .Select(section => section.Key)
                .ToArray();
            output.AppendLine(missing.Length == 0
                ? "    常见章节: 齐全"
                : $"    可能缺失: {string.Join("、", missing)}");
        }
        return output.ToString().TrimEnd();
    }

    private static bool IsReadme(string name) =>
        name.StartsWith("readme", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(name).Equals(".md", StringComparison.OrdinalIgnoreCase);
}
