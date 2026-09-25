using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>扫描工作区中的符号链接和 junction，不跟随链接目标并对外部路径脱敏。</summary>
public sealed class FindSymlinksTool : ITool
{
    public string Name => "find_symlinks";
    public string Description =>
        "查找工作区中的符号链接/junction，显示链接路径、目标类型和断链状态；不会跟随链接进入目标目录。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "扫描目录，默认工作区根目录" },
            ["max_depth"] = new JsonObject { ["type"] = "integer", ["description"] = "最大递归深度（默认 5，最大 50）" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示链接数（默认 100，最大 5000）" },
            ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含隐藏链接/目录（默认 false）" },
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
        var links = new List<(string Path, string Target, string Kind, bool Broken)>();
        var visited = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var scanned = 0;
        var capped = false;
        await Task.Yield();
        Walk(root, 0);
        var output = new StringBuilder();
        output.AppendLine($"扫描目录: {scanned:N0}，发现链接: {links.Count:N0}");
        if (capped)
            output.AppendLine($"达到 max_results={maxResults} 上限，结果可能不完整");
        if (links.Count == 0)
            return output.Append("没有发现符号链接或 junction。").ToString();
        foreach (var link in links)
            output.AppendLine($"{ctx.Workspace.ToRelative(link.Path)} -> {link.Target} [{link.Kind}{(link.Broken ? ", broken" : "")}]");
        return output.ToString().TrimEnd();

        void Walk(string directory, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > maxDepth || links.Count >= maxResults)
            {
                capped |= depth <= maxDepth && links.Count >= maxResults;
                return;
            }
            try
            {
                var info = new DirectoryInfo(directory);
                if (info.LinkTarget is not null)
                    return;
                var identity = Path.GetFullPath(directory);
                if (!visited.Add(identity))
                    return;
                scanned++;
                if (depth == maxDepth)
                    return;
                foreach (var child in Directory.EnumerateFileSystemEntries(directory).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (links.Count >= maxResults)
                    {
                        capped = true;
                        return;
                    }
                    if (!includeHidden && SkipDirs.IsHiddenPath(child, root))
                        continue;
                    var childDirectory = new DirectoryInfo(child);
                    var childFile = new FileInfo(child);
                    string? target = null;
                    var kind = "";
                    try { target = childDirectory.LinkTarget; kind = "directory"; } catch { }
                    if (target is null)
                    {
                        try { target = childFile.LinkTarget; kind = "file"; } catch { }
                    }
                    if (target is not null)
                    {
                        var targetPath = target;
                        if (!Path.IsPathRooted(targetPath))
                            targetPath = Path.Combine(directory, targetPath);
                        var broken = !File.Exists(targetPath) && !Directory.Exists(targetPath);
                        links.Add((child, DisplayTarget(targetPath, root), kind, broken));
                        continue;
                    }
                    if (Directory.Exists(child))
                    {
                        var name = Path.GetFileName(child);
                        if (SkipDirs.IsSkipped(name))
                            continue;
                        try { ctx.Workspace.ResolveRead(child); }
                        catch (ToolException) { continue; }
                        Walk(child, depth + 1);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string DisplayTarget(string target, string root)
    {
        var relative = Path.GetRelativePath(root, target).Replace('\\', '/');
        if (relative == ".")
            return ".";
        if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return "<external>/" + Path.GetFileName(target);
        return relative;
    }
}
