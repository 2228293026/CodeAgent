using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>查看单个文件的大小、编码、行数、单词数、哈希和符号链接目标。</summary>
public sealed class FileInfoTool : ITool
{
    private const long MaxTextStatsBytes = 20 * 1024 * 1024;

    public string Name => "file_info";
    public string Description =>
        "查看单个文件的大小、时间、编码、行数、单词数和可选 SHA256；不输出文件正文，适合先确认文件属性再决定是否读取。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "文件路径，相对工作区根目录" },
            ["count_lines"] = new JsonObject { ["type"] = "boolean", ["description"] = "统计文本行数（默认 true）" },
            ["count_words"] = new JsonObject { ["type"] = "boolean", ["description"] = "统计文本单词数（默认 false）" },
            ["hash"] = new JsonObject { ["type"] = "boolean", ["description"] = "计算并显示 SHA256（默认 false）" },
        },
        ["required"] = new JsonArray("path"),
    };

    public Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = ToolArgs.GetString(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            throw new ToolException("缺少必填参数 path");
        var full = ctx.Workspace.ResolveRead(path);
        if (Directory.Exists(full))
            throw new ToolException($"'{path}' 是目录，请指定具体文件。");
        if (!File.Exists(full))
            throw new ToolException($"文件不存在: {path}");

        var info = new FileInfo(full);
        var output = new System.Text.StringBuilder();
        output.AppendLine($"文件: {path}");
        output.AppendLine($"大小: {TextUtil.FormatBytes(info.Length)}（{info.Length:N0} 字节）");
        output.AppendLine($"修改时间: {File.GetLastWriteTime(full):yyyy-MM-dd HH:mm:ss}");
        output.AppendLine($"创建时间: {File.GetCreationTime(full):yyyy-MM-dd HH:mm:ss}");
        output.AppendLine($"扩展名: {(string.IsNullOrEmpty(info.Extension) ? "(无扩展名)" : info.Extension)}");

        var encoding = TextUtil.DetectFileEncoding(full);
        output.AppendLine($"编码: {encoding ?? "UTF-8（无 BOM）"}");

        var textStatsAllowed = info.Length <= MaxTextStatsBytes;
        if (textStatsAllowed)
        {
            var lines = ToolArgs.GetBool(args, "count_lines", true);
            if (lines)
            {
                var count = SkipDirs.CountFileLines(full, ct);
                if (count is { } lineCount)
                    output.AppendLine($"行数: {lineCount:N0}");
                else
                    output.AppendLine("行数: 读取失败");
            }
            if (ToolArgs.GetBool(args, "count_words", false))
            {
                var count = SkipDirs.CountFileWords(full, ct);
                if (count is { } wordCount)
                    output.AppendLine($"单词数: {wordCount:N0}");
                else
                    output.AppendLine("单词数: 读取失败");
            }
        }
        else
        {
            output.AppendLine($"文本统计: 跳过（文件超过 {MaxTextStatsBytes / 1024 / 1024} MB）");
        }

        if (ToolArgs.GetBool(args, "hash", false))
        {
            var hash = SkipDirs.ComputeFileSha256(full, ct);
            output.AppendLine(hash is null ? "SHA256: 读取失败" : $"SHA256: {hash}");
        }

        try
        {
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
                output.AppendLine($"符号链接: → {target.FullName}");
        }
        catch (IOException) { output.AppendLine("符号链接: 目标读取失败"); }
        catch (UnauthorizedAccessException) { output.AppendLine("符号链接: 无权读取目标"); }

        return Task.FromResult(output.ToString().TrimEnd());
    }
}
