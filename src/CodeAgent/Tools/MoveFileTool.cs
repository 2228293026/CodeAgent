using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>在工作区内安全移动/重命名文件，支持覆盖保护、预览和可撤销记录。</summary>
public sealed class MoveFileTool : ITool
{
    private const long MaxUndoBytes = 4 * 1024 * 1024;

    static MoveFileTool() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public string Name => "move_file";
    public string Description =>
        "移动或重命名工作区内的文件。目标已存在时默认拒绝覆盖；支持 dry_run、保留时间戳，并在可记录时加入撤销栈。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["source"] = new JsonObject { ["type"] = "string", ["description"] = "源文件路径，相对工作区根目录" },
            ["destination"] = new JsonObject { ["type"] = "string", ["description"] = "目标文件路径，相对工作区根目录" },
            ["overwrite"] = new JsonObject { ["type"] = "boolean", ["description"] = "目标已存在时允许覆盖（默认 false）" },
            ["create_dirs"] = new JsonObject { ["type"] = "boolean", ["description"] = "自动创建目标父目录（默认 true）" },
            ["preserve_timestamp"] = new JsonObject { ["type"] = "boolean", ["description"] = "移动后保留源文件修改时间（默认 true）" },
            ["dry_run"] = new JsonObject { ["type"] = "boolean", ["description"] = "只预览移动，不改磁盘（默认 false）" },
        },
        ["required"] = new JsonArray("source", "destination"),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sourcePath = ToolArgs.GetString(args, "source");
        var destinationPath = ToolArgs.GetString(args, "destination");
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath))
            throw new ToolException("source 和 destination 都是必填文件路径");

        var source = ctx.Workspace.ResolveRead(sourcePath);
        var destination = ctx.Workspace.Resolve(destinationPath);
        if (Directory.Exists(source))
            throw new ToolException($"source 是目录: {sourcePath}（move_file 只能移动文件）");
        if (!File.Exists(source))
            throw new ToolException($"源文件不存在: {sourcePath}");
        if (Directory.Exists(destination))
            throw new ToolException($"destination 是目录: {destinationPath}");
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            return $"源和目标是同一个文件，未执行移动: {sourcePath}";
        if (new FileInfo(source).LinkTarget is not null)
            throw new ToolException($"源文件不能是符号链接: {sourcePath}");
        if (new FileInfo(destination).LinkTarget is not null)
            throw new ToolException($"目标不能是符号链接: {destinationPath}");

        var overwrite = ToolArgs.GetBool(args, "overwrite", false);
        var destinationExists = File.Exists(destination);
        if (destinationExists && !overwrite)
            throw new ToolException($"目标已存在: {destinationPath}（如需覆盖请设置 overwrite=true）");
        var createDirs = ToolArgs.GetBool(args, "create_dirs", true);
        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent))
        {
            if (createDirs)
            {
                if (!ToolArgs.GetBool(args, "dry_run", false))
                    Directory.CreateDirectory(parent);
            }
            else if (!Directory.Exists(parent))
                throw new ToolException($"目标父目录不存在: {parent}（如需自动创建请设置 create_dirs=true）");
        }

        var preserveTimestamp = ToolArgs.GetBool(args, "preserve_timestamp", true);
        var dryRun = ToolArgs.GetBool(args, "dry_run", false);
        if (dryRun)
            return $"[dry_run] 将移动 {sourcePath} → {destinationPath}" +
                   (destinationExists ? "（覆盖目标）" : "") + "。未写盘。";

        var sourceInfo = new FileInfo(source);
        var sourceEncoding = TextUtil.DetectFileEncoding(source);
        string? sourceText = null;
        if (sourceInfo.Length <= MaxUndoBytes)
        {
            try
            {
                var candidate = await TextUtil.ReadTextSmartAsync(source, ct);
                if (!SkipDirs.LooksBinary(candidate))
                    sourceText = candidate;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        string? oldDestinationText = null;
        string? destinationEncoding = null;
        var canUndoDestination = !destinationExists;
        if (destinationExists)
        {
            destinationEncoding = TextUtil.DetectFileEncoding(destination);
            if (new FileInfo(destination).Length <= MaxUndoBytes)
            {
                try
                {
                    var candidate = await TextUtil.ReadTextSmartAsync(destination, ct);
                    if (!SkipDirs.LooksBinary(candidate))
                    {
                        oldDestinationText = candidate;
                        canUndoDestination = true;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        try
        {
            File.Move(source, destination, overwrite);
        }
        catch
        {
            throw;
        }
        if (preserveTimestamp)
        {
            try { File.SetLastWriteTime(destination, sourceInfo.LastWriteTime); } catch { }
        }

        var canUndo = sourceText is not null && canUndoDestination;
        if (canUndo)
        {
            ctx.Undo.Push(new UndoEntry
            {
                Kind = "move",
                Path = source,
                OldText = sourceText,
                HadFile = true,
                EncodingName = sourceEncoding,
                RelatedPath = destination,
                RelatedHadFile = destinationExists,
                RelatedOldText = oldDestinationText,
                RelatedEncodingName = destinationEncoding,
            });
        }

        return $"已移动 {sourcePath} → {destinationPath}（{sourceInfo.Length:N0} 字节）" +
               (canUndo ? "，可撤销。" : "。源文件或目标内容过大/不可读，本次未加入撤销栈。");
    }
}
