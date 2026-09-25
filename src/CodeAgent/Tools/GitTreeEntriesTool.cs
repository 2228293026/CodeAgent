using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读列出 Git tree 对象中的路径、类型和对象 ID。</summary>
public sealed class GitTreeEntriesTool : ITool
{
    public string Name => "git_tree_entries";
    public string Description =>
        "查看指定 Git tree/提交的目录条目、对象类型和对象 ID，支持递归遍历与结果限制。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["object"] = new JsonObject { ["type"] = "string", ["description"] = "Tree 或提交对象 ID，默认 HEAD" },
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["recursive"] = new JsonObject { ["type"] = "boolean", ["description"] = "递归列出所有文件（默认 false）" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示条目数（默认 500，最大 10000）" },
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
        var objectId = ToolArgs.GetString(args, "object");
        if (string.IsNullOrWhiteSpace(objectId))
            objectId = "HEAD";
        var recursive = ToolArgs.GetBool(args, "recursive", false);
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 500), 1, 10_000);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 15), 1, 120);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var command = new List<string> { "ls-tree" };
        if (recursive)
            command.Add("-r");
        command.Add(objectId);
        var result = await GitStatusTool.RunGitAsync(start, command, timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException($"git ls-tree 失败（退出码 {result.ExitCode}）: {result.Error.Trim()}");
        var output = new StringBuilder();
        var count = 0;
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (count >= maxResults)
                break;
            var trimmed = line.TrimEnd('\r');
            var tab = trimmed.IndexOf('\t');
            if (tab <= 0)
                continue;
            var metadata = trimmed[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (metadata.Length < 3)
                continue;
            output.AppendLine($"{metadata[1]} {metadata[2]} {trimmed[(tab + 1)..]}");
            count++;
        }
        var total = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        return $"Git tree 条目: {objectId}（显示 {count:N0} / {total:N0}）\n{output.ToString().TrimEnd()}";
    }
}
