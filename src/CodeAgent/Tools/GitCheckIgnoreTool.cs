using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读检查路径是否被 Git ignore 规则匹配，并显示匹配来源。</summary>
public sealed class GitCheckIgnoreTool : ITool
{
    public string Name => "git_check_ignore";
    public string Description =>
        "检查一个或多个工作区路径是否被 Git ignore 规则忽略，显示规则来源和匹配模式；不修改仓库。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["paths"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["minItems"] = 1,
                ["description"] = "要检查的路径数组",
            },
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "单个路径（paths 的替代写法）" },
            ["include_unignored"] = new JsonObject { ["type"] = "boolean", ["description"] = "同时显示未被忽略的路径（默认 false）" },
            ["max_paths"] = new JsonObject { ["type"] = "integer", ["description"] = "最多检查路径数（默认 100，最大 1000）" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Git 命令超时秒数（默认 10，最大 60）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requested = new List<string>();
        if (args?["paths"] is JsonArray array)
        {
            foreach (var item in array)
                if (item is JsonValue value && value.TryGetValue<string>(out var path) && !string.IsNullOrWhiteSpace(path))
                    requested.Add(path);
        }
        var single = ToolArgs.GetString(args, "path");
        if (!string.IsNullOrWhiteSpace(single))
            requested.Add(single);
        if (requested.Count == 0)
            throw new ToolException("必须提供 path 或 paths");
        var maxPaths = Math.Clamp(ToolArgs.GetInt(args, "max_paths", 100), 1, 1_000);
        var includeUnignored = ToolArgs.GetBool(args, "include_unignored", false);
        var root = ctx.Workspace.ResolveRead(null);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 10), 1, 60);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var output = new StringBuilder();
        var ignored = 0;
        var processed = 0;
        foreach (var requestedPath in requested.Take(maxPaths))
        {
            ct.ThrowIfCancellationRequested();
            processed++;
            var full = ctx.Workspace.ResolveRead(requestedPath);
            var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
            var result = await GitStatusTool.RunGitAsync(root, ["check-ignore", "-v", "--no-index", "--", relative], timeout.Token);
            if (result.ExitCode == 0)
            {
                ignored++;
                output.AppendLine($"已忽略: {relative}");
                var rule = result.Output.TrimEnd();
                if (rule.Length > 0)
                    output.AppendLine($"  规则: {rule}");
            }
            else if (result.ExitCode == 1)
            {
                if (includeUnignored)
                    output.AppendLine($"未忽略: {relative}");
            }
            else
            {
                throw new ToolException($"git check-ignore 失败（退出码 {result.ExitCode}）: {result.Error}");
            }
        }
        var header = $"Git ignore 检查: {ignored} 个已忽略，{processed} 个已处理";
        if (requested.Count > maxPaths)
            header += $"，达到 max_paths={maxPaths} 上限";
        return header + (output.Length == 0 ? "" : "\n" + output.ToString().TrimEnd());
    }
}
