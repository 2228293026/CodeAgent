using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>为单个项目文件创建安全备份，支持自定义后缀、覆盖和 dry-run。</summary>
public sealed class BackupFileTool : ITool
{
    public string Name => "backup_file";
    public string Description =>
        "在工作区内为文件创建备份，默认生成 .bak 后缀；支持自定义目标和覆盖、dry-run、missing_ok，并拒绝符号链接和目录。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "要备份的源文件路径" },
            ["destination"] = new JsonObject { ["type"] = "string", ["description"] = "备份目标路径；省略时在源文件名后追加 suffix" },
            ["suffix"] = new JsonObject { ["type"] = "string", ["description"] = "默认备份后缀（默认 .bak，不能包含路径分隔符）" },
            ["overwrite"] = new JsonObject { ["type"] = "boolean", ["description"] = "目标已存在时覆盖（默认 false）" },
            ["dry_run"] = new JsonObject { ["type"] = "boolean", ["description"] = "只预览，不写盘（默认 false）" },
            ["missing_ok"] = new JsonObject { ["type"] = "boolean", ["description"] = "源文件不存在时跳过（默认 false）" },
        },
        ["required"] = new JsonArray("path"),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = ToolArgs.GetString(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            throw new ToolException("缺少必填参数 path");
        var source = ctx.Workspace.ResolveRead(path);
        if (Directory.Exists(source))
            throw new ToolException($"源路径是目录，不能使用 backup_file: {path}");
        if (!File.Exists(source))
        {
            if (ToolArgs.GetBool(args, "missing_ok", false))
                return $"源文件不存在，已跳过: {path}";
            throw new ToolException($"源文件不存在: {path}");
        }
        if (new FileInfo(source).LinkTarget is not null)
            throw new ToolException($"源文件不能是符号链接: {path}");
        var suffix = ToolArgs.GetString(args, "suffix");
        if (string.IsNullOrEmpty(suffix))
            suffix = ".bak";
        if (suffix.IndexOfAny(new[] { '/', '\\' }) >= 0 || suffix.Contains('\0'))
            throw new ToolException("suffix 不能包含路径分隔符或 NUL 字符");
        var destinationArg = ToolArgs.GetString(args, "destination");
        var destination = string.IsNullOrWhiteSpace(destinationArg)
            ? ctx.Workspace.Resolve(path + suffix)
            : ctx.Workspace.Resolve(destinationArg);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new ToolException("备份目标不能与源文件相同");
        if (Directory.Exists(destination))
            throw new ToolException($"备份目标是目录: {destinationArg ?? path + suffix}");
        var overwrite = ToolArgs.GetBool(args, "overwrite", false);
        if (File.Exists(destination) && !overwrite)
            throw new ToolException($"备份目标已存在: {destinationArg ?? path + suffix}（可设置 overwrite=true）");
        if (ToolArgs.GetBool(args, "dry_run", false))
            return $"[dry_run] 将备份 {path} → {destinationArg ?? path + suffix}。未写盘。";
        var destinationParent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(destinationParent) && !Directory.Exists(destinationParent))
            Directory.CreateDirectory(destinationParent);
        var timestamp = File.GetLastWriteTimeUtc(source);
        var attributes = File.GetAttributes(source);
        await Task.Run(() => File.Copy(source, destination, overwrite), ct);
        File.SetLastWriteTimeUtc(destination, timestamp);
        try { File.SetAttributes(destination, attributes); } catch { /* 某些平台不允许全部属性 */ }
        return $"已创建备份: {path} → {ctx.Workspace.ToRelative(destination)}";
    }
}
