using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>删除工作区内的单个文件，支持预览、可选备份和可撤销恢复。</summary>
public sealed class DeleteFileTool : ITool
{
    private const long MaxUndoBytes = 4 * 1024 * 1024;

    static DeleteFileTool() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public string Name => "delete_file";
    public string Description =>
        "删除工作区内的单个文件。默认拒绝删除目录和符号链接；支持 dry_run、missing_ok、可选 .bak 备份，并在可记录时加入撤销栈。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "要删除的文件路径，相对工作区根目录" },
            ["dry_run"] = new JsonObject { ["type"] = "boolean", ["description"] = "只预览删除，不改磁盘（默认 false）" },
            ["missing_ok"] = new JsonObject { ["type"] = "boolean", ["description"] = "文件不存在时不报错（默认 false）" },
            ["backup"] = new JsonObject { ["type"] = "boolean", ["description"] = "删除前在同目录创建 .bak 备份（默认 false）" },
        },
        ["required"] = new JsonArray("path"),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = ToolArgs.GetString(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            throw new ToolException("缺少必填参数 path");
        var full = ctx.Workspace.Resolve(path);
        if (Directory.Exists(full))
            throw new ToolException($"'{path}' 是目录，delete_file 只能删除单个文件");
        if (!File.Exists(full))
        {
            if (ToolArgs.GetBool(args, "missing_ok", false))
                return $"文件不存在，已跳过: {path}";
            throw new ToolException($"文件不存在: {path}");
        }
        var info = new FileInfo(full);
        if (info.LinkTarget is not null)
            throw new ToolException($"目标不能是符号链接: {path}");

        var dryRun = ToolArgs.GetBool(args, "dry_run", false);
        if (dryRun)
            return $"[dry_run] 将删除文件: {path}（{info.Length:N0} 字节）。未写盘。";

        var encoding = TextUtil.DetectFileEncoding(full);
        string? oldText = null;
        if (info.Length <= MaxUndoBytes)
        {
            try
            {
                var candidate = await TextUtil.ReadTextSmartAsync(full, ct);
                if (!SkipDirs.LooksBinary(candidate))
                    oldText = candidate;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var backup = ToolArgs.GetBool(args, "backup", false);
        string? backupPath = null;
        if (backup)
        {
            backupPath = full + ".bak";
            if (new FileInfo(backupPath).LinkTarget is not null)
                throw new ToolException($"备份目标不能是符号链接: {backupPath}");
            File.Copy(full, backupPath, overwrite: true);
        }

        File.Delete(full);
        if (oldText is not null)
        {
            ctx.Undo.Push(new UndoEntry
            {
                Kind = "write",
                Path = full,
                HadFile = true,
                OldText = oldText,
                EncodingName = encoding,
            });
        }

        return $"已删除文件: {path}（{info.Length:N0} 字节）" +
               (backupPath is not null ? $"，备份: {backupPath}" : "") +
               (oldText is null ? "。内容过大或为二进制，未加入撤销栈。" : "，可撤销。");
    }
}
