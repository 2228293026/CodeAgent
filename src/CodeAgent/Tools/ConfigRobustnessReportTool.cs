using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查配置/环境变量的脆弱读取：直接读环境变量却不给兜底、
/// 解析失败静默用 0、以及从配置读数值却不校验范围。</summary>
public sealed class ConfigRobustnessReportTool : ITool
{
    public string Name => "config_robustness_report";
    public string Description =>
        "只读检查配置脆弱性：读环境变量不判空、解析失败静默用默认值/0、数值型配置项没做范围校验。";

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

    /// <summary>数值型配置键：这些值写错会一路传到深层才炸。</summary>
    internal static readonly string[] NumericKeys =
    {
        "timeout", "retries", "limit", "max", "min", "size", "count", "port", "depth", "ttl", "interval",
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
        var unguardedEnv = new List<string>();
        var silentParseFallback = new List<string>();
        var unvalidatedConfig = new List<string>();
        var files = 0;
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
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
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;
                var window = string.Join("\n", lines.Skip(i).Take(6));
                // 读环境变量后直接用，没有 ?? 兜底也没有判空/抛错
                if (Regex.IsMatch(trimmed, @"GetEnvironmentVariable\s*\(\s*""[^""]+""\s*\)\s*(?!\?\?|\?\.|[,)]|$)")
                    && !Regex.IsMatch(window, @"(?i)(IsNullOrEmpty|IsNullOrWhiteSpace|\?\?|throw|Default)"))
                    unguardedEnv.Add($"{relative}:{lineNo} 读环境变量后不判空也不兜底（部署漏配时得到 null，异常会出现在很远的地方）");
                // TryParse 失败用 ?: 常量 兜底 —— 环境变量名拼错会静默变成那个常量
                if (Regex.IsMatch(trimmed, @"(?:int|long|double|bool)\.TryParse\s*\([^,]+,\s*out\s+var\s+\w+\s*\)\s*\?")
                    && !Regex.IsMatch(window, @"(?i)(throw|Invalid|Error|日志)"))
                    silentParseFallback.Add($"{relative}:{lineNo} TryParse 失败后直接取三元另一支（配置值写错时会静默变成兜底值）");
                // 数值型配置键读出来没有范围校验
                var get = Regex.Match(trimmed, @"(?i)\.Get\w*\s*\(\s*""(?<k>timeout|retries|limit|max|min|size|count|port|depth|ttl|interval)""");
                if (get.Success && !Regex.IsMatch(window, @"(?i)(<=|>=|<|>|Math\.Clamp|Validate|IsValid|throw)"))
                    unvalidatedConfig.Add($"{relative}:{lineNo} 读了数值型配置 {get.Groups["k"].Value} 却没做范围校验（写成 0 或负数会一路传到深层才出错）");
            }
        }
        if (files == 0)
            return "配置健壮性报告: 未找到 .cs 文件";
        var output = new StringBuilder();
        output.AppendLine($"配置健壮性报告: {files} 个 .cs 文件");
        Report(output, "环境变量不判空", unguardedEnv, maxResults);
        Report(output, "解析失败静默兜底", silentParseFallback, maxResults);
        Report(output, "配置值无范围校验", unvalidatedConfig, maxResults);
        if (unguardedEnv.Count == 0 && silentParseFallback.Count == 0 && unvalidatedConfig.Count == 0)
            output.AppendLine("未发现配置健壮性问题");
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
