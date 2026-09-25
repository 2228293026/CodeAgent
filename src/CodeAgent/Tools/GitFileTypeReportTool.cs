using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读把已跟踪文件按类别（源码/测试/配置/文档/资源/其他）归类统计体积。</summary>
public sealed class GitFileTypeReportTool : ITool
{
    public string Name => "git_file_type_report";
    public string Description =>
        "只读按类别统计已跟踪文件的数量与体积（源码/测试/配置/文档/资源/其他），并标出源码目录里的二进制与资源文件。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["max_misplaced"] = new JsonObject { ["type"] = "integer", ["description"] = "最多列出错放文件数（默认 50，最大 500）" },
            ["timeout_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Git 命令超时秒数（默认 20，最大 120）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var start = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(start))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxMisplaced = Math.Clamp(ToolArgs.GetInt(args, "max_misplaced", 50), 0, 500);
        var timeoutSeconds = Math.Clamp(ToolArgs.GetInt(args, "timeout_seconds", 20), 1, 120);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var probe = await GitStatusTool.RunGitAsync(start, ["rev-parse", "--git-dir"], timeout.Token);
        if (probe.ExitCode != 0)
            throw new ToolException("当前目录不是 Git 仓库");
        var listed = await GitStatusTool.RunGitAsync(start, ["ls-files"], timeout.Token);
        if (listed.ExitCode != 0)
            throw new ToolException($"无法读取文件清单（退出码 {listed.ExitCode}）");
        var counts = new Dictionary<string, (int Files, long Bytes)>();
        long missing = 0;
        var misplaced = new List<string>();
        foreach (var raw in listed.Output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            ct.ThrowIfCancellationRequested();
            var file = raw.Trim();
            if (file.Length == 0)
                continue;
            var category = Categorize(file);
            long size = 0;
            try
            {
                var full = Path.Combine(start, file.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full))
                    size = new FileInfo(full).Length;
                else
                    missing++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            var current = counts.GetValueOrDefault(category);
            counts[category] = (current.Files + 1, current.Bytes + size);
            if (category is "资源" or "其他" && LooksLikeSourcePath(file))
                misplaced.Add($"{category} {size / 1024.0:0.0} KiB  {file}");
        }
        if (counts.Count == 0)
            return "Git 文件分类报告: 仓库没有已跟踪文件";
        var totalFiles = counts.Values.Sum(v => v.Files);
        var totalBytes = counts.Values.Sum(v => v.Bytes);
        var output = new StringBuilder();
        output.AppendLine($"Git 文件分类报告: {totalFiles:N0} 个已跟踪文件，{totalBytes / 1024.0 / 1024.0:0.00} MiB");
        foreach (var pair in counts.OrderByDescending(p => p.Value.Files).ThenBy(p => p.Key, StringComparer.Ordinal))
            output.AppendLine($"  {pair.Key}: {pair.Value.Files:N0} 个，{pair.Value.Bytes / 1024.0 / 1024.0:0.00} MiB");
        if (missing > 0)
            output.AppendLine($"  工作区缺失（已删除未提交）: {missing:N0} 个");
        if (misplaced.Count > 0)
        {
            output.AppendLine($"源码目录中的资源/其他文件: {misplaced.Count:N0} 个（确认是否该进版本库）");
            foreach (var line in misplaced.OrderBy(x => x, StringComparer.Ordinal).Take(maxMisplaced))
                output.AppendLine($"  {line}");
            if (misplaced.Count > maxMisplaced)
                output.AppendLine($"…（另有 {misplaced.Count - maxMisplaced} 个未显示）");
        }
        return output.ToString().TrimEnd();
    }

    /// <summary>按路径与扩展名归类已跟踪文件。</summary>
    internal static string Categorize(string path)
    {
        var lower = path.ToLowerInvariant();
        if (lower.Contains("/test", StringComparison.Ordinal) || lower.Contains("tests/", StringComparison.Ordinal)
            || lower.Contains(".test.", StringComparison.Ordinal) || lower.Contains(".tests.", StringComparison.Ordinal)
            || lower.EndsWith("test.cs", StringComparison.Ordinal))
            return "测试";
        var ext = Path.GetExtension(lower);
        if (ext is ".cs" or ".py" or ".js" or ".ts" or ".tsx" or ".jsx" or ".go" or ".rs" or ".java" or ".kt"
            or ".c" or ".h" or ".cpp" or ".hpp" or ".rb" or ".php" or ".swift")
            return "源码";
        if (ext is ".md" or ".txt" or ".rst")
            return "文档";
        if (ext is ".json" or ".yml" or ".yaml" or ".toml" or ".xml" or ".ini" or ".props" or ".targets" or ".config")
            return "配置";
        if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".ico" or ".webp" or ".mp3" or ".mp4"
            or ".woff" or ".woff2" or ".ttf" or ".zip" or ".dll" or ".exe" or ".pdb" or ".bin")
            return "资源";
        return "其他";
    }

    /// <summary>路径看起来像源码/测试目录（src、lib、tools 等），用于提示资源文件错放。</summary>
    internal static bool LooksLikeSourcePath(string path)
    {
        var lower = path.ToLowerInvariant().Replace('\\', '/');
        return lower.StartsWith("src/", StringComparison.Ordinal)
            || lower.StartsWith("lib/", StringComparison.Ordinal)
            || lower.StartsWith("app/", StringComparison.Ordinal)
            || lower.StartsWith("tools/", StringComparison.Ordinal)
            || lower.StartsWith("internal/", StringComparison.Ordinal);
    }
}
