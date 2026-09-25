using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读比较两个 Git 引用之间指定路径的文件变化。</summary>
public sealed class GitPathDiffTool : ITool
{
    public string Name => "git_path_diff";
    public string Description =>
        "比较两个 Git 引用之间指定目录或文件的增删改状态，支持结果限制和超时，不修改仓库。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "要比较的仓库内路径，默认整个仓库" },
            ["from"] = new JsonObject { ["type"] = "string", ["description"] = "左侧引用，默认 HEAD" },
            ["to"] = new JsonObject { ["type"] = "string", ["description"] = "右侧引用，默认工作区 HEAD" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示变化数（默认 200，最大 5000）" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Git 命令超时秒数（默认 15，最大 120）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(null);
        var relative = string.IsNullOrWhiteSpace(requestedPath) ? null : Path.GetRelativePath(root, ctx.Workspace.ResolveRead(requestedPath)).Replace('\\', '/');
        var from = string.IsNullOrWhiteSpace(ToolArgs.GetString(args, "from")) ? "HEAD" : ToolArgs.GetString(args, "from");
        var to = string.IsNullOrWhiteSpace(ToolArgs.GetString(args, "to")) ? "HEAD" : ToolArgs.GetString(args, "to");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 200), 1, 5_000);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 15), 1, 120);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var command = new List<string> { "diff", "--name-status", "--find-renames", from, to, "--" };
        if (relative is not null)
            command.Add(relative);
        var result = await GitStatusTool.RunGitAsync(root, command, timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException($"git diff 失败（退出码 {result.ExitCode}）: {result.Error.Trim()}");
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToList();
        var output = new StringBuilder();
        output.AppendLine($"Git 路径变化: {from} -> {to}（{lines.Count:N0} 项）");
        foreach (var line in lines.Take(maxResults))
            output.AppendLine(line);
        if (lines.Count > maxResults)
            output.AppendLine($"…（另有 {lines.Count - maxResults} 项未显示）");
        return output.ToString().TrimEnd();
    }
}
