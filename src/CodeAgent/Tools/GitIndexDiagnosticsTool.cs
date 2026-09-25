using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读检查 Git 索引中的未合并条目和冲突阶段。</summary>
public sealed class GitIndexDiagnosticsTool : ITool
{
    public string Name => "git_index_diagnostics";
    public string Description =>
        "检查 Git 索引中的未合并文件、各阶段对象和冲突数量，不修改仓库。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示冲突条目数（默认 200，最大 5000）" },
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
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 200), 1, 5_000);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 15), 1, 120);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var result = await GitStatusTool.RunGitAsync(start, ["ls-files", "--unmerged", "-z"], timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException($"git ls-files 失败（退出码 {result.ExitCode}）: {result.Error.Trim()}");
        var entries = new List<string>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = record.IndexOf('\t');
            if (tab <= 0)
                continue;
            var metadata = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (metadata.Length < 3)
                continue;
            var path = record[(tab + 1)..].Replace('\\', '/');
            paths.Add(path);
            entries.Add($"阶段 {metadata[2]}: {path} ({metadata[1]})");
        }
        var output = new StringBuilder();
        output.AppendLine($"Git 索引诊断: {paths.Count:N0} 个未合并文件，{entries.Count:N0} 个阶段条目");
        foreach (var entry in entries.Take(maxResults))
            output.AppendLine(entry);
        if (entries.Count > maxResults)
            output.AppendLine($"…（另有 {entries.Count - maxResults} 个阶段条目未显示）");
        return output.ToString().TrimEnd();
    }
}
