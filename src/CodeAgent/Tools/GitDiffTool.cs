using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>以只读方式查看 Git 工作区或暂存区的统一差异。</summary>
public sealed class GitDiffTool : ITool
{
    public string Name => "git_diff";
    public string Description =>
        "查看 Git 工作区/暂存区的 unified diff。支持选择未暂存或已暂存差异、上下文行数、超时和输出限制；使用参数化 Git 进程调用。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["unstaged"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示未暂存差异（默认 true）" },
            ["staged"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示已暂存差异（默认 false）" },
            ["context_lines"] = new JsonObject { ["type"] = "integer", ["description"] = "差异上下文行数（默认 3，最大 20）" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Git 命令超时秒数（默认 10，最大 60）" },
            ["max_output_chars"] = new JsonObject { ["type"] = "integer", ["description"] = "最大输出字符数（默认 50000，最大 200000）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var showUnstaged = ToolArgs.GetBool(args, "unstaged", true);
        var showStaged = ToolArgs.GetBool(args, "staged", false);
        if (!showUnstaged && !showStaged)
            throw new ToolException("unstaged 和 staged 不能同时为 false");
        var contextLines = Math.Clamp(ToolArgs.GetInt(args, "context_lines", 3), 0, 20);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 10), 1, 60);
        var maxOutput = Math.Clamp(ToolArgs.GetInt(args, "max_output_chars", 50_000), 1_000, 200_000);
        var scope = Path.GetRelativePath(ctx.Workspace.Root, root)
            .Replace('\\', '/')
            .TrimEnd('/');

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var output = new StringBuilder();
        if (showUnstaged)
        {
            var result = await RunDiffAsync(root, ["diff", "--no-ext-diff", $"--unified={contextLines}", "--", scope], timeout.Token);
            AppendSection(output, "未暂存差异", result);
        }
        if (showStaged)
        {
            var result = await RunDiffAsync(root, ["diff", "--cached", "--no-ext-diff", $"--unified={contextLines}", "--", scope], timeout.Token);
            AppendSection(output, "已暂存差异", result);
        }
        if (output.Length == 0)
            return "没有匹配的 Git 差异。";
        return Limit(output.ToString().TrimEnd(), maxOutput);
    }

    private static async Task<GitStatusTool.GitResult> RunDiffAsync(string root, IReadOnlyList<string> args, CancellationToken ct)
    {
        var result = await GitStatusTool.RunGitAsync(root, args, ct);
        if (result.ExitCode != 0)
            throw new ToolException($"git diff 失败（退出码 {result.ExitCode}）: {result.Error}");
        return result;
    }

    private static void AppendSection(StringBuilder output, string title, GitStatusTool.GitResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Output))
            return;
        if (output.Length > 0)
            output.AppendLine();
        output.AppendLine($"=== {title} ===");
        output.Append(result.Output.TrimEnd());
    }

    private static string Limit(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        const string marker = "\n…（Git diff 输出已截断）";
        return text[..Math.Max(0, maxChars - marker.Length)] + marker;
    }
}
