using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查写死的凭据：源码里的 API Key / Token / 连接串口令，
/// 以及把密钥拼进日志或异常消息（异常会连同消息一起进会话日志）。</summary>
public sealed class HardcodedSecretReportTool : ITool
{
    public string Name => "hardcoded_secret_report";
    public string Description =>
        "只读检查源码里的硬编码凭据：API Key/Token/密码字面量、含口令的连接串、以及把密钥写进日志或异常消息。";

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

    /// <summary>明显是占位符、不是真凭据的值——报了只会淹没真问题。
    /// 短于 8 字符的几乎都是 <c>xxx</c>/<c>test</c>/<c>changeme</c>。</summary>
    internal static bool IsPlaceholder(string value)
    {
        if (value.Length < 8)
            return true;
        var v = value.ToLowerInvariant();
        foreach (var marker in new[] { "your", "example", "placeholder", "changeme", "change_me", "dummy", "xxxx", "todo", "fake", "sample", "redacted", "notarealkey" })
        {
            if (v.Contains(marker, StringComparison.Ordinal))
                return true;
        }
        return value.All(c => c == '*' || c == 'x' || c == 'X' || c == '.');
    }

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var requestedPath = ToolArgs.GetString(args, "path");
        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath);
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {requestedPath}");
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 30), 1, 300);
        var maxDepth = Math.Clamp(ToolArgs.GetInt(args, "max_depth", 10), 1, 32);
        var literals = new List<string>();
        var connectionStrings = new List<string>();
        var leaked = new List<string>();
        var files = 0;
        var assign = new Regex(@"(?:api[_-]?key|apikey|token|secret|password|passwd|pwd|access[_-]?key|private[_-]?key)\b\s*[:=]\s*""(?<v>[^""]*)""", RegexOptions.IgnoreCase);
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(file);
            if (!ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals(".json", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals(".yml", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals(".xml", StringComparison.OrdinalIgnoreCase)
                && !ext.Equals(".env", StringComparison.OrdinalIgnoreCase))
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
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.Length == 0)
                    continue;
                var lineNo = i + 1;
                // 注释掉的示例行不算——文档里写 `api_key: your-key-here` 是正确做法
                foreach (Match m in assign.Matches(trimmed))
                {
                    var v = m.Groups["v"].Value;
                    if (v.Length == 0 || IsPlaceholder(v))
                        continue;
                    literals.Add($"{relative}:{lineNo} 疑似硬编码的 {m.Value.Split(':')[0].Split('=')[0].Trim()}（{Mask(v)}）");
                }
                var cs = Regex.Match(trimmed, @"(?i)(?:Server|Data Source|Host)\s*=\s*[^;]+;.*?(?:Password|Pwd)\s*=\s*(?<v>[^;""]+)", RegexOptions.None);
                if (cs.Success && !IsPlaceholder(cs.Groups["v"].Value))
                    connectionStrings.Add($"{relative}:{lineNo} 连接串里带口令（{Mask(cs.Groups["v"].Value)}）——应从环境变量读");
                // 把密钥写进日志/异常
                // 关键字与括号之间**允许有构造器名**：`throw new Exception($"key {apiKey}")` 里
                // throw 后面不是紧跟 `(`，早先的 `\s*\(` 因此匹配不到，整条规则静默空跑。
                var leak = Regex.Match(trimmed, @"(?i)\b(?:Log|Write(?:Line)?|throw|Debug|Console\.Write\w*)\b[^()]*\([^)]*\b(apiKey|token|secret|password|credential)\b");
                if (leak.Success)
                    leaked.Add($"{relative}:{lineNo} 密钥出现在日志/异常消息里（异常消息会连同会话日志一起落盘）");
            }
        }
        if (files == 0)
            return "硬编码凭据报告: 未找到可检查的文件";
        var output = new StringBuilder();
        output.AppendLine($"硬编码凭据报告: {files} 个文件");
        Report(output, "疑似硬编码的凭据", literals, maxResults);
        Report(output, "连接串里带口令", connectionStrings, maxResults);
        Report(output, "密钥进了日志/异常", leaked, maxResults);
        if (literals.Count == 0 && connectionStrings.Count == 0 && leaked.Count == 0)
            output.AppendLine("未发现硬编码凭据");
        return output.ToString().TrimEnd();
    }

    /// <summary>只回显头尾各 3 位——报告本身会进会话日志，不能把完整密钥再抄一遍。</summary>
    internal static string Mask(string v) =>
        v.Length <= 8 ? "（过短，未回显）" : $"{v[..3]}…{v[^3..]}（{v.Length} 字符）";

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
