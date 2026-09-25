using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读检查 Git 对象库和引用完整性。</summary>
public sealed class GitFsckTool : ITool
{
    public string Name => "git_fsck";
    public string Description =>
        "运行只读 Git fsck 检查对象库、提交和引用完整性，支持 full/dangling 模式和超时/输出限制。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["full"] = new JsonObject { ["type"] = "boolean", ["description"] = "执行完整检查（默认 false）" },
            ["include_dangling"] = new JsonObject { ["type"] = "boolean", ["description"] = "输出悬空对象信息（默认 true）" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Git 命令超时秒数（默认 30，最大 300）" },
            ["max_output_chars"] = new JsonObject { ["type"] = "integer", ["description"] = "最大输出字符数（默认 20000，最大 100000）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var start = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(start))
            throw new ToolException($"目录不存在: {requestedPath}");
        var full = ToolArgs.GetBool(args, "full", false);
        var includeDangling = ToolArgs.GetBool(args, "include_dangling", true);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 30), 1, 300);
        var maxOutput = Math.Clamp(ToolArgs.GetInt(args, "max_output_chars", 20_000), 1_000, 100_000);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var command = new List<string> { "fsck", "--no-progress" };
        if (full)
            command.Add("--full");
        if (!includeDangling)
            command.Add("--no-dangling");
        var result = await GitStatusTool.RunGitAsync(start, command, timeout.Token);
        var output = new StringBuilder();
        output.AppendLine($"Git fsck 检查（{(full ? "完整" : "快速")}）: {(result.ExitCode == 0 ? "通过" : $"失败（退出码 {result.ExitCode}）")}");
        var details = result.Output.TrimEnd();
        if (details.Length == 0)
            details = result.Error.TrimEnd();
        if (details.Length > 0)
        {
            output.AppendLine("诊断:");
            output.Append(details);
        }
        return Limit(output.ToString().TrimEnd(), maxOutput);
    }

    private static string Limit(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        const string marker = "\n…（Git fsck 输出已截断）";
        return text[..Math.Max(0, maxChars - marker.Length)] + marker;
    }
}
