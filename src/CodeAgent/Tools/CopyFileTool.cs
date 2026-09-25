using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>复制文本文件到工作区内的另一条路径，支持覆盖、预览、编码保留和撤销。</summary>
public sealed class CopyFileTool : ITool
{
    static CopyFileTool() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    private const long MaxFileBytes = 20 * 1024 * 1024;

    public string Name => "copy_file";
    public string Description =>
        "复制工作区内的文本文件到另一条路径。支持 dry_run 预览、覆盖保护、保留源文件编码/时间戳，并将覆盖或新建操作加入撤销栈。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["source"] = new JsonObject { ["type"] = "string", ["description"] = "源文件路径，相对工作区根目录" },
            ["destination"] = new JsonObject { ["type"] = "string", ["description"] = "目标文件路径，相对工作区根目录" },
            ["overwrite"] = new JsonObject { ["type"] = "boolean", ["description"] = "目标已存在时允许覆盖（默认 false）" },
            ["create_dirs"] = new JsonObject { ["type"] = "boolean", ["description"] = "自动创建目标父目录（默认 true）" },
            ["preserve_timestamp"] = new JsonObject { ["type"] = "boolean", ["description"] = "复制后保留源文件修改时间（默认 false）" },
            ["dry_run"] = new JsonObject { ["type"] = "boolean", ["description"] = "只预览复制结果，不写盘（默认 false）" },
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
            throw new ToolException($"source 是目录: {sourcePath}（copy_file 只能复制文件）");
        if (!File.Exists(source))
            throw new ToolException($"源文件不存在: {sourcePath}");
        if (Directory.Exists(destination))
            throw new ToolException($"destination 是目录: {destinationPath}");
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            return $"源和目标是同一个文件，未执行复制: {sourcePath}";
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
                Directory.CreateDirectory(parent);
            else if (!Directory.Exists(parent))
                throw new ToolException($"目标父目录不存在: {parent}（如需自动创建请设置 create_dirs=true）");
        }

        var length = new FileInfo(source).Length;
        if (length > MaxFileBytes)
            throw new ToolException($"源文件过大（{length / 1024 / 1024} MB），copy_file 当前最多支持 {MaxFileBytes / 1024 / 1024} MB");
        var text = await TextUtil.ReadTextSmartAsync(source, ct);
        if (SkipDirs.LooksBinary(text))
            throw new ToolException($"源文件疑似二进制，copy_file 当前只支持文本文件: {sourcePath}");

        var preserveTimestamp = ToolArgs.GetBool(args, "preserve_timestamp", false);
        if (ToolArgs.GetBool(args, "dry_run", false))
        {
            return $"[dry_run] 将复制 {sourcePath} → {destinationPath}（{length:N0} 字节）" +
                   (destinationExists ? "，覆盖目标" : "，创建新文件") + "。未写盘。";
        }

        string? oldText = null;
        string? destinationEncoding = null;
        if (destinationExists)
        {
            destinationEncoding = TextUtil.DetectFileEncoding(destination);
            if (new FileInfo(destination).Length <= 4 * 1024 * 1024)
                oldText = await TextUtil.ReadTextSmartAsync(destination, ct);
        }
        var sourceEncoding = TextUtil.DetectFileEncoding(source);
        var encoding = EncodingFor(sourceEncoding);
        var tmp = SkipDirs.TempPathFor(destination);
        try
        {
            await File.WriteAllTextAsync(tmp, text, encoding, ct);
            File.Move(tmp, destination, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }

        if (preserveTimestamp)
            File.SetLastWriteTime(destination, File.GetLastWriteTime(source));

        ctx.Undo.Push(new UndoEntry
        {
            Kind = "write",
            Path = destination,
            HadFile = destinationExists,
            OldText = oldText,
            EncodingName = destinationEncoding,
        });
        return $"已复制 {sourcePath} → {destinationPath}（{length:N0} 字节）" +
               (destinationExists ? "，已覆盖目标" : "，已创建新文件") + "。";
    }

    private static Encoding EncodingFor(string? name) => name switch
    {
        "utf8-bom" => new UTF8Encoding(true),
        "gb18030" => Encoding.GetEncoding("GB18030"),
        _ => new UTF8Encoding(false),
    };
}
