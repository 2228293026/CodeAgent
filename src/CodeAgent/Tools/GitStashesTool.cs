using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>以只读方式查看 Git stash 条目。</summary>
public sealed class GitStashesTool : ITool
{
    public string Name => "git_stashes";
    public string Description =>
        "查看 Git stash 列表（stash 标识、短哈希、日期和说明），支持数量限制、超时和取消。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["max_entries"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示 stash 数（默认 50，最大 500）" },
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
        var maxEntries = Math.Clamp(ToolArgs.GetInt(args, "max_entries", 50), 1, 500);
        var maxOutput = Math.Clamp(ToolArgs.GetInt(args, "max_output_chars", 30_000), 1_000, 200_000);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 10), 1, 60);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var result = await GitStatusTool.RunGitAsync(
            ctx.Workspace.Root,
            ["stash", "list", "--date=short", $"--max-count={maxEntries}", "--format=%gd %h %gs"],
            timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException($"git stash list 失败（退出码 {result.ExitCode}）: {result.Error}");
        if (string.IsNullOrWhiteSpace(result.Output))
            return "没有 Git stash。";
        return Limit("Git stash:\n" + result.Output.TrimEnd(), maxOutput);
    }

    private static string Limit(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        const string marker = "\n…（Git stash 输出已截断）";
        return text[..Math.Max(0, maxChars - marker.Length)] + marker;
    }
}
