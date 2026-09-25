using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>以只读方式查看 Git 标签、对象类型和标签主题。</summary>
public sealed class GitTagsTool : ITool
{
    public string Name => "git_tags";
    public string Description =>
        "列出 Git 标签及对象短哈希、类型、创建日期和主题，支持标签模式、数量限制、超时和取消。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["pattern"] = new JsonObject { ["type"] = "string", ["description"] = "标签名称模式，如 v*" },
            ["max_tags"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示标签数（默认 100，最大 1000）" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Git 命令超时秒数（默认 10，最大 60）" },
            ["max_output_chars"] = new JsonObject { ["type"] = "integer", ["description"] = "最大输出字符数（默认 30000，最大 200000）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxTags = Math.Clamp(ToolArgs.GetInt(args, "max_tags", 100), 1, 1_000);
        var maxOutput = Math.Clamp(ToolArgs.GetInt(args, "max_output_chars", 30_000), 1_000, 200_000);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 10), 1, 60);
        var pattern = ToolArgs.GetString(args, "pattern");
        if (!string.IsNullOrWhiteSpace(pattern) && pattern.StartsWith('-'))
            throw new ToolException("pattern 不能以 '-' 开头");

        var command = new List<string>
        {
            "for-each-ref",
            "--sort=-creatordate",
            "--format=%(refname:short)%09%(objectname:short)%09%(objecttype)%09%(creatordate:short)%09%(subject)",
        };
        command.Add(string.IsNullOrWhiteSpace(pattern) ? "refs/tags" : "refs/tags/" + pattern);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var result = await GitStatusTool.RunGitAsync(ctx.Workspace.Root, command, timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException($"git for-each-ref 失败（退出码 {result.ExitCode}）: {result.Error}");
        if (string.IsNullOrWhiteSpace(result.Output))
            return "没有匹配的 Git 标签。";
        var lines = result.Output.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var shown = Math.Min(lines.Length, maxTags);
        var output = new StringBuilder();
        output.AppendLine($"Git 标签: {lines.Length} 个");
        for (var i = 0; i < shown; i++)
        {
            var fields = lines[i].Split('\t', 5);
            output.AppendLine(fields.Length >= 5
                ? $"{fields[0]}  {fields[1]}  {fields[2]}  {fields[3]}  {fields[4]}"
                : lines[i]);
        }
        if (shown < lines.Length)
            output.AppendLine($"…（另有 {lines.Length - shown} 个标签未显示）");
        return Limit(output.ToString().TrimEnd(), maxOutput);
    }

    private static string Limit(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        const string marker = "\n…（Git 标签输出已截断）";
        return text[..Math.Max(0, maxChars - marker.Length)] + marker;
    }
}
