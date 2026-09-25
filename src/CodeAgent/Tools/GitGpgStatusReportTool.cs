using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读查看 Git 提交/标签签名配置和最近提交的签名状态。</summary>
public sealed class GitGpgStatusReportTool : ITool
{
    public string Name => "git_gpg_status_report";
    public string Description =>
        "只读汇总 commit/tag 签名配置、gpg.* 设置和最近提交签名状态，签名密钥值自动脱敏。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["include_history"] = new JsonObject { ["type"] = "boolean", ["description"] = "检查最近提交的签名状态（默认 true）" },
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
        var includeHistory = ToolArgs.GetBool(args, "include_history", true);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 20), 1, 120);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var probe = await GitStatusTool.RunGitAsync(start, ["rev-parse", "--git-dir"], timeout.Token);
        if (probe.ExitCode != 0)
            throw new ToolException("当前目录不是 Git 仓库");
        var config = await GitStatusTool.RunGitAsync(start, ["config", "--get-regexp", "^(commit|tag|user|gpg)\\."], timeout.Token);
        var output = new StringBuilder();
        output.AppendLine("Git 签名状态报告");
        output.AppendLine("签名配置:");
        if (config.ExitCode == 0 && config.Output.Trim().Length > 0)
        {
            foreach (var line in config.Output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var space = line.IndexOf(' ');
                if (space < 0)
                {
                    output.AppendLine($"  {line.Trim()}");
                    continue;
                }
                var key = line[..space].Trim();
                var value = line[(space + 1)..].Trim();
                output.AppendLine($"  {key} = {Redact(key, value)}");
            }
        }
        else
        {
            output.AppendLine("  (无匹配配置)");
        }
        if (includeHistory)
        {
            var history = await GitStatusTool.RunGitAsync(start, ["log", "-1", "--show-signature", "--format=%H%n%an%n%s"], timeout.Token);
            output.AppendLine("最近提交:");
            if (history.ExitCode == 0)
            {
                foreach (var line in history.Output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    output.AppendLine($"  {line.Trim()}");
            }
            else
            {
                output.AppendLine("  (仓库没有提交)");
            }
        }
        return output.ToString().TrimEnd();
    }

    private static string Redact(string key, string value)
    {
        if (key.Contains("signingkey", StringComparison.OrdinalIgnoreCase))
            return value.Length <= 4 ? "<redacted>" : value[..2] + new string('*', Math.Min(8, value.Length - 4)) + value[^2..];
        return value;
    }
}
