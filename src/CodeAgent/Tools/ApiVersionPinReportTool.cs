using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查项目文件的依赖版本问题：版本用范围（^ ~ *）而不是锁死、
/// 引用预发布版本、以及不同项目引用同一个包却锁在不同版本。</summary>
public sealed class ApiVersionPinReportTool : ITool
{
    public string Name => "api_version_pin_report";
    public string Description =>
        "只读检查项目文件依赖版本：PackageReference 用 ^ ~ * 范围而非锁死、引用预发布版本、同一个包在不同项目锁了不同版本。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示每类问题数（默认 30，最大 300）" },
            ["max_depth"] = new JsonObject { ["type"] = "integer", ["description"] = "扫描深度上限（默认 10，最大 32）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 30), 1, 300);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        var rangeVersion = new List<string>();
        var preRelease = new List<string>();
        var inconsistent = new List<string>();
        var files = 0;
        // 包名 → 版本 → 出现在哪些项目文件
        var versionsByPackage = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            if (!name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith("packages.config", StringComparison.OrdinalIgnoreCase))
                continue;
            files++;
            string text;
            try { text = await File.ReadAllTextAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var lineNo = 0;
            foreach (var raw in text.Split('\n'))
            {
                lineNo++;
                var trimmed = raw.Trim();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("<!--", StringComparison.Ordinal))
                    continue;
                var m = Regex.Match(trimmed, @"<PackageReference\s+Include\s*=\s*""(?<p>[^""]+)""(?:\s+Version\s*=\s*""(?<v>[^""]+)"")?");
                if (!m.Success)
                    continue;
                var package = m.Groups["p"].Value;
                var version = m.Groups["v"].Success ? m.Groups["v"].Value : string.Empty;
                if (version.Length == 0)
                {
                    // 版本可能写在 Update 元素里；拿不到就是没锁
                    rangeVersion.Add($"{relative}:{lineNo} {package} 没有写 Version（在中央包管理之外时 NuGet 会解析成不确定版本）");
                    continue;
                }
                if (!versionsByPackage.TryGetValue(package, out var versions))
                {
                    versions = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                    versionsByPackage[package] = versions;
                }
                if (!versions.TryGetValue(version, out var filesList))
                {
                    filesList = new List<string>();
                    versions[version] = filesList;
                }
                filesList.Add(relative);
                if (IsRange(version))
                    rangeVersion.Add($"{relative}:{lineNo} {package} 版本 {version} 是范围（^/~/* 会随还原时间漂移，CI 今天构建通过明天可能就换了版本）");
                if (IsPreRelease(version))
                    preRelease.Add($"{relative}:{lineNo} {package} 锁了预发布版本 {version}（正式发布前 API 仍可能变）");
            }
        }
        if (files == 0)
            return "依赖版本报告: 未找到项目文件";
        foreach (var (package, versions) in versionsByPackage.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (versions.Count <= 1)
                continue;
            var rendered = string.Join(" / ", versions.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}（{string.Join(", ", v.Value)}）"));
            inconsistent.Add($"{package}: {rendered}——同一个包锁了 {versions.Count} 个版本，依赖解析结果取决于谁先被还原");
        }
        var output = new StringBuilder();
        output.AppendLine($"依赖版本报告: {files} 个项目文件，{versionsByPackage.Count} 个包");
        Report(output, "版本未锁死", rangeVersion, maxResults);
        Report(output, "预发布版本", preRelease, maxResults);
        Report(output, "同一包多版本", inconsistent, maxResults);
        if (rangeVersion.Count == 0 && preRelease.Count == 0 && inconsistent.Count == 0)
            output.AppendLine("未发现依赖版本问题");
        return output.ToString().TrimEnd();
    }

    /// <summary>是否是范围而非锁定版本。纯数字（含 4 段修订号）算锁定。</summary>
    internal static bool IsRange(string version) =>
        version.Length == 0 || Regex.IsMatch(version, @"^[\^~*]|[,]|^\s*[\d]+\s*-\s*[\d]+");

    /// <summary>是否是预发布版本：1.0.0-beta / 1.0.0-rc.1 …</summary>
    internal static bool IsPreRelease(string version) =>
        Regex.IsMatch(version, @"^\d+(\.\d+){1,3}-[A-Za-z]", RegexOptions.CultureInvariant);

    private static void Report(StringBuilder output, string title, List<string> items, int maxResults)
    {
        if (items.Count == 0)
            return;
        output.AppendLine($"{title}: {items.Count}");
        foreach (var item in items.OrderBy(x => x, StringComparer.Ordinal).Take(maxResults))
            output.AppendLine($"  {item}");
        if (items.Count > maxResults)
            output.AppendLine($"  …（另有 {items.Count - maxResults} 处未显示）");
    }
}
