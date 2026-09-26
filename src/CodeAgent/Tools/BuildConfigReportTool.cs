using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查构建与发布配置：版本号未同步、缺少发布产物忽略、以及调试配置泄漏到发布。</summary>
public sealed class BuildConfigReportTool : ITool
{
    public string Name => "build_config_report";
    public string Description =>
        "只读检查构建/发布配置：版本号在各处不一致、Debug 配置被误用于发布、以及缺少 .gitignore 关键条目。";

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
        var versionDrift = new List<string>();
        var debugInRelease = new List<string>();
        var missingIgnore = new List<string>();
        var versions = new List<(string Source, string Value)>();
        var files = 0;
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            var isProject = name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
            if (!isProject && !name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "Directory.Build.props", StringComparison.Ordinal))
                continue;
            files++;
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                var lineNo = i + 1;
                var version = Regex.Match(trimmed, @"<(?:Version|VersionPrefix|AssemblyVersion|FileVersion)>([^<]+)<");
                if (version.Success)
                {
                    versions.Add(($"{relative}:{lineNo}", version.Groups[1].Value.Trim()));
                }
                // 发布配置里跑 Debug 构建
                if (Regex.IsMatch(trimmed, @"(?i)(dotnet\s+publish|dotnet\s+build)[^\n]*\s-c\s+Debug\b")
                    || Regex.IsMatch(trimmed, @"(?i)^\s*-c:\s*Debug\s*$"))
                    debugInRelease.Add($"{relative}:{lineNo} 发布/构建流水线使用 Debug 配置（应至少跑一次 Release）");
            }
        }
        // 版本号在各文件间是否一致
        var distinct = versions.Select(v => v.Value).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count > 1)
            foreach (var v in versions.Where(v => v.Value != distinct[0]))
                versionDrift.Add($"{v.Source} 版本 {v.Value} 与 {versions.First(x => x.Value == distinct[0]).Source} 的 {distinct[0]} 不一致");
        // .gitignore 关键条目
        var gitignore = Path.Combine(root, ".gitignore");
        if (File.Exists(gitignore))
        {
            var text = await File.ReadAllTextAsync(gitignore, ct);
            foreach (var entry in new[] { "bin/", "obj/" })
            {
                if (!text.Contains(entry, StringComparison.Ordinal))
                    missingIgnore.Add($".gitignore 缺少 {entry}（编译产物会被提交）");
            }
        }
        else
        {
            missingIgnore.Add("缺少 .gitignore（编译产物与本地配置会被提交）");
        }
        if (files == 0)
            return "构建配置报告: 未找到项目或流水线文件";
        var output = new StringBuilder();
        output.AppendLine($"构建配置报告: {files} 个项目/流水线文件，{versions.Count} 处版本声明");
        Report(output, "版本号不一致", versionDrift, maxResults);
        Report(output, "流水线用 Debug", debugInRelease, maxResults);
        Report(output, ".gitignore 问题", missingIgnore, maxResults);
        if (versionDrift.Count == 0 && debugInRelease.Count == 0 && missingIgnore.Count == 0)
            output.AppendLine("未发现构建配置问题");
        return output.ToString().TrimEnd();
    }

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
