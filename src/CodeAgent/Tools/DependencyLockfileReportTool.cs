using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读发现依赖锁定文件并汇总生态、大小和依赖条目数。</summary>
public sealed class DependencyLockfileReportTool : ITool
{
    private static readonly Dictionary<string, string> KnownFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["packages-lock.json"] = "npm",
        ["package-lock.json"] = "npm",
        ["yarn.lock"] = "Yarn",
        ["pnpm-lock.yaml"] = "pnpm",
        ["poetry.lock"] = "Poetry",
        ["Pipfile.lock"] = "Pipenv",
        ["Cargo.lock"] = "Cargo",
        ["go.sum"] = "Go modules",
        ["packages.config"] = "NuGet",
        ["packages.lock.json"] = "NuGet",
        ["project.assets.json"] = "NuGet assets",
    };

    public string Name => "dependency_lockfile_report";
    public string Description =>
        "只读发现 npm、Yarn、pnpm、Python、Cargo、Go、NuGet 等依赖锁定文件并汇总大小和依赖条目数。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "工作区内目录，默认根目录" },
            ["depth"] = new JsonObject { ["type"] = "integer", ["description"] = "递归深度（默认 8，最大 30）" },
            ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含隐藏目录（默认 false）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多分析锁定文件数（默认 100，最大 2000）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var depth = Math.Clamp(ToolArgs.GetInt(args, "depth", 8), 0, 30);
        var includeHidden = ToolArgs.GetBool(args, "include_hidden", false);
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", 100), 1, 2_000);
        var lockfiles = new List<string>();
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, depth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!includeHidden && SkipDirs.IsHiddenPath(file, root))
                continue;
            if (KnownFiles.ContainsKey(Path.GetFileName(file)))
            {
                lockfiles.Add(file);
                if (lockfiles.Count >= maxFiles)
                    break;
            }
        }
        var output = new StringBuilder();
        output.AppendLine($"依赖锁定报告: {lockfiles.Count:N0} 个锁定文件");
        foreach (var file in lockfiles)
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var name = Path.GetFileName(file);
            var ecosystem = KnownFiles[name];
            output.AppendLine($"  {relative} [{ecosystem}]，{info.Length:N0} 字节");
            try
            {
                if (info.Length > 5_242_880)
                    continue;
                var text = await File.ReadAllTextAsync(file, ct);
                var count = CountDependencies(name, text);
                if (count > 0)
                    output.AppendLine($"    依赖条目: {count:N0}");
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { output.AppendLine("    (无法读取锁定文件)"); }
            catch (UnauthorizedAccessException) { output.AppendLine("    (无权限读取锁定文件)"); }
            catch (JsonException) { output.AppendLine("    (锁定文件格式无法解析)"); }
        }
        return output.ToString().TrimEnd();
    }

    private static int CountDependencies(string name, string text)
    {
        if (name.Equals("project.assets.json", StringComparison.OrdinalIgnoreCase))
            return JsonNode.Parse(text) is JsonObject assets && assets["libraries"] is JsonObject libraries ? libraries.Count : 0;
        if (name.Equals("package-lock.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("packages-lock.json", StringComparison.OrdinalIgnoreCase))
        {
            if (JsonNode.Parse(text) is not JsonObject root)
                return 0;
            if (root["dependencies"] is JsonObject dependencies)
                return dependencies.Count;
            return root["packages"] is JsonObject packages ? packages.Count : 0;
        }
        if (name.Equals("yarn.lock", StringComparison.OrdinalIgnoreCase) || name.Equals("pnpm-lock.yaml", StringComparison.OrdinalIgnoreCase))
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(line => line.StartsWith("  ", StringComparison.Ordinal) && !line.TrimStart().StartsWith('#'));
        if (name.Equals("Cargo.lock", StringComparison.OrdinalIgnoreCase))
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(line => line.StartsWith("name = ", StringComparison.Ordinal));
        if (name.Equals("poetry.lock", StringComparison.OrdinalIgnoreCase) || name.Equals("Pipfile.lock", StringComparison.OrdinalIgnoreCase))
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(line => line.StartsWith("name = ", StringComparison.Ordinal) || line.StartsWith("\"name\"", StringComparison.Ordinal));
        if (name.Equals("go.sum", StringComparison.OrdinalIgnoreCase))
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(line => !line.StartsWith('#'));
        if (name.Equals("packages.config", StringComparison.OrdinalIgnoreCase))
            return text.Split('<').Count(part => part.StartsWith("package ", StringComparison.Ordinal));
        return 0;
    }
}
