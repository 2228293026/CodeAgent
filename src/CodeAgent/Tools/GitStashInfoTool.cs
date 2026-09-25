using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读查看 Git stash 的提交元数据和变更文件。</summary>
public sealed class GitStashInfoTool : ITool
{
    public string Name => "git_stash_info";
    public string Description =>
        "查看指定 Git stash 的哈希、父提交、作者、摘要和变更文件，不修改仓库。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["stash"] = new JsonObject { ["type"] = "string", ["description"] = "stash 引用，默认 stash@{0}" },
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["include_untracked"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含未跟踪文件变更（默认 true）" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示变更文件数（默认 200，最大 5000）" },
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
        var stash = string.IsNullOrWhiteSpace(ToolArgs.GetString(args, "stash")) ? "stash@{0}" : ToolArgs.GetString(args, "stash");
        var includeUntracked = ToolArgs.GetBool(args, "include_untracked", true);
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 200), 1, 5_000);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 15), 1, 120);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var meta = await GitStatusTool.RunGitAsync(start, ["show", "-s", "--format=%H%n%P%n%an <%ae>%n%aI%n%s", stash], timeout.Token);
        if (meta.ExitCode != 0)
            throw new ToolException($"无法读取 Git stash: {stash}");
        var fields = meta.Output.Replace("\r\n", "\n").Split('\n');
        string Field(int index) => index < fields.Length ? fields[index].Trim() : "(无)";
        var filesCommand = new List<string> { "stash", "show", "--name-status" };
        if (includeUntracked) filesCommand.Add("--include-untracked");
        filesCommand.Add(stash);
        var files = await GitStatusTool.RunGitAsync(start, filesCommand, timeout.Token);
        if (files.ExitCode != 0)
            throw new ToolException($"无法读取 stash 变更: {stash}");
        var lines = files.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimEnd('\r')).ToList();
        var output = new StringBuilder();
        output.AppendLine($"Git stash: {stash}");
        output.AppendLine($"  提交: {Field(0)}");
        output.AppendLine($"  父提交: {(string.IsNullOrWhiteSpace(Field(1)) ? "(无)" : Field(1))}");
        output.AppendLine($"  作者: {Field(2)}");
        output.AppendLine($"  时间: {Field(3)}");
        output.AppendLine($"  摘要: {Field(4)}");
        output.AppendLine($"  变更文件: {lines.Count:N0}");
        foreach (var line in lines.Take(maxResults))
            output.AppendLine($"    {line}");
        if (lines.Count > maxResults)
            output.AppendLine($"…（另有 {lines.Count - maxResults} 个文件未显示）");
        return output.ToString().TrimEnd();
    }
}
