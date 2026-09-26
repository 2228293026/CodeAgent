using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查配置健壮性：硬编码密钥、拼写错误未告警的键、以及危险的默认值。</summary>
public sealed class ConfigHardeningReportTool : ITool
{
    public string Name => "config_hardening_report";
    public string Description =>
        "只读检查配置健壮性：源码里的硬编码密钥、config.json 里的明文口令、以及危险默认（跳过校验/信任所有证书）。";

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

    private static readonly string[] SecretKeys = ["apikey", "api_key", "secret", "password", "token", "credential", "privatekey"];

    /// <summary>各家密钥的固定前缀：变量名可能叫 Key/Token 而不含上面的词，
    /// 但值的前缀不会变——只认键名会漏掉 `const string Key = "sk-…"` 这种最常见的写法。</summary>
    private static readonly string[] SecretPrefixes = ["sk-", "sk_", "ghp_", "gho_", "github_pat_", "xoxb-", "xoxp-", "AKIA", "AIza", "eyJ"];

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 30), 1, 300);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        var hardcoded = new List<string>();
        var plaintextConfig = new List<string>();
        var insecureDefault = new List<string>();
        var files = 0;
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file).ToLowerInvariant();
            if (!name.EndsWith(".cs", StringComparison.Ordinal)
                && !name.EndsWith(".json", StringComparison.Ordinal)
                && !name.EndsWith(".config", StringComparison.Ordinal))
                continue;
            files++;
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var isConfig = name.EndsWith(".json", StringComparison.Ordinal) || name.EndsWith(".config", StringComparison.Ordinal);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                var lineNo = i + 1;
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    continue;
                if (Regex.IsMatch(trimmed, @"(?i)""[A-Za-z0-9_\-]{16,}""")
                    && (SecretKeys.Any(k => trimmed.Contains(k, StringComparison.OrdinalIgnoreCase))
                        || SecretPrefixes.Any(p => trimmed.Contains(p, StringComparison.Ordinal))))
                    hardcoded.Add($"{relative}:{lineNo} 疑似硬编码密钥（{TextUtil.TruncateLine(trimmed, 50)}）");
                if (isConfig && Regex.IsMatch(trimmed, @"(?i)""(apiKey|api_key|secret|password|token)""\s*:\s*""[^""]+"""))
                    plaintextConfig.Add($"{relative}:{lineNo} 配置文件里存明文口令（应改用环境变量）");
                if (Regex.IsMatch(trimmed, @"ServerCertificateValidationCallback\s*=\s*\(\s*\w+\s*,\s*\w+\s*,\s*\w+\s*,\s*\w+\s*\)\s*=>\s*true", RegexOptions.IgnoreCase)
                    || Regex.IsMatch(trimmed, @"DangerousAcceptAnyServerCertificateValidator", RegexOptions.IgnoreCase))
                    insecureDefault.Add($"{relative}:{lineNo} 信任所有服务端证书（HTTPS 失去意义）");
            }
        }
        if (files == 0)
            return "配置健壮性报告: 未找到相关文件";
        var output = new StringBuilder();
        output.AppendLine($"配置健壮性报告: {files} 个文件");
        Report(output, "疑似硬编码密钥", hardcoded, maxResults);
        Report(output, "配置内明文口令", plaintextConfig, maxResults);
        Report(output, "不安全默认", insecureDefault, maxResults);
        if (hardcoded.Count == 0 && plaintextConfig.Count == 0 && insecureDefault.Count == 0)
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
