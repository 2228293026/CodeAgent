using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读统计代码里引用的环境变量名，便于核对部署配置与文档。</summary>
public sealed class EnvVarReferenceReportTool : ITool
{
    private static readonly Regex Call = new(
        @"GetEnvironmentVariable\s*\(\s*""(?<name>[A-Za-z_][A-Za-z0-9_]*)""", RegexOptions.Compiled);

    public string Name => "env_var_reference_report";
    public string Description =>
        "只读扫描 GetEnvironmentVariable 调用，汇总代码引用的环境变量名、出现次数和文件位置。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "仓库内目录，默认工作区根目录" },
            ["include_builtin"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含本程序内置的变量名（默认 true）" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示引用行数（默认 100，最大 2000）" },
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
        var includeBuiltin = ToolArgs.GetBool(args, "include_builtin", true);
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 100), 1, 2_000);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 8), 1, 32);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sites = new List<(string Name, string File, int Line)>();
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                continue;
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match match in Call.Matches(lines[i]))
                {
                    var name = match.Groups["name"].Value;
                    counts[name] = counts.GetValueOrDefault(name) + 1;
                    sites.Add((name, Path.GetRelativePath(root, file).Replace('\\', '/'), i + 1));
                }
            }
        }
        var output = new StringBuilder();
        var total = counts.Values.Sum();
        output.AppendLine($"环境变量引用报告: {counts.Count} 个变量，{total} 处引用");
        if (includeBuiltin)
        {
            var builtin = KnownVariables.Where(v => !counts.ContainsKey(v)).ToArray();
            if (builtin.Length > 0)
                output.AppendLine($"  常见但未在代码中引用: {string.Join(", ", builtin)}");
        }
        output.AppendLine("引用明细:");
        foreach (var pair in counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            output.AppendLine($"  {pair.Key}: {pair.Value}");
        output.AppendLine("位置:");
        foreach (var site in sites.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.File, StringComparer.Ordinal).ThenBy(s => s.Line).Take(maxResults))
            output.AppendLine($"  {site.Name} @ {site.File}:{site.Line}");
        if (sites.Count > maxResults)
            output.AppendLine($"…（另有 {sites.Count - maxResults} 处未显示）");
        return output.ToString().TrimEnd();
    }

    /// <summary>常见于本类 CLI 的约定变量名，用于提示「文档里有、代码里没读」的缺口。</summary>
    private static readonly string[] KnownVariables =
    {
        "ANTHROPIC_API_KEY", "OPENAI_API_KEY", "OPENAI_BASE_URL", "OPENAI_ORGANIZATION",
        "ANTHROPIC_BASE_URL", "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "CODEX_HOME",
    };
}
