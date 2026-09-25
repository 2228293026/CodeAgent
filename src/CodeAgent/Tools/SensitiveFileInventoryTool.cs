using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>只读发现可能包含凭据或私密配置的文件名，不读取文件内容。</summary>
public sealed class SensitiveFileInventoryTool : ITool
{
    public string Name => "sensitive_file_inventory";
    public string Description =>
        "只读发现 .env、私钥、证书、凭据和包管理器认证配置文件名；不读取或输出文件内容。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "工作区内目录，默认根目录" },
            ["depth"] = new JsonObject { ["type"] = "integer", ["description"] = "递归深度（默认 8，最大 30）" },
            ["include_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "包含隐藏目录（默认 true）" },
            ["max_files"] = new JsonObject { ["type"] = "integer", ["description"] = "最多扫描文件数（默认 10000，最大 100000）" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多显示发现数（默认 200，最大 5000）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        await Task.CompletedTask;
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var depth = Math.Clamp(ToolArgs.GetInt(args, "depth", 8), 0, 30);
        var includeHidden = ToolArgs.GetBool(args, "include_hidden", true);
        var maxFiles = Math.Clamp(ToolArgs.GetInt(args, "max_files", 10_000), 1, 100_000);
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 200), 1, 5_000);
        var findings = new List<(string Path, string Category, long Bytes)>();
        var scanned = 0;
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, depth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            if (scanned++ >= maxFiles || (!includeHidden && SkipDirs.IsHiddenPath(file, root)))
                continue;
            var category = Categorize(Path.GetFileName(file));
            if (category is null)
                continue;
            long size;
            try { size = new FileInfo(file).Length; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            findings.Add((Path.GetRelativePath(root, file).Replace('\\', '/'), category, size));
            if (findings.Count >= maxResults)
                break;
        }
        var output = new StringBuilder();
        output.AppendLine($"敏感文件清单: {scanned:N0} 个文件中发现 {findings.Count:N0} 个疑似敏感文件（未读取内容）");
        foreach (var finding in findings.Take(maxResults))
            output.AppendLine($"  {finding.Path} [{finding.Category}]，{finding.Bytes:N0} 字节");
        if (findings.Count > maxResults)
            output.AppendLine($"…（另有 {findings.Count - maxResults} 个发现未显示）");
        return output.ToString().TrimEnd();
    }

    private static string? Categorize(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower == ".env" || lower.StartsWith(".env.", StringComparison.Ordinal))
            return "环境配置";
        if (lower.EndsWith(".pem", StringComparison.Ordinal) || lower.EndsWith(".key", StringComparison.Ordinal)
            || lower.EndsWith(".p12", StringComparison.Ordinal) || lower.EndsWith(".pfx", StringComparison.Ordinal)
            || lower.EndsWith(".keystore", StringComparison.Ordinal) || lower is "id_rsa" or "id_dsa" or "id_ecdsa" or "id_ed25519")
            return "私钥/证书";
        if (lower.Contains("credential") || lower.Contains("secret") || lower.StartsWith("password", StringComparison.Ordinal))
            return "凭据文件";
        if (lower is ".npmrc" or ".pypirc" or ".netrc" or ".dockerconfigjson")
            return "包管理器认证";
        if (lower.StartsWith("appsettings.", StringComparison.Ordinal) && lower.EndsWith(".json", StringComparison.Ordinal))
            return "应用配置";
        return null;
    }
}
