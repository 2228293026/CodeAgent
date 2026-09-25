using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>从备份文件安全恢复目标文件，恢复前可自动保存目标当前版本。</summary>
public sealed class RestoreBackupTool : ITool
{
    public string Name => "restore_backup";
    public string Description =>
        "从指定备份文件恢复目标文件，支持覆盖前自动创建 .pre-restore 备份、dry-run、覆盖策略和原子替换。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "备份源文件路径，例如 a.txt.bak" },
            ["destination"] = new JsonObject { ["type"] = "string", ["description"] = "要恢复的目标文件路径" },
            ["overwrite"] = new JsonObject { ["type"] = "boolean", ["description"] = "目标已存在时允许覆盖（默认 false）" },
            ["backup_original"] = new JsonObject { ["type"] = "boolean", ["description"] = "覆盖前先保存目标为 .pre-restore（默认 true）" },
            ["overwrite_backup"] = new JsonObject { ["type"] = "boolean", ["description"] = "允许覆盖已有 .pre-restore 备份（默认 false）" },
            ["dry_run"] = new JsonObject { ["type"] = "boolean", ["description"] = "只预览，不写盘（默认 false）" },
        },
        ["required"] = new JsonArray("path", "destination"),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = ToolArgs.GetString(args, "path");
        var destinationArg = ToolArgs.GetString(args, "destination");
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(destinationArg))
            throw new ToolException("必须提供 path 和 destination");
        var source = ctx.Workspace.ResolveRead(path);
        var destination = ctx.Workspace.Resolve(destinationArg);
        if (Directory.Exists(source) || !File.Exists(source))
            throw new ToolException($"备份源文件不存在: {path}");
        if (new FileInfo(source).LinkTarget is not null)
            throw new ToolException($"备份源不能是符号链接: {path}");
        if (Directory.Exists(destination))
            throw new ToolException($"恢复目标是目录: {destinationArg}");
        if (new FileInfo(destination).LinkTarget is not null)
            throw new ToolException($"恢复目标不能是符号链接: {destinationArg}");
        var destinationExists = File.Exists(destination);
        var overwrite = ToolArgs.GetBool(args, "overwrite", false);
        if (destinationExists && !overwrite)
            throw new ToolException($"恢复目标已存在: {destinationArg}（可设置 overwrite=true）");
        var backupOriginal = ToolArgs.GetBool(args, "backup_original", true);
        var overwriteBackup = ToolArgs.GetBool(args, "overwrite_backup", false);
        var originalBackup = destination + ".pre-restore";
        if (destinationExists && backupOriginal && File.Exists(originalBackup) && !overwriteBackup)
            throw new ToolException($"恢复前备份已存在: {ctx.Workspace.ToRelative(originalBackup)}（可设置 overwrite_backup=true）");
        if (ToolArgs.GetBool(args, "dry_run", false))
            return $"[dry_run] 将从 {path} 恢复 {destinationArg}" +
                   (destinationExists ? (backupOriginal ? "，并先保存 .pre-restore" : "，覆盖现有目标") : "") + "。未写盘。";
        if (destinationExists && backupOriginal)
            await Task.Run(() => File.Copy(destination, originalBackup, overwriteBackup), ct);
        var temp = SkipDirs.TempPathFor(destination);
        try
        {
            await Task.Run(() =>
            {
                File.Copy(source, temp, overwrite: true);
                File.Move(temp, destination, overwrite: true);
            }, ct);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
        return $"已恢复文件: {path} → {destinationArg}" +
               (destinationExists && backupOriginal ? $"（原文件已备份为 {ctx.Workspace.ToRelative(originalBackup)}）" : "");
    }
}
