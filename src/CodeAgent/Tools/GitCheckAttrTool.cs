using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读查询 Git 属性（例如 text、eol、filter）及其来源。</summary>
public sealed class GitCheckAttrTool : ITool
{
    public string Name => "git_check_attr";
    public string Description =>
        "查询一个或多个路径的 Git 属性设置和匹配来源，例如 text、eol、filter、diff、merge；不修改仓库。";

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
                ["description"] = "要查询的路径数组",
            },
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "单个路径（paths 的替代写法）" },
            ["attributes"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "要查询的属性名；省略时查询全部属性",
            },
            ["max_paths"] = new JsonObject { ["type"] = "integer", ["description"] = "最多查询路径数（默认 100，最大 1000）" },
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
        var root = ctx.Workspace.ResolveRead(null);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 10), 1, 60);
        var attributes = ToolArgs.GetStringList(args, "attributes")?.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var output = new StringBuilder();
        var processed = 0;
        foreach (var requestedPath in requested.Take(maxPaths))
        {
            ct.ThrowIfCancellationRequested();
            processed++;
            var full = ctx.Workspace.ResolveRead(requestedPath);
            var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
            var command = new List<string> { "check-attr" };
            if (attributes is null || attributes.Count == 0)
                command.Add("--all");
            else
                command.AddRange(attributes);
            command.Add("--");
            command.Add(relative);
            var result = await GitStatusTool.RunGitAsync(root, command, timeout.Token);
            if (result.ExitCode != 0)
                throw new ToolException($"git check-attr 失败（退出码 {result.ExitCode}）: {result.Error}");
            output.Append(result.Output.TrimEnd());
            output.AppendLine();
        }
        var header = $"Git 属性检查: {processed} 个路径";
        if (requested.Count > maxPaths)
            header += $"，达到 max_paths={maxPaths} 上限";
        return header + "\n" + output.ToString().TrimEnd();
    }
}
