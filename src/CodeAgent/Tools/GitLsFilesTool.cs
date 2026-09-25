using System.IO.Enumeration;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读列出 Git 已跟踪或未跟踪的文件，支持 glob 过滤和输出限制。</summary>
public sealed class GitLsFilesTool : ITool
{
    public string Name => "git_ls_files";
    public string Description =>
        "列出 Git 已跟踪文件或未跟踪文件，支持 glob 过滤、显示未跟踪项和最大结果数；不修改仓库。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["mode"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("tracked", "untracked", "all"),
                ["description"] = "列出 tracked、untracked 或 all（默认 tracked）",
            },
            ["patterns"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "可选 glob 模式，如 [\"src/**\", \"*.cs\"]",
            },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示路径数（默认 200，最大 10000）" },
            ["max_output_chars"] = new JsonObject { ["type"] = "integer", ["description"] = "最大输出字符数（默认 30000，最大 200000）" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Git 命令超时秒数（默认 10，最大 60）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var mode = ToolArgs.GetString(args, "mode")?.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(mode))
            mode = "tracked";
        if (mode is not ("tracked" or "untracked" or "all"))
            throw new ToolException("mode 只能是 tracked、untracked 或 all");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 200), 1, 10_000);
        var maxOutput = Math.Clamp(ToolArgs.GetInt(args, "max_output_chars", 30_000), 1_000, 200_000);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 10), 1, 60);
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var command = new List<string> { "ls-files", "-z" };
        if (mode is "tracked" or "all")
            command.Add("--cached");
        if (mode is "untracked" or "all")
        {
            command.Add("--others");
            command.Add("--exclude-standard");
        }
        var result = await GitStatusTool.RunGitAsync(root, command, timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException($"git ls-files 失败（退出码 {result.ExitCode}）: {result.Error}");
        var patterns = ToolArgs.GetStringList(args, "patterns")?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
        var paths = result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Replace('\\', '/'))
            .Where(path => patterns is null || patterns.Count == 0 || patterns.Any(pattern => GlobMatches(path, pattern)))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var output = new StringBuilder();
        output.AppendLine($"Git 文件列表（{mode}）: {paths.Count:N0} 个匹配");
        foreach (var path in paths.Take(maxResults))
            output.AppendLine(path);
        if (paths.Count > maxResults)
            output.AppendLine($"…（另有 {paths.Count - maxResults} 个路径未显示）");
        return Limit(output.ToString().TrimEnd(), maxOutput);
    }

    private static bool GlobMatches(string path, string pattern)
    {
        pattern = pattern.Replace('\\', '/');
        if (pattern.StartsWith("./", StringComparison.Ordinal))
            pattern = pattern[2..];
        return FileSystemName.MatchesSimpleExpression(pattern, path, ignoreCase: true)
               || (pattern.EndsWith('/') && path.StartsWith(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private static string Limit(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        const string marker = "\n…（Git 文件列表输出已截断）";
        return text[..Math.Max(0, maxChars - marker.Length)] + marker;
    }
}
