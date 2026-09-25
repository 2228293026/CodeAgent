using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>扫描项目中的空目录，帮助清理构建残留或无内容目录。</summary>
public sealed class FindEmptyDirectoriesTool : ITool
{
    public string Name => "find_empty_directories";
    public string Description =>
        "递归查找工作区中的空目录，支持最大深度、结果数量、隐藏目录开关，并跳过构建缓存目录和符号链接。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "扫描目录，默认工作区根目录" },
            ["max_depth"] = new JsonObject { ["type"] = "integer", ["description"] = "最大递归深度（默认 5，最大 50）" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示目录数（默认 100，最大 5000）" },
            ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含隐藏目录（默认 false）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 5), 0, 50);
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 100), 1, 5_000);
        var includeHidden = ToolArgs.GetBool(args, "include_hidden", false);
        var visited = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var empty = new List<string>();
        var scanned = 0;
        var capped = false;
        await Task.Yield();
        Walk(root, 0);
        var output = new StringBuilder();
        output.AppendLine($"扫描目录: {scanned:N0}，发现空目录: {empty.Count:N0}");
        if (capped)
            output.AppendLine($"达到 max_results={maxResults} 上限，结果可能不完整");
        if (empty.Count == 0)
            return output.Append("没有发现空目录。").ToString();
        foreach (var directory in empty)
            output.AppendLine(ctx.Workspace.ToRelative(directory));
        return output.ToString().TrimEnd();

        void Walk(string directory, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > maxDepth || empty.Count >= maxResults)
            {
                capped |= depth <= maxDepth && empty.Count >= maxResults;
                return;
            }
            string identity;
            try
            {
                var info = new DirectoryInfo(directory);
                if (info.LinkTarget is not null)
                    return;
                identity = Path.GetFullPath(info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? directory);
                if (!visited.Add(identity))
                    return;
                scanned++;
                var entries = Directory.EnumerateFileSystemEntries(directory).ToList();
                if (entries.Count == 0)
                {
                    empty.Add(directory);
                    return;
                }
                if (depth == maxDepth)
                    return;
                foreach (var child in entries.Where(Directory.Exists).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var name = Path.GetFileName(child);
                    if (SkipDirs.IsSkipped(name) || (!includeHidden && SkipDirs.IsHiddenPath(child, root)))
                        continue;
                    try { ctx.Workspace.ResolveRead(child); }
                    catch (ToolException) { continue; }
                    Walk(child, depth + 1);
                    if (empty.Count >= maxResults)
                    {
                        capped = true;
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
