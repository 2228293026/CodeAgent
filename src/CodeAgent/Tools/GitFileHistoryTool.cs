using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读查看单个文件的 Git 提交历史，并跟随重命名。</summary>
public sealed class GitFileHistoryTool : ITool
{
    public string Name => "git_file_history";
    public string Description =>
        "查看指定文件的 Git 提交历史，支持跟随重命名、限制提交数量和超时，不修改仓库。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "要查看历史的文件路径" },
            ["max_commits"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示提交数（默认 50，最大 1000）" },
            ["follow"] = new JsonObject { ["type"] = "boolean", ["description"] = "跟随文件重命名（默认 true）" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Git 命令超时秒数（默认 15，最大 120）" },
        },
        ["required"] = new JsonArray("path"),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = ToolArgs.GetString(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            throw new ToolException("缺少必填参数 path");
        var full = ctx.Workspace.ResolveRead(path);
        var root = ctx.Workspace.ResolveRead(null);
        var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        var maxCommits = Math.Clamp(ToolArgs.GetInt(args, "max_commits", 50), 1, 1_000);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 15), 1, 120);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var command = new List<string> { "log", $"--max-count={maxCommits}", "--date=short", "--format=%h %ad %an %s" };
        if (ToolArgs.GetBool(args, "follow", true))
            command.Add("--follow");
        command.Add("--");
        command.Add(relative);
        var result = await GitStatusTool.RunGitAsync(root, command, timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException($"git log 失败（退出码 {result.ExitCode}）: {result.Error.Trim()}");
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimEnd('\r')).ToList();
        var output = new StringBuilder();
        output.AppendLine($"Git 文件历史: {relative}（{lines.Count} 条）");
        foreach (var line in lines)
            output.AppendLine(line);
        return output.ToString().TrimEnd();
    }
}
