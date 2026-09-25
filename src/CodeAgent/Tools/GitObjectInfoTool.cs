using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读检查 Git 对象的类型和大小。</summary>
public sealed class GitObjectInfoTool : ITool
{
    public string Name => "git_object_info";
    public string Description =>
        "检查 Git 对象、提交或树对象的类型和大小，支持任意对象 ID，不修改仓库。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["object"] = new JsonObject { ["type"] = "string", ["description"] = "对象 ID、提交哈希或引用名，默认 HEAD" },
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
        var objectId = ToolArgs.GetString(args, "object");
        if (string.IsNullOrWhiteSpace(objectId))
            objectId = "HEAD";
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 10), 1, 60);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var resolved = await GitStatusTool.RunGitAsync(start, ["rev-parse", "--verify", objectId], timeout.Token);
        if (resolved.ExitCode != 0)
            throw new ToolException($"无法解析 Git 对象: {objectId}");
        var id = resolved.Output.Trim();
        var type = await GitStatusTool.RunGitAsync(start, ["cat-file", "-t", id], timeout.Token);
        if (type.ExitCode != 0)
            throw new ToolException($"无法读取 Git 对象类型: {id}");
        var size = await GitStatusTool.RunGitAsync(start, ["cat-file", "-s", id], timeout.Token);
        if (size.ExitCode != 0)
            throw new ToolException($"无法读取 Git 对象大小: {id}");
        return $"Git 对象: {id}\n  类型: {type.Output.Trim()}\n  大小: {long.Parse(size.Output.Trim()):N0} 字节";
    }
}
