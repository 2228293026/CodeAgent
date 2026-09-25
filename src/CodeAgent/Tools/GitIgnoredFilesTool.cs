using System.IO.Enumeration;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读列出 Git 忽略规则覆盖的文件。</summary>
public sealed class GitIgnoredFilesTool : ITool
{
    public string Name => "git_ignored_files";
    public string Description =>
        "列出 Git 忽略规则覆盖的未跟踪文件，支持 glob、结果和输出限制，不修改仓库。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["patterns"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "只显示匹配 glob 的路径" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示文件数（默认 200，最大 5000）" },
            ["max_output_chars"] = new JsonObject { ["type"] = "integer", ["description"] = "最大输出字符数（默认 20000，最大 100000）" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Git 命令超时秒数（默认 15，最大 120）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var start = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(start))
            throw new ToolException($"目录不存在: {requestedPath}");
        var patterns = ToolArgs.GetStringSet(args, "patterns");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 200), 1, 5_000);
        var maxOutput = Math.Clamp(ToolArgs.GetInt(args, "max_output_chars", 20_000), 1_000, 100_000);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 15), 1, 120);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var result = await GitStatusTool.RunGitAsync(start, ["ls-files", "-z", "--others", "--ignored", "--exclude-standard"], timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException($"git ls-files 失败（退出码 {result.ExitCode}）: {result.Error.Trim()}");
        var files = result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(path => path.Replace('\\', '/'))
            .Where(path => patterns is null || patterns.Count == 0 || patterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, path)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        var output = new StringBuilder();
        output.AppendLine($"Git 忽略文件: {files.Count:N0} 个");
        foreach (var file in files.Take(maxResults))
            output.AppendLine(file);
        if (files.Count > maxResults)
            output.AppendLine($"…（另有 {files.Count - maxResults} 个文件未显示）");
        var text = output.ToString().TrimEnd();
        if (text.Length <= maxOutput)
            return text;
        const string marker = "\n…（Git 忽略文件输出已截断）";
        return text[..Math.Max(0, maxOutput - marker.Length)] + marker;
    }
}
