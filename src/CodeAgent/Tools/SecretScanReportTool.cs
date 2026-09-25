using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读扫描工作区中疑似密钥、令牌和密码，并对匹配值脱敏。</summary>
public sealed class SecretScanReportTool : ITool
{
    private static readonly (string Name, Regex Pattern)[] Rules =
    {
        ("AWS access key", new Regex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled)),
        ("GitHub token", new Regex(@"\bgh[pousr]_[A-Za-z0-9]{20,}\b", RegexOptions.Compiled)),
        ("Private key", new Regex(@"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.Compiled)),
        ("Credential assignment", new Regex(@"(?i)\b(?:api[_-]?key|secret|password|passwd|access[_-]?token|auth[_-]?token)\b\s*[:=]\s*[""']?([^\s""'<>]{8,})", RegexOptions.Compiled)),
    };

    public string Name => "secret_scan_report";
    public string Description =>
        "只读扫描疑似 API key、GitHub token、私钥和凭据赋值；输出规则、路径和行号，匹配值始终脱敏。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "工作区内目录，默认根目录" },
            ["depth"] = new JsonObject { ["type"] = "integer", ["description"] = "递归深度（默认 8，最大 30）" },
            ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含隐藏目录（默认 false）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多扫描文件数（默认 10000，最大 100000）" },
            ["max_findings"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示发现数（默认 200，最大 5000）" },
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
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", 10_000), 1, 100_000);
        var maxFindings = Math.Clamp(ToolArgs.GetInt(args, "max_findings", 200), 1, 5_000);
        var findings = new List<string>();
        var scanned = 0;
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, depth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (scanned++ >= maxFiles || (!includeHidden && SkipDirs.IsHiddenPath(file, root)))
                continue;
            if (new FileInfo(file).Length > 1_048_576)
                continue;
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var line = lines[lineIndex];
                foreach (var rule in Rules)
                {
                    foreach (Match match in rule.Pattern.Matches(line))
                    {
                        var value = match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1].Value : match.Value;
                        findings.Add($"{relative}:{lineIndex + 1} [{rule.Name}] {Mask(value)}");
                        if (findings.Count >= maxFindings)
                            break;
                    }
                    if (findings.Count >= maxFindings)
                        break;
                }
                if (findings.Count >= maxFindings)
                    break;
            }
            if (findings.Count >= maxFindings)
                break;
        }
        var output = new StringBuilder();
        output.AppendLine($"疑似秘密扫描: {scanned:N0} 个文件，{findings.Count:N0} 个发现（值已脱敏）");
        foreach (var finding in findings.Take(maxFindings))
            output.AppendLine($"  {finding}");
        if (findings.Count > maxFindings)
            output.AppendLine($"…（另有 {findings.Count - maxFindings} 个发现未显示）");
        return output.ToString().TrimEnd();
    }

    private static string Mask(string value)
    {
        if (value.Length <= 4)
            return "****";
        return value[..2] + new string('*', Math.Min(12, value.Length - 4)) + value[^2..];
    }
}
