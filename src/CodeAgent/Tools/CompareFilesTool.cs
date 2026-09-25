using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>比较两个文本文件并返回统一格式的行级差异，适合代码审查和修改前确认。</summary>
public sealed class CompareFilesTool : ITool
{
    private const long MaxFileBytes = 20 * 1024 * 1024;
    private const int DefaultOutputLines = 2_000;
    private const int MaxOutputLines = 10_000;

    public string Name => "compare_files";
    public string Description =>
        "比较两个文本文件并返回 unified diff。支持忽略行尾空白/空行、限制输出行数；文件不存在、目录或二进制文件会给出明确错误。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["left"] = new JsonObject { ["type"] = "string", ["description"] = "左侧（旧）文件路径，相对工作区根目录" },
            ["right"] = new JsonObject { ["type"] = "string", ["description"] = "右侧（新）文件路径，相对工作区根目录" },
            ["ignore_whitespace"] = new JsonObject { ["type"] = "boolean", ["description"] = "忽略每行首尾空白（默认 false）" },
            ["ignore_blank_lines"] = new JsonObject { ["type"] = "boolean", ["description"] = "忽略空白行（默认 false）" },
            ["max_lines"] = new JsonObject { ["type"] = "integer", ["description"] = "最多输出差异行数（默认 2000，最大 10000）" },
        },
        ["required"] = new JsonArray("left", "right"),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var leftPath = ToolArgs.GetString(args, "left");
        var rightPath = ToolArgs.GetString(args, "right");
        if (string.IsNullOrWhiteSpace(leftPath) || string.IsNullOrWhiteSpace(rightPath))
            throw new ToolException("left 和 right 都是必填文件路径");

        var left = ResolveFile(ctx, leftPath, "left");
        var right = ResolveFile(ctx, rightPath, "right");
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return $"文件相同: {leftPath}（左右路径指向同一个文件）";

        var leftText = await ReadTextFileAsync(left, leftPath, ct);
        var rightText = await ReadTextFileAsync(right, rightPath, ct);
        var ignoreWhitespace = ToolArgs.GetBool(args, "ignore_whitespace", false);
        var ignoreBlankLines = ToolArgs.GetBool(args, "ignore_blank_lines", false);
        if (ignoreWhitespace || ignoreBlankLines)
        {
            leftText = NormalizeForCompare(leftText, ignoreWhitespace, ignoreBlankLines, ct);
            rightText = NormalizeForCompare(rightText, ignoreWhitespace, ignoreBlankLines, ct);
        }

        if (string.Equals(leftText, rightText, StringComparison.Ordinal))
            return $"文件内容相同: {leftPath} ↔ {rightPath}" +
                   (ignoreWhitespace || ignoreBlankLines ? "（已应用忽略规则）" : "");

        var label = $"{leftPath} → {rightPath}";
        var diff = DiffUtil.Unified(leftText, rightText, label, ct);
        var maxLines = Math.Clamp(ToolArgs.GetInt(args, "max_lines", DefaultOutputLines), 3, MaxOutputLines);
        var lines = diff.Replace("\r\n", "\n").Split('\n');
        if (lines.Length <= maxLines)
            return diff;

        var output = new StringBuilder();
        foreach (var line in lines.Take(maxLines))
            output.AppendLine(line);
        output.AppendLine($"…（差异输出超过 {maxLines} 行，已截断）");
        return output.ToString().TrimEnd();
    }

    private static string ResolveFile(AgentContext ctx, string path, string label)
    {
        var full = ctx.Workspace.ResolveRead(path);
        if (Directory.Exists(full))
            throw new ToolException($"{label} 是目录: {path}（compare_files 需要两个文本文件）");
        if (!File.Exists(full))
            throw new ToolException($"{label} 文件不存在: {path}");
        return full;
    }

    private static async Task<string> ReadTextFileAsync(string full, string displayPath, CancellationToken ct)
    {
        var length = new FileInfo(full).Length;
        if (length > MaxFileBytes)
            throw new ToolException($"文件过大（{displayPath}，{length / 1024 / 1024} MB），请先缩小文件或使用分段读取");
        var text = await TextUtil.ReadTextSmartAsync(full, ct);
        if (SkipDirs.LooksBinary(text))
            throw new ToolException($"文件疑似二进制，无法进行文本 diff: {displayPath}");
        return text;
    }

    private static string NormalizeForCompare(string text, bool ignoreWhitespace, bool ignoreBlankLines, CancellationToken ct)
    {
        var lines = DiffUtil.SplitLines(text, ct)
            .Select(line => ignoreWhitespace ? line.Trim() : line)
            .Where(line => !ignoreBlankLines || line.Length > 0)
            .ToList();
        return string.Join("\n", lines);
    }
}
