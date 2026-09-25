using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读汇总 Git 配置来源，并对 URL、令牌和密码类值脱敏。</summary>
public sealed class GitConfigReportTool : ITool
{
    public string Name => "git_config_report";
    public string Description =>
        "只读汇总 Git 配置键、来源和 section 分布，自动脱敏 URL、token、password 等敏感值。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示配置项数（默认 200，最大 5000）" },
            ["include_global"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含全局/系统配置（默认 true）" },
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
        var includeGlobal = ToolArgs.GetBool(args, "include_global", true);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 15), 1, 120);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var command = new List<string> { "config", "--list", "--show-origin" };
        if (!includeGlobal)
            command.Add("--local");
        var result = await GitStatusTool.RunGitAsync(start, command, timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException($"无法读取 Git 配置（退出码 {result.ExitCode}）");
        var entries = new List<string>();
        var sections = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in result.Output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            var origin = tab >= 0 ? line[..tab] : "(unknown)";
            var setting = tab >= 0 ? line[(tab + 1)..] : line;
            var equals = setting.IndexOf('=');
            var key = equals >= 0 ? setting[..equals] : setting;
            var value = equals >= 0 ? setting[(equals + 1)..] : "(boolean)";
            var section = key.Split('.', 2)[0];
            sections[section] = sections.GetValueOrDefault(section) + 1;
            entries.Add($"{origin} | {key} = {Redact(key, value)}");
        }
        var output = new StringBuilder();
        output.AppendLine($"Git 配置报告: {entries.Count:N0} 项");
        foreach (var pair in sections.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            output.AppendLine($"  {pair.Key}: {pair.Value:N0}");
        output.AppendLine("配置项:");
        foreach (var entry in entries.Take(maxResults))
            output.AppendLine($"  {entry}");
        if (entries.Count > maxResults)
            output.AppendLine($"…（另有 {entries.Count - maxResults} 项未显示）");
        return output.ToString().TrimEnd();
    }

    private static string Redact(string key, string value)
    {
        var lower = key.ToLowerInvariant();
        return lower.Contains("url", StringComparison.Ordinal)
            || lower.Contains("token", StringComparison.Ordinal)
            || lower.Contains("password", StringComparison.Ordinal)
            || lower.Contains("secret", StringComparison.Ordinal)
            || lower.Contains("credential", StringComparison.Ordinal)
            || lower.Contains("auth", StringComparison.Ordinal)
            ? "<redacted>"
            : value;
    }
}
