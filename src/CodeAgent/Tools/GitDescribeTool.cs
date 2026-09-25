using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读生成 Git 版本描述（最近标签、提交和 dirty 状态）。</summary>
public sealed class GitDescribeTool : ITool
{
    public string Name => "git_describe";
    public string Description =>
        "使用 git describe 生成当前版本描述，支持长格式、标签匹配、always 和 dirty 状态，不修改仓库。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["long_format"] = new JsonObject { ["type"] = "boolean", ["description"] = "使用长格式输出（默认 false）" },
            ["always"] = new JsonObject { ["type"] = "boolean", ["description"] = "没有标签时也输出提交哈希（默认 true）" },
            ["dirty"] = new JsonObject { ["type"] = "boolean", ["description"] = "检查工作区 dirty 标记（默认 true）" },
            ["match"] = new JsonObject { ["type"] = "string", ["description"] = "只匹配符合模式的标签，如 v*" },
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
        var command = new List<string> { "describe", "--tags" };
        if (ToolArgs.GetBool(args, "always", true))
            command.Add("--always");
        if (ToolArgs.GetBool(args, "long_format", false))
            command.Add("--long");
        if (ToolArgs.GetBool(args, "dirty", true))
            command.Add("--dirty");
        var match = ToolArgs.GetString(args, "match");
        if (!string.IsNullOrWhiteSpace(match))
            command.AddRange(new[] { "--match", match });
        var result = await GitStatusTool.RunGitAsync(start, command, timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException($"git describe 失败（退出码 {result.ExitCode}）: {result.Error.Trim()}");
        return "Git 版本描述: " + result.Output.Trim();
    }
}
