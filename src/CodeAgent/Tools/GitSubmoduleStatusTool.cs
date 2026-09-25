using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读查看 Git 子模块状态和提交指针。</summary>
public sealed class GitSubmoduleStatusTool : ITool
{
    public string Name => "git_submodule_status";
    public string Description =>
        "查看 Git 子模块的初始化状态、提交指针和递归子模块，不修改仓库。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["recursive"] = new JsonObject { ["type"] = "boolean", ["description"] = "递归检查嵌套子模块（默认 false）" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示子模块数（默认 200，最大 5000）" },
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
        var recursive = ToolArgs.GetBool(args, "recursive", false);
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 200), 1, 5_000);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 15), 1, 120);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var command = new List<string> { "submodule", "status" };
        if (recursive)
            command.Add("--recursive");
        var result = await GitStatusTool.RunGitAsync(start, command, timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException($"git submodule status 失败（退出码 {result.ExitCode}）: {result.Error.Trim()}");
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.TrimEnd('\r'))
            .ToList();
        var output = new StringBuilder();
        output.AppendLine($"Git 子模块状态: {lines.Count:N0} 个");
        foreach (var line in lines.Take(maxResults))
            output.AppendLine(line);
        if (lines.Count > maxResults)
            output.AppendLine($"…（另有 {lines.Count - maxResults} 个子模块未显示）");
        return output.ToString().TrimEnd();
    }
}
