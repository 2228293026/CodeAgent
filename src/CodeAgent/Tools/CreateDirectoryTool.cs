using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>安全创建工作区目录，支持递归创建、已存在处理和 dry-run。</summary>
public sealed class CreateDirectoryTool : ITool
{
    public string Name => "create_directory";
    public string Description =>
        "创建工作区内的单个目录，支持自动创建父目录、exist_ok、dry_run，并拒绝文件路径和符号链接。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "要创建的目录路径，相对工作区根目录" },
            ["parents"] = new JsonObject { ["type"] = "boolean", ["description"] = "自动创建缺失的父目录（默认 true）" },
            ["exist_ok"] = new JsonObject { ["type"] = "boolean", ["description"] = "目录已存在时视为成功（默认 true）" },
            ["dry_run"] = new JsonObject { ["type"] = "boolean", ["description"] = "只预览，不写盘（默认 false）" },
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
        var parents = ToolArgs.GetBool(args, "parents", true);
        var existOk = ToolArgs.GetBool(args, "exist_ok", true);
        var dryRun = ToolArgs.GetBool(args, "dry_run", false);
        if (File.Exists(full))
            throw new ToolException($"目标已存在且为文件: {path}");
        if (Directory.Exists(full))
        {
            if (new DirectoryInfo(full).LinkTarget is not null)
                throw new ToolException($"目标不能是符号链接目录: {path}");
            if (!existOk)
                throw new ToolException($"目录已存在: {path}");
            return Task.FromResult($"目录已存在: {path}");
        }
        var parent = Path.GetDirectoryName(full);
        if (!parents && !string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            throw new ToolException($"父目录不存在: {Path.GetRelativePath(ctx.Workspace.Root, parent)}（请设置 parents=true）");
        if (dryRun)
            return Task.FromResult($"[dry_run] 将创建目录: {path}。未写盘。");
        Directory.CreateDirectory(full);
        return Task.FromResult($"已创建目录: {path}" + (parents ? "（含父目录）" : ""));
    }
}
