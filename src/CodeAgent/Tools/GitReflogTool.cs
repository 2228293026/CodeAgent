using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>以只读方式查看 Git reflog，了解 HEAD 和分支最近移动记录。</summary>
public sealed class GitReflogTool : ITool
{
    public string Name => "git_reflog";
    public string Description =>
        "查看 Git HEAD 或全部分支的 reflog（短哈希、reflog 标识和操作说明），支持数量、超时和输出限制。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["include_all"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含全部分支/引用而非仅 HEAD（默认 false）" },
            ["max_entries"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示记录数（默认 50，最大 500）" },
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
        var includeAll = ToolArgs.GetBool(args, "include_all", false);
        var command = new List<string>
        {
            "reflog",
            "--date=short",
            $"--max-count={maxEntries}",
            "--format=%h %gd %gs",
        };
        if (includeAll)
            command.Add("--all");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var result = await GitStatusTool.RunGitAsync(ctx.Workspace.Root, command, timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException($"git reflog 失败（退出码 {result.ExitCode}）: {result.Error}");
        if (string.IsNullOrWhiteSpace(result.Output))
            return "没有 Git reflog 记录。";
        return Limit($"Git reflog ({(includeAll ? "全部引用" : "HEAD")}):\n{result.Output.TrimEnd()}", maxOutput);
    }

    private static string Limit(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        const string marker = "\n…（Git reflog 输出已截断）";
        return text[..Math.Max(0, maxChars - marker.Length)] + marker;
    }
}
