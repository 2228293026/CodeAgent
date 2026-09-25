using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>安全删除工作区内的空目录，不递归删除内容。</summary>
public sealed class RemoveEmptyDirectoryTool : ITool
{
    public string Name => "remove_empty_directory";
    public string Description =>
        "删除工作区内为空的单个目录；目录非空、目标为文件或符号链接时拒绝，支持 dry_run 和 missing_ok。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "要删除的空目录路径，相对工作区根目录" },
            ["dry_run"] = new JsonObject { ["type"] = "boolean", ["description"] = "只预览删除，不改磁盘（默认 false）" },
            ["missing_ok"] = new JsonObject { ["type"] = "boolean", ["description"] = "目录不存在时跳过（默认 false）" },
        },
        ["required"] = new JsonArray("path"),
    };

    public Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = ToolArgs.GetString(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            throw new ToolException("缺少必填参数 path");
        var full = ctx.Workspace.Resolve(path);
        if (File.Exists(full))
            throw new ToolException($"目标是文件，不能使用 remove_empty_directory: {path}");
        if (!Directory.Exists(full))
        {
            if (ToolArgs.GetBool(args, "missing_ok", false))
                return Task.FromResult($"目录不存在，已跳过: {path}");
            throw new ToolException($"目录不存在: {path}");
        }
        if (new DirectoryInfo(full).LinkTarget is not null)
            throw new ToolException($"目标不能是符号链接目录: {path}");
        var entries = Directory.EnumerateFileSystemEntries(full).ToList();
        if (entries.Count > 0)
            throw new ToolException($"目录非空，已拒绝删除: {path}（包含 {entries.Count} 个条目）");
        if (ToolArgs.GetBool(args, "dry_run", false))
            return Task.FromResult($"[dry_run] 将删除空目录: {path}。未写盘。");
        Directory.Delete(full);
        return Task.FromResult($"已删除空目录: {path}");
    }
}
