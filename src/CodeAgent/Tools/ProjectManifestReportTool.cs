using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读发现项目清单文件并汇总主要生态和配置信息。</summary>
public sealed class ProjectManifestReportTool : ITool
{
    private static readonly HashSet<string> ManifestNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", "requirements.txt", "pyproject.toml", "cargo.toml", "go.mod", "pom.xml",
        "build.gradle", "build.gradle.kts", "gemfile", "composer.json", "pubspec.yaml", "deno.json", "deno.jsonc",
    };

    public string Name => "project_manifest_report";
    public string Description =>
        "只读发现 package.json、requirements.txt、pyproject.toml、Cargo.toml、go.mod 等项目清单并汇总生态信息。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "工作区内目录，默认根目录" },
            ["depth"] = new JsonObject { ["type"] = "integer", ["description"] = "递归深度（默认 5，最大 20）" },
            ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含隐藏目录（默认 false）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多处理的清单数（默认 100，最大 5000）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var depth = Math.Clamp(ToolArgs.GetInt(args, "depth", 5), 0, 20);
        var includeHidden = ToolArgs.GetBool(args, "include_hidden", false);
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", 100), 1, 5_000);
        var manifests = new List<string>();
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, depth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!includeHidden && SkipDirs.IsHiddenPath(file, root))
                continue;
            var name = Path.GetFileName(file);
            if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
                || ManifestNames.Contains(name))
            {
                manifests.Add(file);
                if (manifests.Count >= maxFiles)
                    break;
            }
        }
        var output = new StringBuilder();
        output.AppendLine($"项目清单报告: {manifests.Count:N0} 个清单");
        foreach (var file in manifests)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var name = Path.GetFileName(file);
            var ecosystem = name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) ? ".NET" :
                name.Equals("package.json", StringComparison.OrdinalIgnoreCase) ? "Node.js" :
                name.Equals("requirements.txt", StringComparison.OrdinalIgnoreCase) ? "Python" :
                name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) ? "Python" :
                name.Equals("cargo.toml", StringComparison.OrdinalIgnoreCase) ? "Rust" :
                name.Equals("go.mod", StringComparison.OrdinalIgnoreCase) ? "Go" :
                name.Equals("pom.xml", StringComparison.OrdinalIgnoreCase) ? "Maven" :
                name.Equals("composer.json", StringComparison.OrdinalIgnoreCase) ? "PHP" :
                name.Equals("pubspec.yaml", StringComparison.OrdinalIgnoreCase) ? "Dart" : "Build";
            output.AppendLine($"  {relative} [{ecosystem}]");
            try
            {
                var text = await File.ReadAllTextAsync(file, ct);
                if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase))
                {
                    var frameworks = System.Text.RegularExpressions.Regex.Matches(text, "<TargetFrameworks?>([^<]+)</TargetFrameworks?>", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                        .Select(match => match.Groups[1].Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    var packages = System.Text.RegularExpressions.Regex.Matches(text, "PackageReference\\s+Include=\"([^\"]+)\"").Count;
                    output.AppendLine($"    TargetFramework: {(frameworks.Length == 0 ? "(未声明)" : string.Join(", ", frameworks))}; PackageReference: {packages}");
                }
                else if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase))
                {
                    var node = JsonNode.Parse(text) as JsonObject;
                    var packageName = node?["name"]?.GetValue<string>() ?? "(未命名)";
                    var dependencies = node?["dependencies"] as JsonObject;
                    var devDependencies = node?["devDependencies"] as JsonObject;
                    output.AppendLine($"    name: {packageName}; dependencies: {dependencies?.Count ?? 0}; devDependencies: {devDependencies?.Count ?? 0}");
                }
                else if (name.Equals("requirements.txt", StringComparison.OrdinalIgnoreCase))
                {
                    var count = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(line => !line.TrimStart().StartsWith('#'));
                    output.AppendLine($"    requirements: {count}");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { output.AppendLine("    (无法读取清单内容)"); }
            catch (UnauthorizedAccessException) { output.AppendLine("    (无权限读取清单内容)"); }
        }
        return output.ToString().TrimEnd();
    }
}
