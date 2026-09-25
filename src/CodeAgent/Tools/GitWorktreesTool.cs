using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>以只读方式查看 Git linked worktree、分支和 HEAD 状态。</summary>
public sealed class GitWorktreesTool : ITool
{
    public string Name => "git_worktrees";
    public string Description =>
        "查看 Git linked worktree 列表、HEAD、分支和锁定/prunable 状态；外部 worktree 路径只显示目录名，避免泄露工作区外绝对路径。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Git 命令超时秒数（默认 10，最大 60）" },
            ["max_output_chars"] = new JsonObject { ["type"] = "integer", ["description"] = "最大输出字符数（默认 30000，最大 200000）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxOutput = Math.Clamp(ToolArgs.GetInt(args, "max_output_chars", 30_000), 1_000, 200_000);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 10), 1, 60);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var result = await GitStatusTool.RunGitAsync(ctx.Workspace.Root, ["worktree", "list", "--porcelain"], timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException($"git worktree list 失败（退出码 {result.ExitCode}）: {result.Error}");

        var output = new StringBuilder();
        output.AppendLine("Git worktree:");
        foreach (var block in result.Output.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string? path = null;
            string? head = null;
            string? branch = null;
            var flags = new List<string>();
            foreach (var line in block.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("worktree ", StringComparison.Ordinal)) path = line[9..].Trim();
                else if (line.StartsWith("HEAD ", StringComparison.Ordinal)) head = line[5..].Trim();
                else if (line.StartsWith("branch ", StringComparison.Ordinal)) branch = line[7..].Trim();
                else if (line is "detached" or "bare" or "locked" or "prunable") flags.Add(line);
            }
            if (path is null)
                continue;
            output.AppendLine($"- {DisplayPath(path, ctx.Workspace.Root)}  HEAD: {head ?? "(未知)"}  branch: {branch ?? "detached"}" +
                              (flags.Count == 0 ? "" : $"  [{string.Join(", ", flags)}]"));
        }
        return Limit(output.ToString().TrimEnd(), maxOutput);
    }

    private static string DisplayPath(string path, string workspaceRoot)
    {
        var relative = Path.GetRelativePath(workspaceRoot, path).Replace('\\', '/');
        if (relative == ".")
            return ".";
        if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return "<external>/" + Path.GetFileName(path);
        return relative;
    }

    private static string Limit(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        const string marker = "\n…（Git worktree 输出已截断）";
        return text[..Math.Max(0, maxChars - marker.Length)] + marker;
    }
}
