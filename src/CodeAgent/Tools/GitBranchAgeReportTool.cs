using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读按最后提交时间列出本地分支，标出长期未更新的陈旧分支。</summary>
public sealed class GitBranchAgeReportTool : ITool
{
    /// <summary>判定「陈旧」的天数阈值：超过该天数未提交的分支标为陈旧。</summary>
    public const int StaleDays = 90;

    public string Name => "git_branch_age_report";
    public string Description =>
        "只读按最后提交时间排序本地分支，标出超过阈值天数未更新的陈旧分支。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["stale_days"] = new JsonObject { ["type"] = "integer", ["description"] = $"陈旧判定天数（默认 {StaleDays}，最大 3650）" },
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
        var staleDays = Math.Clamp(ToolArgs.GetInt(args, "stale_days", StaleDays), 1, 3650);
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 100), 1, 1_000);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 20), 1, 120);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var probe = await GitStatusTool.RunGitAsync(start, ["rev-parse", "--git-dir"], timeout.Token);
        if (probe.ExitCode != 0)
            throw new ToolException("当前目录不是 Git 仓库");
        var result = await GitStatusTool.RunGitAsync(
            start,
            ["for-each-ref", "--format=%(refname:short)\t%(committerdate:iso-strict)\t%(objectname:short)", "refs/heads"],
            timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException($"无法读取分支（退出码 {result.ExitCode}）");
        var current = await GitStatusTool.RunGitAsync(start, ["rev-parse", "--abbrev-ref", "HEAD"], timeout.Token);
        var currentName = current.ExitCode == 0 ? current.Output.Trim() : string.Empty;
        var branches = new List<(string Name, DateTimeOffset When, string Sha)>();
        foreach (var line in result.Output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 2)
                continue;
            if (!DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var when))
                continue;
            branches.Add((parts[0], when, parts.Length > 2 ? parts[2] : ""));
        }
        if (branches.Count == 0)
            return "Git 分支时效报告: 没有本地分支";
        branches.Sort((a, b) => b.When.CompareTo(a.When));
        var now = DateTimeOffset.UtcNow;
        var stale = branches.Count(b => (now - b.When).TotalDays > staleDays);
        var output = new StringBuilder();
        output.AppendLine($"Git 分支时效报告: {branches.Count} 个分支，陈旧（>{staleDays} 天未提交）: {stale}");
        foreach (var branch in branches.Take(maxResults))
        {
            var days = (int)(now - branch.When).TotalDays;
            var mark = days > staleDays ? " ⚠陈旧" : string.Empty;
            var head = branch.Name == currentName ? " *" : string.Empty;
            output.AppendLine($"  {branch.When:yyyy-MM-dd} {days,5} 天前 {branch.Sha,-8} {branch.Name}{head}{mark}");
        }
        if (branches.Count > maxResults)
            output.AppendLine($"…（另有 {branches.Count - maxResults} 个分支未显示）");
        output.AppendLine("（* 为当前分支）");
        return output.ToString().TrimEnd();
    }
}
