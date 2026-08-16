using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>按 glob 模式查找文件（支持 **、*、?）。</summary>
public sealed class GlobTool : ITool
{
    public string Name => "glob";
    public string Description => "按 glob 模式查找文件，如 src/**/*.cs 或 *.sln。返回相对路径列表。";
    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["pattern"] = new JsonObject { ["type"] = "string", ["description"] = "glob 模式，支持 ** 跨目录、*、?" },
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "搜索起点目录，默认工作区根目录" },
        },
        ["required"] = new JsonArray("pattern"),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        var pattern = ToolArgs.GetString(args, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
            throw new ToolException("缺少必填参数 pattern");

        var basePath = ToolArgs.GetString(args, "path");
        var start = ctx.Workspace.Resolve(string.IsNullOrWhiteSpace(basePath) ? null : basePath);
        if (!Directory.Exists(start))
            throw new ToolException($"目录不存在: {basePath}");

        var re = Glob.ToRegex(pattern);
        var results = new List<string>();
        var scanned = 0;

        foreach (var file in SkipDirs.EnumerateFilesPruned(start))
        {
            if (scanned++ > 200_000 || results.Count > 500)
                break;
            var rel = Path.GetRelativePath(start, file).Replace('\\', '/');
            if (re.IsMatch(rel))
                results.Add(rel);
        }

        await Task.Yield();
        if (results.Count == 0)
            return $"(没有匹配 {pattern} 的文件)";
        var shown = string.Join('\n', results.Take(300));
        return shown + (results.Count > 300 ? $"\n…(共 {results.Count} 个，仅显示前 300)" : "");
    }
}

/// <summary>正则搜索文件内容（智能大小写 + 上下文行）。</summary>
public sealed class GrepTool : ITool
{
    public string Name => "grep";
    public string Description => "用正则搜索文件内容。pattern 含大写字母时区分大小写，否则忽略大小写。可用 include/exclude（glob）限定文件范围。返回 文件:行号: 内容。";
    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["pattern"] = new JsonObject { ["type"] = "string", ["description"] = "正则表达式" },
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "搜索的文件或目录，默认工作区根目录" },
            ["context"] = new JsonObject { ["type"] = "integer", ["description"] = "上下文行数（默认 3，最大 10）" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最大结果数（默认 50，最大 500）" },
            ["include"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "仅搜索匹配这些 glob 的文件（如 \"*.cs\"），可用字符串或数组" },
            ["exclude"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "跳过匹配这些 glob 的文件（如 \"**/*.g.cs\"），可用字符串或数组" },
        },
        ["required"] = new JsonArray("pattern"),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        var pattern = ToolArgs.GetString(args, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
            throw new ToolException("缺少必填参数 pattern");

        var target = ToolArgs.GetString(args, "path");
        var context = Math.Clamp(ToolArgs.GetInt(args, "context", 3), 0, 10);
        var max = Math.Clamp(ToolArgs.GetInt(args, "max_results", 50), 1, 500);
        var include = ToolArgs.GetStringList(args, "include");
        var exclude = ToolArgs.GetStringList(args, "exclude");
        var includeRes = include?.Select(Glob.ToRegex).ToList();
        var excludeRes = exclude?.Select(Glob.ToRegex).ToList();

        RegexOptions opts = RegexOptions.Compiled;
        if (pattern == pattern.ToLowerInvariant())
            opts |= RegexOptions.IgnoreCase;

        Regex re;
        try
        {
            re = new Regex(pattern, opts);
        }
        catch (ArgumentException ex)
        {
            throw new ToolException($"正则表达式无效: {ex.Message}");
        }

        var full = ctx.Workspace.Resolve(string.IsNullOrWhiteSpace(target) ? null : target);
        var sb = new StringBuilder();
        var hits = 0;

        void ScanFile(string path)
        {
            if (hits >= max)
                return;
            try
            {
                var rel = ctx.Workspace.ToRelative(path).Replace('\\', '/');
                // include/exclude 仅作用于目录扫描（单文件目标视为用户显式指定）
                if (includeRes is not null && includeRes.Count > 0 && !includeRes.Any(r => r.IsMatch(rel)))
                    return;
                if (excludeRes is not null && excludeRes.Any(r => r.IsMatch(rel)))
                    return;

                var fi = new FileInfo(path);
                if (fi.Length > 2_000_000)
                    return;
                var text = File.ReadAllText(path);
                if (SkipDirs.LooksBinary(text))
                    return;
                var lines = text.Split('\n');
                for (int i = 0; i < lines.Length && hits < max; i++)
                {
                    var line = lines[i].TrimEnd('\r');
                    if (!re.IsMatch(line))
                        continue;
                    hits++;
                    sb.AppendLine($"{rel}:{i + 1}: {TextUtil.TruncateLine(line, 300)}");
                    for (int c = Math.Max(0, i - context); c <= Math.Min(lines.Length - 1, i + context); c++)
                    {
                        if (c != i)
                            sb.AppendLine($"  {c + 1}| {TextUtil.TruncateLine(lines[c].TrimEnd('\r'), 300)}");
                    }
                    sb.AppendLine();
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        await Task.Yield();
        if (File.Exists(full))
        {
            ScanFile(full);
        }
        else if (Directory.Exists(full))
        {
            foreach (var file in SkipDirs.EnumerateFilesPruned(full))
            {
                if (hits >= max)
                    break;
                ScanFile(file);
            }
        }
        else
        {
            throw new ToolException($"路径不存在: {target}");
        }

        if (hits == 0)
            return $"(无匹配: {pattern})";
        return $"匹配 {hits} 处:\n" + sb.ToString().TrimEnd();
    }
}
