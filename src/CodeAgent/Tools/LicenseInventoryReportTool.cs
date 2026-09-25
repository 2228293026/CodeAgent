using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读发现许可证文件、包清单许可证字段和 SPDX 标记。</summary>
public sealed class LicenseInventoryReportTool : ITool
{
    public string Name => "license_inventory_report";
    public string Description =>
        "只读发现 LICENSE/COPYING 文件、项目清单许可证字段和源码 SPDX 标记，汇总许可证线索。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "工作区内目录，默认根目录" },
            ["depth"] = new JsonObject { ["type"] = "integer", ["description"] = "递归深度（默认 6，最大 20）" },
            ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含隐藏目录（默认 false）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多扫描文件数（默认 10000，最大 100000）" },
            ["max_paths"] = new JsonObject { ["type"] = "integer", ["description"] = "最多列出的许可证文件数（默认 100，最大 1000）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var depth = Math.Clamp(ToolArgs.GetInt(args, "depth", 6), 0, 20);
        var includeHidden = ToolArgs.GetBool(args, "include_hidden", false);
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", 10_000), 1, 100_000);
        var maxPaths = Math.Clamp(ToolArgs.GetInt(args, "max_paths", 100), 1, 1_000);
        var licenseFiles = new List<string>();
        var manifestLicenses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var spdx = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scanned = 0;
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, depth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (scanned++ >= maxFiles || (!includeHidden && SkipDirs.IsHiddenPath(file, root)))
                continue;
            var name = Path.GetFileName(file);
            var lower = name.ToLowerInvariant();
            if (lower.StartsWith("license", StringComparison.Ordinal) || lower.StartsWith("copying", StringComparison.Ordinal))
                licenseFiles.Add(file);
            if (!IsTextCandidate(file))
                continue;
            string text;
            try
            {
                if (new FileInfo(file).Length > 1_048_576)
                    continue;
                text = await File.ReadAllTextAsync(file, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (Match match in System.Text.RegularExpressions.Regex.Matches(text, @"SPDX-License-Identifier:\s*([^\s*]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                spdx.Add(match.Groups[1].Value.TrimEnd('.', ',', ';'));
            if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (JsonNode.Parse(text) is JsonObject package && package["license"] is JsonValue value)
                        manifestLicenses[Path.GetRelativePath(root, file).Replace('\\', '/')] = value.ToString();
                }
                catch (JsonException) { }
            }
            else if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var match = System.Text.RegularExpressions.Regex.Match(text, @"PackageLicenseExpression\s*(?:=\s*[""']([^""']+)[""']|>\s*([^<]+)<)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                    manifestLicenses[Path.GetRelativePath(root, file).Replace('\\', '/')] = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value).Trim();
            }
        }
        var output = new StringBuilder();
        output.AppendLine($"许可证清单报告: {licenseFiles.Count:N0} 个许可证文件，{spdx.Count:N0} 个 SPDX 标识");
        foreach (var pair in manifestLicenses.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            output.AppendLine($"  清单许可证: {pair.Key} = {pair.Value}");
        if (spdx.Count > 0)
            output.AppendLine($"  SPDX: {string.Join(", ", spdx.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))}");
        output.AppendLine("许可证文件:");
        foreach (var file in licenseFiles.Take(maxPaths))
            output.AppendLine($"  {Path.GetRelativePath(root, file).Replace('\\', '/')}");
        if (licenseFiles.Count > maxPaths)
            output.AppendLine($"…（另有 {licenseFiles.Count - maxPaths} 个许可证文件未显示）");
        return output.ToString().TrimEnd();
    }

    private static bool IsTextCandidate(string file)
    {
        var extension = Path.GetExtension(file).ToLowerInvariant();
        return extension is ".json" or ".csproj" or ".fsproj" or ".vbproj" or ".md" or ".txt" or ".c" or ".h" or ".cpp" or ".hpp" or ".py" or ".js" or ".ts" or ".rs" or ".go" or ".java";
    }
}
