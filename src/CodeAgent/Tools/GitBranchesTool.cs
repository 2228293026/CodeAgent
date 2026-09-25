using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>以只读方式列出 Git 本地/远程分支及其最新提交摘要。</summary>
public sealed class GitBranchesTool : ITool
{
    public string Name => "git_branches";
    public string Description =>
        "列出 Git 分支（短哈希、提交日期、主题），支持本地/远程分支、名称模式、排序、数量限制和超时。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["include_local"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含本地分支（默认 true）" },
            ["include_remote"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含远程分支（默认 false）" },
            ["pattern"] = new JsonObject { ["type"] = "string", ["description"] = "分支名称模式，如 feature/*" },
            ["max_branches"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示分支数（默认 100，最大 1000）" },
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
        var includeLocal = ToolArgs.GetBool(args, "include_local", true);
        var includeRemote = ToolArgs.GetBool(args, "include_remote", false);
        if (!includeLocal && !includeRemote)
            throw new ToolException("include_local 和 include_remote 不能同时为 false");
        var maxBranches = Math.Clamp(ToolArgs.GetInt(args, "max_branches", 100), 1, 1_000);
        var maxOutput = Math.Clamp(ToolArgs.GetInt(args, "max_output_chars", 30_000), 1_000, 200_000);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 10), 1, 60);
        var pattern = ToolArgs.GetString(args, "pattern");
        if (!string.IsNullOrWhiteSpace(pattern) && pattern.StartsWith('-'))
            throw new ToolException("pattern 不能以 '-' 开头");

        var refs = new List<string>();
        if (includeLocal)
            refs.Add("refs/heads");
        if (includeRemote)
            refs.Add("refs/remotes");
        var command = new List<string>
        {
            "for-each-ref",
            "--sort=-committerdate",
            "--format=%(refname:short)%09%(objectname:short)%09%(committerdate:short)%09%(subject)",
        };
        if (!string.IsNullOrWhiteSpace(pattern))
        {
            foreach (var reference in refs)
                command.Add(reference + "/" + pattern);
        }
        else
            command.AddRange(refs);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var result = await GitStatusTool.RunGitAsync(ctx.Workspace.Root, command, timeout.Token);
        if (result.ExitCode != 0)
            throw new ToolException($"git for-each-ref 失败（退出码 {result.ExitCode}）: {result.Error}");
        if (string.IsNullOrWhiteSpace(result.Output))
            return "没有匹配的 Git 分支。";

        var lines = result.Output.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var shown = Math.Min(lines.Length, maxBranches);
        var output = new StringBuilder();
        output.AppendLine($"Git 分支: {lines.Length} 个");
        for (var i = 0; i < shown; i++)
        {
            var fields = lines[i].Split('\t', 4);
            if (fields.Length >= 4)
                output.AppendLine($"{fields[0]}  {fields[1]}  {fields[2]}  {fields[3]}");
            else
                output.AppendLine(fields[0]);
        }
        if (shown < lines.Length)
            output.AppendLine($"…（另有 {lines.Length - shown} 个分支未显示）");
        return Limit(output.ToString().TrimEnd(), maxOutput);
    }

    private static string Limit(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        const string marker = "\n…（Git 分支输出已截断）";
        return text[..Math.Max(0, maxChars - marker.Length)] + marker;
    }
}
