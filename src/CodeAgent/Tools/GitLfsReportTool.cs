using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读查看 Git LFS 是否可用、跟踪模式和已跟踪文件。</summary>
public sealed class GitLfsReportTool : ITool
{
    public string Name => "git_lfs_report";
    public string Description =>
        "只读检查 Git LFS 可用性、跟踪模式（.gitattributes）和已跟踪的大文件，不执行下载或修改。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示跟踪文件数（默认 200，最大 5000）" },
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
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 200), 1, 5_000);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 20), 1, 120);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var env = await GitStatusTool.RunGitAsync(start, ["lfs", "env"], timeout.Token);
        if (env.ExitCode != 0)
            return "Git LFS 报告: 当前环境未提供可用的 git-lfs（仅显示不可用状态）";
        var output = new StringBuilder();
        output.AppendLine("Git LFS 报告: 可用");
        var track = await GitStatusTool.RunGitAsync(start, ["lfs", "track"], timeout.Token);
        if (track.ExitCode == 0 && track.Output.Trim().Length > 0)
        {
            output.AppendLine("跟踪模式:");
            foreach (var line in track.Output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
                output.AppendLine($"  {line.Trim()}");
        }
        else
        {
            output.AppendLine("跟踪模式: (无)");
        }
        var files = await GitStatusTool.RunGitAsync(start, ["lfs", "ls-files", "--long"], timeout.Token);
        var lines = files.ExitCode == 0
            ? files.Output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToList()
            : new List<string>();
        output.AppendLine($"已跟踪文件: {lines.Count:N0}");
        foreach (var line in lines.Take(maxResults))
            output.AppendLine($"  {line}");
        if (lines.Count > maxResults)
            output.AppendLine($"…（另有 {lines.Count - maxResults} 个文件未显示）");
        return output.ToString().TrimEnd();
    }
}
