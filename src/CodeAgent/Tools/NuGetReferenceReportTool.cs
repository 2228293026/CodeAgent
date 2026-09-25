using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查 NuGet 包引用健康度：浮动版本、重复引用、已弃用包名与缺失锁定信息。</summary>
public sealed class NuGetReferenceReportTool : ITool
{
    /// <summary>已被官方弃用/停用的常见包名（片段匹配，避免依赖完整清单）。</summary>
    private static readonly (string Fragment, string Advice)[] Deprecated =
    {
        ("Newtonsoft.Json", "官方推荐改用 System.Text.Json"),
        ("Microsoft.AspNetCore.Mvc", "已被 ASP.NET Core 内置取代"),
        ("EntityFramework", "考虑改用 EF Core"),
        ("System.Web", "ASP.NET Core 无此命名空间"),
        ("Microsoft.Owin", "已被 ASP.NET Core 中间件取代"),
        ("packages.config", "已迁移到 PackageReference"),
    };

    public string Name => "nuget_reference_report";
    public string Description =>
        "只读审查 PackageReference：浮动版本、同一包重复引用、已弃用包名，以及缺少中央包版本管理的项目。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
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
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 8), 1, 32);
        var projects = new List<string>();
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                projects.Add(file);
        }
        if (projects.Count == 0)
            return "NuGet 引用报告: 未发现 csproj";
        var floating = new List<string>();
        var duplicates = new List<string>();
        var deprecated = new List<string>();
        var totalRefs = 0;
        var propsFiles = 0;
        foreach (var file in projects)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            string text;
            try { text = await File.ReadAllTextAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            // 浮动版本：* 与不带版本的 Include 引用都会随每次还原漂移
            var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(text, @"<PackageReference\s+[^>]*?Include\s*=\s*""([^""]+)""\s*(?:Version\s*=\s*""([^""]*)"")?"))
            {
                var id = m.Groups[1].Value;
                var version = m.Groups[2].Success ? m.Groups[2].Value : string.Empty;
                totalRefs++;
                if (!string.IsNullOrEmpty(version) && (version.Contains('*', StringComparison.Ordinal)
                    || version.StartsWith('[') || version.Contains(",", StringComparison.Ordinal)))
                    floating.Add($"{relative}: {id} 使用浮动版本 {version}");
                else if (string.IsNullOrEmpty(version))
                    floating.Add($"{relative}: {id} 未指定 Version（版本来自其他来源，无法审计）");
                ids[id] = ids.GetValueOrDefault(id) + 1;
            }
            foreach (var pair in ids.Where(p => p.Value > 1))
                duplicates.Add($"{relative}: {pair.Key} 被引用 {pair.Value} 次（版本可能冲突）");
            foreach (var (fragment, advice) in Deprecated)
            {
                if (text.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    deprecated.Add($"{relative}: {fragment} —— {advice}");
            }
            if (text.Contains("Directory.Packages.props", StringComparison.OrdinalIgnoreCase))
                propsFiles++;
        }
        var output = new StringBuilder();
        output.AppendLine($"NuGet 引用报告: {projects.Count} 个项目，{totalRefs} 个 PackageReference");
        Report(output, "浮动/缺失版本", floating);
        Report(output, "同一包重复引用", duplicates);
        Report(output, "已弃用包名", deprecated);
        if (propsFiles == 0 && projects.Count > 1)
            output.AppendLine("提示: 多个项目但没有 Directory.Packages.props（中央包版本管理），版本升级要逐个改");
        if (floating.Count == 0 && duplicates.Count == 0 && deprecated.Count == 0)
            output.AppendLine("未发现包引用问题");
        return output.ToString().TrimEnd();
    }

    private static void Report(StringBuilder output, string title, List<string> items)
    {
        if (items.Count == 0)
            return;
        output.AppendLine($"{title}: {items.Count}");
        foreach (var item in items.OrderBy(x => x, StringComparer.Ordinal))
            output.AppendLine($"  {item}");
    }
}
