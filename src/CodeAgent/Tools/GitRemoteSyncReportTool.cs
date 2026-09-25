using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读比较本地分支与其上游远程分支的领先/落后数量，标出需要推送或拉取的分支。</summary>
public sealed class GitRemoteSyncReportTool : ITool
{
    public string Name => "git_remote_sync_report";
    public string Description =>
        "只读比较当前分支与其上游远程分支，输出领先/落后提交数，并标出需要推送或需要拉取的分支。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示分支数（默认 100，最大 1000）" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Git 命令超时秒数（默认 20，最大 120）" },
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
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 20), 1, 120);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var probe = await GitStatusTool.RunGitAsync(start, ["rev-parse", "--git-dir"], timeout.Token);
        if (probe.ExitCode != 0)
            throw new ToolException("当前目录不是 Git 仓库");
        var current = await GitStatusTool.RunGitAsync(start, ["rev-parse", "--abbrev-ref", "HEAD"], timeout.Token);
        var currentName = current.ExitCode == 0 ? current.Output.Trim() : string.Empty;
        var list = await GitStatusTool.RunGitAsync(
            start, ["for-each-ref", "--format=%(refname:short)\t%(upstream:short)", "refs/heads"], timeout.Token);
        if (list.ExitCode != 0)
            throw new ToolException($"无法读取分支（退出码 {list.ExitCode}）");
        var rows = new List<(string Branch, string Upstream, int Ahead, int Behind, bool Comparable)>();
        foreach (var line in list.Output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            ct.ThrowIfCancellationRequested();
            var parts = line.Split('\t');
            var branch = parts[0];
            var upstream = parts.Length > 1 ? parts[1] : string.Empty;
            if (upstream.Length == 0)
            {
                rows.Add((branch, "(无上游)", 0, 0, false));
                continue;
            }
            var counts = await GitStatusTool.RunGitAsync(
                start, ["rev-list", "--left-right", "--count", $"{upstream}...{branch}"], timeout.Token);
            if (counts.ExitCode != 0)
            {
                rows.Add((branch, upstream, 0, 0, false));
                continue;
            }
            var nums = counts.Output.Replace("\r\n", "\n").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (nums.Length < 2 || !int.TryParse(nums[0], out var behind) || !int.TryParse(nums[1], out var ahead))
            {
                rows.Add((branch, upstream, 0, 0, false));
                continue;
            }
            rows.Add((branch, upstream, ahead, behind, true));
        }
        if (rows.Count == 0)
            return "Git 远程同步报告: 没有本地分支";
        var needPush = rows.Count(r => r.Comparable && r.Ahead > 0);
        var needPull = rows.Count(r => r.Comparable && r.Behind > 0);
        var noUpstream = rows.Count(r => !r.Comparable);
        var output = new StringBuilder();
        output.AppendLine($"Git 远程同步报告: {rows.Count} 个分支");
        output.AppendLine($"  需推送: {needPush}   需拉取: {needPull}   无上游/无法比较: {noUpstream}");
        foreach (var row in rows.Take(maxResults))
        {
            var mark = row.Branch == currentName ? " *" : string.Empty;
            if (!row.Comparable)
            {
                output.AppendLine($"  {row.Branch} → {row.Upstream}：无法比较{mark}");
                continue;
            }
            var state = row.Ahead > 0 && row.Behind > 0 ? "⚠双向分歧"
                : row.Ahead > 0 ? "↑待推送"
                : row.Behind > 0 ? "↓待拉取"
                : "✓ 同步";
            output.AppendLine($"  {row.Branch} → {row.Upstream}：领先 {row.Ahead} / 落后 {row.Behind} {state}{mark}");
        }
        if (rows.Count > maxResults)
            output.AppendLine($"…（另有 {rows.Count - maxResults} 个分支未显示）");
        output.AppendLine("（* 为当前分支）");
        return output.ToString().TrimEnd();
    }
}
