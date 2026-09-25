using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读列出当前 Git 仓库的自定义 hooks 和示例 hooks，不执行 hook。</summary>
public sealed class GitHookInventoryTool : ITool
{
    public string Name => "git_hook_inventory";
    public string Description =>
        "只读查看当前 Git 仓库的 hooks 目录，列出活动 hook、示例 hook、大小和首行，不执行或修改 hook。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["include_samples"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含 .sample 示例 hook（默认 true）" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示 hook 数（默认 100，最大 1000）" },
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
        var includeSamples = ToolArgs.GetBool(args, "include_samples", true);
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 100), 1, 1_000);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 15), 1, 120);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var pathResult = await GitStatusTool.RunGitAsync(start, ["rev-parse", "--git-path", "hooks"], timeout.Token);
        if (pathResult.ExitCode != 0)
            throw new ToolException("当前目录不是 Git 仓库");
        var hooksPath = pathResult.Output.Trim();
        if (string.IsNullOrWhiteSpace(hooksPath))
            throw new ToolException("无法解析 Git hooks 目录");
        hooksPath = Path.GetFullPath(hooksPath, start);
        if (!Directory.Exists(hooksPath))
            return "Git hooks 清单: 0 个（hooks 目录不存在）";
        var hooks = Directory.EnumerateFiles(hooksPath).Where(file =>
        {
            var name = Path.GetFileName(file);
            return includeSamples || !name.EndsWith(".sample", StringComparison.OrdinalIgnoreCase);
        }).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
        var output = new StringBuilder();
        output.AppendLine($"Git hooks 清单: {hooks.Count:N0} 个");
        foreach (var hook in hooks.Take(maxResults))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(hook);
            var sample = name.EndsWith(".sample", StringComparison.OrdinalIgnoreCase);
            string firstLine;
            try { firstLine = File.ReadLines(hook).FirstOrDefault() ?? "(空文件)"; }
            catch (IOException) { firstLine = "(无法读取)"; }
            catch (UnauthorizedAccessException) { firstLine = "(无权限读取)"; }
            output.AppendLine($"  {name} [{(sample ? "示例" : "活动")}]，{new FileInfo(hook).Length:N0} 字节，首行: {firstLine.Trim()}");
        }
        if (hooks.Count > maxResults)
            output.AppendLine($"…（另有 {hooks.Count - maxResults} 个 hook 未显示）");
        return output.ToString().TrimEnd();
    }
}
