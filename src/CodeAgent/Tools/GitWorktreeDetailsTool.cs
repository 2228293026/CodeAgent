using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读查看 linked worktree 的 HEAD、分支和锁定状态。</summary>
public sealed class GitWorktreeDetailsTool : ITool
{
    public string Name => "git_worktree_details";
    public string Description =>
        "查看 Git linked worktree 的路径、HEAD、分支、detached/bare 状态及锁定信息，不修改仓库。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示 worktree 数（默认 100，最大 1000）" },
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
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 100), 1, 1_000);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 15), 1, 120);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var result = await GitStatusTool.RunGitAsync(start, ["worktree", "list", "--porcelain"], timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException($"git worktree list 失败（退出码 {result.ExitCode}）: {result.Error.Trim()}");
        var records = new List<Dictionary<string, string>>();
        Dictionary<string, string>? current = null;
        foreach (var line in result.Output.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0)
            {
                if (current is not null) records.Add(current);
                current = null;
                continue;
            }
            current ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var space = line.IndexOf(' ');
            if (space < 0) current[line] = "true";
            else current[line[..space]] = line[(space + 1)..];
        }
        if (current is not null) records.Add(current);
        var output = new StringBuilder();
        output.AppendLine($"Git worktree 详情: {records.Count:N0} 个");
        foreach (var record in records.Take(maxResults))
        {
            output.AppendLine($"  路径: {record.GetValueOrDefault("worktree", "(未知)")}");
            output.AppendLine($"    HEAD: {record.GetValueOrDefault("HEAD", "(未知)")}");
            output.AppendLine($"    分支: {record.GetValueOrDefault("branch", "(detached)")}");
            if (record.ContainsKey("bare")) output.AppendLine("    bare: 是");
            if (record.ContainsKey("detached")) output.AppendLine("    detached: 是");
            if (record.TryGetValue("locked", out var locked)) output.AppendLine($"    锁定: {locked}");
            if (record.TryGetValue("prunable", out var prunable)) output.AppendLine($"    可清理: {prunable}");
        }
        if (records.Count > maxResults)
            output.AppendLine($"…（另有 {records.Count - maxResults} 个 worktree 未显示）");
        return output.ToString().TrimEnd();
    }
}
