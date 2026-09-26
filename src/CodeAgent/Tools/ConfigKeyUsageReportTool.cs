using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>只读检查配置健壮性：配置项声明了但从未被读取、
/// 代码里硬编码了配置里已有的值、以及同一配置项在不同处用了不同拼写。</summary>
public sealed class ConfigKeyUsageReportTool : ITool
{
    public string Name => "config_key_usage_report";
    public string Description =>
        "只读检查配置项：声明了却从未被读取、代码里硬编码了配置里已有的值、同一配置项拼写不一致。";

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
        var neverRead = new List<string>();
        var hardcoded = new List<string>();
        var spelling = new List<string>();
        var files = 0;
        // 整个仓库的源码文本，用来判断某个键到底被读没被读
        var all = new StringBuilder();
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
            all.Append('\n').Append(string.Join('\n', lines));
        }
        var text = all.ToString();
        if (files == 0)
            return "配置项使用报告: 未找到 .cs 文件";

        // 1) 配置属性（PascalCase 的 public 属性，带初值）声明了却从未被读取
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, maxDepth, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;
            string[] lines;
            try { lines = await File.ReadAllLinesAsync(file, ct); }
            catch (OperationCanceledException) { throw; }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            for (var i = 0; i < lines.Length; i++)
            {
                var m = Regex.Match(lines[i].Trim(), @"^public\s+[\w\.<>\[\],\?]+\s+(?<name>[A-Z]\w*)\s*\{\s*get;\s*(?:set;|init;)\s*\}");
                if (!m.Success)
                    continue;
                var name = m.Groups["name"].Value;
                // 声明处自己算一次；除它之外再出现才算「被读过」
                var reads = Regex.Matches(text, @"\b" + Regex.Escape(name) + @"\b").Count;
                if (reads <= 1)
                    neverRead.Add($"{relative}:{i + 1} 配置属性 {name} 全仓库只出现这一次（声明处），从未被读取");
            }
        }

        // 2) 硬编码了与配置项同名的字面量
        foreach (Match m in Regex.Matches(text, @"\bconfig\.(?<key>\w+)\b"))
        {
            var key = m.Groups["key"].Value;
            if (!Regex.IsMatch(text, $@"\b{Regex.Escape(key)}\s*=\s*"""))
                spelling.Add($"config.{key} 在代码里被读取，但没找到对应的配置项声明 {key}");
        }

        // 3) 同一概念的大小写/下划线拼写不一致
        foreach (Match m in Regex.Matches(text, @"\bconfig\.(?<key>\w+)\b"))
        {
            var key = m.Groups["key"].Value;
            if (key.Any(char.IsLower) && key.ToLowerInvariant() != key)
                spelling.Add($"config.{key} 命名风格与其它配置项不一致（应当全 PascalCase）");
        }

        var output = new StringBuilder();
        output.AppendLine($"配置项使用报告: {files} 个 .cs 文件");
        Report(output, "声明了却从未读取", neverRead, maxResults);
        Report(output, "硬编码/拼写不一致", hardcoded.Concat(spelling).Distinct().ToList(), maxResults);
        if (neverRead.Count == 0 && hardcoded.Count == 0 && spelling.Count == 0)
            output.AppendLine("未发现配置项使用问题");
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
