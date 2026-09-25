using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>以只读方式查看指定 Git 提交的元数据和补丁。</summary>
public sealed class GitShowTool : ITool
{
    public string Name => "git_show";
    public string Description =>
        "查看指定 Git 提交（默认 HEAD）的作者、时间、消息、统计和 unified diff；支持路径筛选、超时和输出限制。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["revision"] = new JsonObject { ["type"] = "string", ["description"] = "提交哈希、分支或标签，默认 HEAD" },
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "只查看某个仓库子目录/文件，默认整个仓库" },
            ["include_diff"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含提交补丁（默认 true）" },
            ["include_stat"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含文件统计（默认 true）" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Git 命令超时秒数（默认 10，最大 60）" },
            ["max_output_chars"] = new JsonObject { ["type"] = "integer", ["description"] = "最大输出字符数（默认 50000，最大 200000）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var revision = ToolArgs.GetString(args, "revision");
        if (string.IsNullOrWhiteSpace(revision))
            revision = "HEAD";
        if (revision.StartsWith('-') || revision.Any(char.IsWhiteSpace))
            throw new ToolException("revision 不能包含空白或以 '-' 开头");
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root) && !File.Exists(root))
            throw new ToolException($"路径不存在: {requestedPath}");
        var includeDiff = ToolArgs.GetBool(args, "include_diff", true);
        var includeStat = ToolArgs.GetBool(args, "include_stat", true);
        var maxOutput = Math.Clamp(ToolArgs.GetInt(args, "max_output_chars", 50_000), 1_000, 200_000);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 10), 1, 60);
        var scope = string.IsNullOrWhiteSpace(requestedPath) ? null : Path.GetRelativePath(ctx.Workspace.Root, root).Replace('\\', '/');

        var command = new List<string>
        {
            "show",
            "--root",
            "--no-ext-diff",
            "--no-color",
            "--format=fuller",
        };
        if (includeStat)
            command.Add("--stat");
        if (includeDiff)
            command.Add("--patch");
        else
            command.Add("--no-patch");
        command.Add(revision);
        if (scope is not null)
        {
            command.Add("--");
            command.Add(scope);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var result = await GitStatusTool.RunGitAsync(ctx.Workspace.Root, command, timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException($"git show 失败（退出码 {result.ExitCode}）: {result.Error}");
        return Limit($"Git show: {revision}\n{result.Output.TrimEnd()}", maxOutput);
    }

    private static string Limit(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        const string marker = "\n…（Git show 输出已截断）";
        return text[..Math.Max(0, maxChars - marker.Length)] + marker;
    }
}
