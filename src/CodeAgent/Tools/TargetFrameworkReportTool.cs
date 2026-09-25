using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace CodeAgent.Tools;

/// <summary>只读汇总各项目的目标框架（TFM），标出预览/EOL 风险与多目标框架项目。</summary>
public sealed class TargetFrameworkReportTool : ITool
{
    /// <summary>已停止支持的 .NET 主版本。</summary>
    private static readonly HashSet<string> Eol = new(StringComparer.OrdinalIgnoreCase)
    {
        "netcoreapp1.0", "netcoreapp1.1", "netcoreapp2.0", "netcoreapp2.1", "netcoreapp2.2", "netcoreapp3.0", "netcoreapp3.1",
        "net5.0", "net6.0", "net7.0", "net8.0", "netstandard1.0", "netstandard1.1", "netstandard1.2",
    };

    public string Name => "target_framework_report";
    public string Description =>
        "只读解析 csproj 的目标框架，标出预览版与已停止支持(EOL)的 TFM，并列出多目标框架项目。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示项目数（默认 100，最大 1000）" },
            ["max_depth"] = new JsonObject { ["type"] = "integer", ["description"] = "扫描深度上限（默认 8，最大 32）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 100), 1, 1_000);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 8), 1, 32);
        var projects = new List<(string File, string? Sdk, List<string> Frameworks)>();
        var unparsable = new List<string>();
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!Path.GetExtension(file).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                continue;
            XDocument doc;
            try { doc = XDocument.Load(file); }
            catch (System.Xml.XmlException) { unparsable.Add(Path.GetRelativePath(root, file).Replace('\\', '/')); continue; }
            catch (IOException) { continue; }
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var sdk = doc.Root?.Attribute("Sdk")?.Value;
            var frameworks = new List<string>();
            foreach (var prop in doc.Descendants())
            {
                if (prop.Name.LocalName != "TargetFramework" && prop.Name.LocalName != "TargetFrameworks")
                    continue;
                var value = prop.Value.Trim();
                if (value.Length == 0)
                    continue;
                foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var tfm = part.Trim();
                    if (tfm.Length > 0 && !frameworks.Contains(tfm, StringComparer.OrdinalIgnoreCase))
                        frameworks.Add(tfm);
                }
            }
            projects.Add((relative, sdk, frameworks));
        }
        if (projects.Count == 0)
        {
            // 全部解析失败时不能报「未发现 csproj」——那会把"项目读不了"说成"项目不存在"，
            // 用户会去找错方向。解析失败本身必须先说出来。
            return unparsable.Count == 0
                ? "目标框架报告: 未发现 csproj"
                : $"目标框架报告: 未发现可解析的 csproj（解析失败 {unparsable.Count} 个：{string.Join(", ", unparsable.Take(5))}）";
        }
        var byTfm = projects.SelectMany(p => p.Frameworks.Select(f => (Tfm: f, Project: p.File)))
            .GroupBy(x => x.Tfm, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var preview = byTfm.Where(g => g.Key.Contains("preview", StringComparison.OrdinalIgnoreCase)).ToList();
        var eol = byTfm.Where(g => Eol.Contains(Normalize(g.Key))).ToList();
        var multi = projects.Where(p => p.Frameworks.Count > 1).ToList();
        var output = new StringBuilder();
        output.AppendLine($"目标框架报告: {projects.Count} 个项目，{byTfm.Count} 种 TFM");
        output.AppendLine("TFM 分布:");
        foreach (var group in byTfm)
            output.AppendLine($"  {group.Key}: {group.Count()} 个项目");
        if (multi.Count > 0)
            output.AppendLine($"多目标框架项目: {multi.Count}");
        if (eol.Count > 0)
        {
            output.AppendLine("⚠ 已停止支持(EOL)的目标框架（建议升级）:");
            foreach (var group in eol)
                output.AppendLine($"  {group.Key}（{group.Count()} 个项目）");
        }
        if (preview.Count > 0)
        {
            output.AppendLine("⚠ 预览版目标框架（不宜用于生产）:");
            foreach (var group in preview)
                output.AppendLine($"  {group.Key}（{group.Count()} 个项目）");
        }
        output.AppendLine("明细:");
        foreach (var project in projects.OrderBy(p => p.File, StringComparer.Ordinal).Take(maxResults))
        {
            var frameworks = project.Frameworks.Count == 0 ? "(未声明)" : string.Join(", ", project.Frameworks);
            var sdk = project.Sdk is null ? string.Empty : $" [SDK {project.Sdk}]";
            output.AppendLine($"  {project.File}{sdk}: {frameworks}");
        }
        if (projects.Count > maxResults)
            output.AppendLine($"…（另有 {projects.Count - maxResults} 个项目未显示）");
        if (unparsable.Count > 0)
            output.AppendLine($"解析失败: {unparsable.Count} 个（{string.Join(", ", unparsable.Take(5))}）");
        return output.ToString().TrimEnd();
    }

    /// <summary>归一化 TFM 便于比对：net5.0 / .NETCoreApp,Version=v5.0 → net5.0。</summary>
    internal static string Normalize(string tfm)
    {
        var t = tfm.Trim();
        if (t.StartsWith(".NETCoreApp,Version=v", StringComparison.OrdinalIgnoreCase))
            return "netcoreapp" + t[".NETCoreApp,Version=v".Length..];
        if (t.StartsWith(".NETStandard,Version=v", StringComparison.OrdinalIgnoreCase))
            return "netstandard" + t[".NETStandard,Version=v".Length..];
        return t.ToLowerInvariant();
    }
}
