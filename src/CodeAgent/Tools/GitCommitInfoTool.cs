using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读查看 Git 提交元数据和父提交关系。</summary>
public sealed class GitCommitInfoTool : ITool
{
    public string Name => "git_commit_info";
    public string Description =>
        "查看 Git 提交的完整 ID、Tree、父提交、作者、提交者和提交说明，不修改仓库。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["commit"] = new JsonObject { ["type"] = "string", ["description"] = "提交哈希或引用名，默认 HEAD" },
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["include_body"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含提交正文（默认 true）" },
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
        var commit = ToolArgs.GetString(args, "commit");
        if (string.IsNullOrWhiteSpace(commit))
            commit = "HEAD";
        var includeBody = ToolArgs.GetBool(args, "include_body", true);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 10), 1, 60);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var format = "%H%n%T%n%P%n%an <%ae>%n%cn <%ce>%n%aI%n%cI%n%s" + (includeBody ? "%n%b" : "");
        var result = await GitStatusTool.RunGitAsync(start, ["show", "-s", $"--format={format}", commit], timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException($"无法读取 Git 提交: {commit}");
        var fields = result.Output.Replace("\r\n", "\n").Split('\n');
        string Field(int index) => index < fields.Length ? fields[index].Trim() : "(无)";
        var output = $"Git 提交: {Field(0)}\n  Tree: {Field(1)}\n  父提交: {(string.IsNullOrWhiteSpace(Field(2)) ? "(根提交)" : Field(2))}\n  作者: {Field(3)}\n  提交者: {Field(4)}\n  作者时间: {Field(5)}\n  提交时间: {Field(6)}\n  摘要: {Field(7)}";
        if (includeBody)
        {
            var body = fields.Skip(8).Any() ? string.Join('\n', fields.Skip(8)).Trim() : "";
            if (body.Length > 0)
                output += $"\n  正文:\n{body}";
        }
        return output;
    }
}
