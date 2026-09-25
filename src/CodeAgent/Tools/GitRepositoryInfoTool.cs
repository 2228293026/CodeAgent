using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读汇总 Git 仓库位置、分支、浅克隆状态和对象计数。</summary>
public sealed class GitRepositoryInfoTool : ITool
{
    public string Name => "git_repository_info";
    public string Description =>
        "查看 Git 仓库根目录、当前分支、是否浅克隆以及对象数据库计数，不修改仓库。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Git 命令超时秒数（默认 10，最大 60）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var start = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(start))
            throw new ToolException($"目录不存在: {requestedPath}");
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 10), 1, 60);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var top = await GitStatusTool.RunGitAsync(start, ["rev-parse", "--show-toplevel"], timeout.Token);
        if (top.ExitCode != 0)
            throw new ToolException($"不是 Git 仓库或无法读取仓库信息: {top.Error.Trim()}");
        var repositoryRoot = top.Output.Trim();
        var branch = await GitStatusTool.RunGitAsync(start, ["branch", "--show-current"], timeout.Token);
        var shallow = await GitStatusTool.RunGitAsync(start, ["rev-parse", "--is-shallow-repository"], timeout.Token);
        var objects = await GitStatusTool.RunGitAsync(start, ["count-objects", "-v"], timeout.Token);
        if (objects.ExitCode != 0)
            throw new ToolException($"git count-objects 失败（退出码 {objects.ExitCode}）: {objects.Error}");
        var output = new StringBuilder();
        output.AppendLine("Git 仓库信息:");
        output.AppendLine($"  根目录: {repositoryRoot}");
        output.AppendLine($"  当前分支: {(branch.ExitCode == 0 && !string.IsNullOrWhiteSpace(branch.Output) ? branch.Output.Trim() : "(detached HEAD)")}");
        output.AppendLine($"  浅克隆: {(shallow.ExitCode == 0 && shallow.Output.Trim() == "true" ? "是" : "否")}");
        output.AppendLine("  对象计数:");
        foreach (var line in objects.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            output.AppendLine($"    {line.Trim()}");
        return output.ToString().TrimEnd();
    }
}
