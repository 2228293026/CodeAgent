using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>以只读方式查看 Git blame，显示每行代码的提交、作者和日期。</summary>
public sealed class GitBlameTool : ITool
{
    public string Name => "git_blame";
    public string Description =>
        "查看文件每行代码的 Git 提交来源（短哈希、作者、日期和行内容），支持行范围、超时和输出限制。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "要追踪的文件路径，相对工作区根目录" },
            ["start_line"] = new JsonObject { ["type"] = "integer", ["description"] = "起始行号（1 起，默认 1）" },
            ["end_line"] = new JsonObject { ["type"] = "integer", ["description"] = "结束行号；省略则追踪到文件末尾" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Git 命令超时秒数（默认 10，最大 60）" },
            ["max_output_chars"] = new JsonObject { ["type"] = "integer", ["description"] = "最大输出字符数（默认 50000，最大 200000）" },
        },
        ["required"] = new JsonArray("path"),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        if (string.IsNullOrWhiteSpace(requestedPath))
            throw new ToolException("缺少必填参数 path");
        var full = ctx.Workspace.ResolveRead(requestedPath);
        if (!File.Exists(full))
            throw new ToolException($"文件不存在或未被 Git 跟踪: {requestedPath}");
        var start = Math.Max(1, ToolArgs.GetInt(args, "start_line", 1));
        var end = ToolArgs.GetInt(args, "end_line", 0);
        if (end != 0 && end < start)
            throw new ToolException($"end_line ({end}) 不能小于 start_line ({start})");
        var maxOutput = Math.Clamp(ToolArgs.GetInt(args, "max_output_chars", 50_000), 1_000, 200_000);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 10), 1, 60);
        var scope = Path.GetRelativePath(ctx.Workspace.Root, full).Replace('\\', '/');
        var command = new List<string> { "blame", "--date=short" };
        if (end > 0)
            command.Add($"-L{start},{end}");
        else if (start > 1)
            command.Add($"-L{start},+");
        command.Add("--");
        command.Add(scope);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var result = await GitStatusTool.RunGitAsync(ctx.Workspace.Root, command, timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException($"git blame 失败（退出码 {result.ExitCode}）: {result.Error}");
        return Limit($"Git blame: {requestedPath}\n{result.Output.TrimEnd()}", maxOutput);
    }

    private static string Limit(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        const string marker = "\n…（Git blame 输出已截断）";
        return text[..Math.Max(0, maxChars - marker.Length)] + marker;
    }
}
