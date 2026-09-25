using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读比较两个 Git 引用的领先/落后提交和共同基点。</summary>
public sealed class GitCompareRefsTool : ITool
{
    public string Name => "git_compare_refs";
    public string Description =>
        "比较两个 Git 引用的共同祖先、领先和落后提交数量，不修改仓库。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["left"] = new JsonObject { ["type"] = "string", ["description"] = "左侧引用，默认 HEAD" },
            ["right"] = new JsonObject { ["type"] = "string", ["description"] = "右侧引用，默认 HEAD" },
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
        var left = string.IsNullOrWhiteSpace(ToolArgs.GetString(args, "left")) ? "HEAD" : ToolArgs.GetString(args, "left");
        var right = string.IsNullOrWhiteSpace(ToolArgs.GetString(args, "right")) ? "HEAD" : ToolArgs.GetString(args, "right");
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 10), 1, 60);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var count = await GitStatusTool.RunGitAsync(start, ["rev-list", "--left-right", "--count", $"{left}...{right}"], timeout.Token);
        if (count.ExitCode != 0)
            throw new ToolException($"无法比较 Git 引用: {left}...{right}（{count.Error.Trim()}）");
        var parts = count.Output.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], out var ahead) || !int.TryParse(parts[1], out var behind))
            throw new ToolException("无法解析 Git 引用比较结果");
        var mergeBase = await GitStatusTool.RunGitAsync(start, ["merge-base", left, right], timeout.Token);
        return $"Git 引用比较: {left}...{right}\n  {left} 独有提交: {ahead}\n  {right} 独有提交: {behind}\n  共同祖先: {(mergeBase.ExitCode == 0 ? mergeBase.Output.Trim() : "(无共同祖先)")}";
    }
}
