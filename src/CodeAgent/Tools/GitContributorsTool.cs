using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读统计 Git 仓库贡献者及提交数量。</summary>
public sealed class GitContributorsTool : ITool
{
    public string Name => "git_contributors";
    public string Description =>
        "统计所有引用中的 Git 提交贡献者数量，可选择是否显示邮箱、限制结果数和控制命令超时。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["include_email"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示贡献者邮箱（默认 false）" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示贡献者数（默认 100，最大 5000）" },
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
        var includeEmail = ToolArgs.GetBool(args, "include_email", false);
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 100), 1, 5_000);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 15), 1, 120);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var result = await GitStatusTool.RunGitAsync(start, ["log", "--all", "--format=%aN%x09%aE"], timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException($"git log 失败（退出码 {result.ExitCode}）: {result.Error.Trim()}");
        var contributors = new List<(long Count, string Name, string Email)>();
        var counts = new Dictionary<(string Name, string Email), long>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\t', 2);
            var name = fields[0].Trim();
            var email = fields.Length > 1 ? fields[1].Trim() : "";
            if (name.Length == 0)
                continue;
            var key = (name, email);
            counts[key] = counts.TryGetValue(key, out var current) ? current + 1 : 1;
        }
        contributors.AddRange(counts.Select(pair => (pair.Value, pair.Key.Name, pair.Key.Email)));
        contributors = contributors.OrderByDescending(x => x.Count).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var output = new StringBuilder();
        output.AppendLine($"Git 贡献者: {contributors.Count:N0} 人");
        foreach (var contributor in contributors.Take(maxResults))
        {
            var suffix = includeEmail && contributor.Email.Length > 0 ? $" <{contributor.Email}>" : "";
            output.AppendLine($"{contributor.Count:N0}  {contributor.Name}{suffix}");
        }
        if (contributors.Count > maxResults)
            output.AppendLine($"…（另有 {contributors.Count - maxResults} 人未显示）");
        return output.ToString().TrimEnd();
    }
}
