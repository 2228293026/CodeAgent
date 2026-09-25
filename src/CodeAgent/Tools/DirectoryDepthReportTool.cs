using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读分析工作区目录深度和深层目录分布。</summary>
public sealed class DirectoryDepthReportTool : ITool
{
    public string Name => "directory_depth_report";
    public string Description =>
        "统计工作区目录深度分布并列出最深层目录，支持隐藏文件开关和结果限制。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "工作区内目录，默认根目录" },
            ["max_depth"] = new JsonObject { ["type"] = "integer", ["description"] = "最大扫描深度（默认 10，最大 50）" },
            ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含隐藏目录（默认 false）" },
            ["max_directories"] = new JsonObject { ["type"] = "integer", ["description"] = "最多列出的深层目录数（默认 20，最大 500）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 0, 50);
        var includeHidden = ToolArgs.GetBool(args, "include_hidden", false);
        var maxDirectories = Math.Clamp(ToolArgs.GetInt(args, "max_directories", 20), 1, 500);
        var distribution = new Dictionary<int, int>();
        var directories = new List<(int Depth, string Path)>();
        Walk(root, 0);
        var deepest = directories.OrderByDescending(item => item.Depth).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var output = new StringBuilder();
        output.AppendLine($"目录深度报告: {directories.Count:N0} 个目录，最大深度 {(deepest.Count == 0 ? 0 : deepest[0].Depth)}");
        foreach (var pair in distribution.OrderBy(pair => pair.Key))
            output.AppendLine($"  深度 {pair.Key}: {pair.Value:N0} 个目录");
        output.AppendLine("最深目录:");
        foreach (var item in deepest.Take(maxDirectories))
            output.AppendLine($"  {item.Depth}: {item.Path}");
        if (directories.Count > maxDirectories)
            output.AppendLine($"…（另有 {directories.Count - maxDirectories} 个目录未显示）");
        return output.ToString().TrimEnd();

        void Walk(string directory, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > 0)
            {
                var relative = Path.GetRelativePath(root, directory).Replace('\\', '/');
                if (includeHidden || !SkipDirs.IsHidden(directory))
                {
                    directories.Add((depth, relative));
                    distribution[depth] = distribution.GetValueOrDefault(depth) + 1;
                }
            }
            if (depth >= maxDepth)
                return;
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(directory); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(child);
                if (SkipDirs.IsSkipped(name) || (!includeHidden && SkipDirs.IsHidden(child)))
                    continue;
                try { ctx.Workspace.ResolveRead(child); } catch (ToolException) { continue; }
                Walk(child, depth + 1);
            }
        }
    }
}
