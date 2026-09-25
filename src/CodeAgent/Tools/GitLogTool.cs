using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>以只读方式查看 Git 提交历史，可按路径、关键字和作者筛选。</summary>
public sealed class GitLogTool : ITool
{
    public string Name => "git_log";
    public string Description =>
        "查看 Git 提交历史（短哈希、日期、作者、主题），支持路径范围、关键字/作者筛选、文件变更列表、超时和输出限制。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "只查看某个仓库子目录的历史，默认工作区根目录" },
            ["max_commits"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示提交数（默认 20，最大 200）" },
            ["grep"] = new JsonObject { ["type"] = "string", ["description"] = "按提交消息关键字筛选" },
            ["author"] = new JsonObject { ["type"] = "string", ["description"] = "按作者筛选" },
            ["include_files"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示每个提交涉及的文件（默认 false）" },
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
        var maxCommits = Math.Clamp(ToolArgs.GetInt(args, "max_commits", 20), 1, 200);
        var maxOutput = Math.Clamp(ToolArgs.GetInt(args, "max_output_chars", 30_000), 1_000, 200_000);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 10), 1, 60);
        var scope = Path.GetRelativePath(ctx.Workspace.Root, root)
            .Replace('\\', '/')
            .TrimEnd('/');
        var command = new List<string>
        {
            "log",
            $"--max-count={maxCommits}",
            "--date=short",
            "--pretty=format:%h %ad %an %s",
        };
        var grep = ToolArgs.GetString(args, "grep");
        if (!string.IsNullOrWhiteSpace(grep))
        {
            command.Add("--grep");
            command.Add(grep);
        }
        var author = ToolArgs.GetString(args, "author");
        if (!string.IsNullOrWhiteSpace(author))
        {
            command.Add("--author");
            command.Add(author);
        }
        if (ToolArgs.GetBool(args, "include_files", false))
            command.Add("--name-status");
        command.Add("--");
        command.Add(scope);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var result = await GitStatusTool.RunGitAsync(root, command, timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException($"git log 失败（退出码 {result.ExitCode}）: {result.Error}");
        if (string.IsNullOrWhiteSpace(result.Output))
            return "没有匹配的 Git 提交记录。";
        return Limit(result.Output.TrimEnd(), maxOutput);
    }

    private static string Limit(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        const string marker = "\n…（Git log 输出已截断）";
        return text[..Math.Max(0, maxChars - marker.Length)] + marker;
    }
}
