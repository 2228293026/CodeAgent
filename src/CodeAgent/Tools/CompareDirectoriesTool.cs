using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>比较两个工作区目录，报告新增/删除/修改文件并为文本差异生成摘要。</summary>
public sealed class CompareDirectoriesTool : ITool
{
    private const int DefaultMaxFiles = 200;
    private const int MaxFiles = 2_000;
    private readonly CompareFilesTool _comparer = new();

    public string Name => "compare_directories";
    public string Description =>
        "比较两个工作区目录，列出仅左侧/仅右侧/内容不同的文件；相同文件用 SHA256 快速跳过，文本差异复用 compare_files。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["left"] = new JsonObject { ["type"] = "string", ["description"] = "左侧目录路径" },
            ["right"] = new JsonObject { ["type"] = "string", ["description"] = "右侧目录路径" },
            ["ignore_whitespace"] = new JsonObject { ["type"] = "boolean", ["description"] = "比较时忽略行首尾空白（默认 false）" },
            ["ignore_blank_lines"] = new JsonObject { ["type"] = "boolean", ["description"] = "比较时忽略空白行（默认 false）" },
            ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含隐藏文件/目录（默认 false）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多比较的公共文件数（默认 200，最大 2000）" },
            ["max_output_chars"] = new JsonObject { ["type"] = "integer", ["description"] = "最大输出字符数（默认 100000，最大 500000）" },
            ["stop_on_error"] = new JsonObject { ["type"] = "boolean", ["description"] = "遇到不可比较文件时停止（默认 false）" },
        },
        ["required"] = new JsonArray("left", "right"),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var leftPath = ToolArgs.GetString(args, "left");
        var rightPath = ToolArgs.GetString(args, "right");
        if (string.IsNullOrWhiteSpace(leftPath) || string.IsNullOrWhiteSpace(rightPath))
            throw new ToolException("left 和 right 都是必填目录路径");
        var left = ctx.Workspace.ResolveRead(leftPath);
        var right = ctx.Workspace.ResolveRead(rightPath);
        if (!Directory.Exists(left))
            throw new ToolException($"左侧目录不存在: {leftPath}");
        if (!Directory.Exists(right))
            throw new ToolException($"右侧目录不存在: {rightPath}");
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", DefaultMaxFiles), 1, MaxFiles);
        var maxOutput = Math.Clamp(ToolArgs.GetInt(args, "max_output_chars", 100_000), 1_000, 500_000);
        var ignoreWhitespace = ToolArgs.GetBool(args, "ignore_whitespace", false);
        var ignoreBlankLines = ToolArgs.GetBool(args, "ignore_blank_lines", false);
        var includeHidden = ToolArgs.GetBool(args, "include_hidden", false);
        var stopOnError = ToolArgs.GetBool(args, "stop_on_error", false);
        var leftFiles = EnumerateRelativeFiles(left, includeHidden, ct);
        var rightFiles = EnumerateRelativeFiles(right, includeHidden, ct);
        var onlyLeft = leftFiles.Except(rightFiles, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var onlyRight = rightFiles.Except(leftFiles, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var common = leftFiles.Intersect(rightFiles, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var compared = Math.Min(common.Count, maxFiles);
        var output = new StringBuilder();
        output.AppendLine($"目录比较: {leftPath} ↔ {rightPath}");
        output.AppendLine($"扫描文件: 左 {leftFiles.Count:N0}，右 {rightFiles.Count:N0}；仅左 {onlyLeft.Count:N0}，仅右 {onlyRight.Count:N0}，公共 {common.Count:N0}");
        if (common.Count > compared)
            output.AppendLine($"达到 max_files={maxFiles} 上限，仅比较前 {compared} 个公共文件");
        AppendPaths(output, "仅左侧", onlyLeft);
        AppendPaths(output, "仅右侧", onlyRight);
        var same = 0;
        var different = 0;
        var failed = 0;
        for (var i = 0; i < compared; i++)
        {
            ct.ThrowIfCancellationRequested();
            var relative = common[i];
            var leftFull = Path.Combine(left, relative);
            var rightFull = Path.Combine(right, relative);
            var leftHash = SkipDirs.ComputeFileSha256(leftFull, ct);
            var rightHash = SkipDirs.ComputeFileSha256(rightFull, ct);
            if (leftHash is not null && leftHash == rightHash)
            {
                same++;
                continue;
            }
            try
            {
                var diff = await _comparer.ExecuteAsync(new JsonObject
                {
                    ["left"] = CombinePath(leftPath, relative),
                    ["right"] = CombinePath(rightPath, relative),
                    ["ignore_whitespace"] = ignoreWhitespace,
                    ["ignore_blank_lines"] = ignoreBlankLines,
                    ["max_lines"] = 500,
                }, ctx, ct);
                if (diff.Contains("内容相同", StringComparison.Ordinal))
                    same++;
                else
                {
                    different++;
                    output.AppendLine($"=== 修改: {relative} ===");
                    output.AppendLine(diff);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
            {
                failed++;
                if (stopOnError)
                    throw new ToolException($"比较文件失败 {relative}: {ex.Message}");
                output.AppendLine($"无法比较 {relative}: {ex.Message}");
            }
        }
        output.Insert(0, $"结果: 相同 {same}，修改 {different}，无法比较 {failed}\n");
        return Limit(output.ToString().TrimEnd(), maxOutput);
    }

    private static HashSet<string> EnumerateRelativeFiles(string root, bool includeHidden, CancellationToken ct)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            if (!includeHidden && SkipDirs.IsHiddenPath(file, root))
                continue;
            files.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
        }
        return files;
    }

    private static string CombinePath(string root, string relative) => root.TrimEnd('/', '\\') + "/" + relative;

    private static void AppendPaths(StringBuilder output, string title, IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0)
            return;
        output.AppendLine($"{title}（{paths.Count}）:");
        foreach (var path in paths)
            output.AppendLine($"  {path}");
    }

    private static string Limit(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        const string marker = "\n…（目录比较输出已截断）";
        return text[..Math.Max(0, maxChars - marker.Length)] + marker;
    }
}
