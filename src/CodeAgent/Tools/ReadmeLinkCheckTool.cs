using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 Markdown 中的相对链接是否指向真实存在的文件。</summary>
public sealed class ReadmeLinkCheckTool : ITool
{
    public string Name => "readme_link_check";
    public string Description =>
        "只读扫描 Markdown 的链接，区分外链/锚点/相对路径，报告指向不存在文件的失效链接。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "文件或目录，默认工作区根目录" },
            ["include_external"] = new JsonObject { ["type"] = "boolean", ["description"] = "统计外链（默认 false）" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示条目数（默认 100，最大 2000）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多检查文件数（默认 20，最大 200）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var resolved = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        var includeExternal = ToolArgs.GetBool(args, "include_external", false);
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 100), 1, 2_000);
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", 20), 1, 200);
        var files = new List<string>();
        if (File.Exists(resolved) && IsMarkdown(resolved))
        {
            files.Add(resolved);
        }
        else if (Directory.Exists(resolved))
        {
            foreach (var file in SkipDirs.EnumerateFilesPruned(resolved, 4, includeIgnored: false, ct: ct, followSymlinks: false))
            {
                ct.ThrowIfCancellationRequested();
                if (IsMarkdown(file))
                {
                    files.Add(file);
                    if (files.Count >= maxFiles)
                        break;
                }
            }
        }
        if (files.Count == 0)
            throw new ToolException("未找到 Markdown 文件");
        var total = 0;
        var external = 0;
        var anchors = 0;
        var broken = new List<string>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var dir = Path.GetDirectoryName(file) ?? resolved;
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in Regex.Matches(lines[i], @"\[[^\]]*\]\(([^)\s]+)\)"))
                {
                    var target = match.Groups[1].Value.Trim('<', '>');
                    if (target.Length == 0)
                        continue;
                    total++;
                    if (target.StartsWith('#'))
                    {
                        anchors++;
                        continue;
                    }
                    if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                        || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                    {
                        external++;
                        if (includeExternal)
                            broken.Add($"  外链 {Path.GetFileName(file)}:{i + 1} {target}");
                        continue;
                    }
                    var pathPart = target.Split('#', 2)[0].Split('?', 2)[0];
                    if (pathPart.Length == 0)
                    {
                        anchors++;
                        continue;
                    }
                    string full;
                    try { full = Path.GetFullPath(Path.Combine(dir, Uri.UnescapeDataString(pathPart))); }
                    catch (UriFormatException) { full = string.Empty; }
                    if (full.Length == 0 || !File.Exists(full) && !Directory.Exists(full))
                        broken.Add($"  失效 {Path.GetFileName(file)}:{i + 1} → {target}");
                }
            }
        }
        var output = new StringBuilder();
        output.AppendLine($"Markdown 链接检查: {files.Count:N0} 个文件，{total:N0} 条链接");
        output.AppendLine($"  外链: {external:N0}   锚点: {anchors:N0}");
        var realBroken = broken.Count(b => b.Contains("失效", StringComparison.Ordinal));
        output.AppendLine($"  失效相对链接: {realBroken:N0}");
        if (broken.Count > 0)
        {
            output.AppendLine("明细:");
            foreach (var line in broken.Take(maxResults))
                output.AppendLine(line);
            if (broken.Count > maxResults)
                output.AppendLine($"…（另有 {broken.Count - maxResults} 条未显示）");
        }
        return output.ToString().TrimEnd();
    }

    private static bool IsMarkdown(string file) =>
        Path.GetExtension(file).Equals(".md", StringComparison.OrdinalIgnoreCase);
}
