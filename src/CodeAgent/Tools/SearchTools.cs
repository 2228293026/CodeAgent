using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodeAgent.Tools;

/// <summary>按 glob 模式查找文件（支持 **、*、?）。</summary>
public sealed class GlobTool : ITool
{
    public string Name => "glob";
    public string Description => "按 glob 模式查找文件，如 src/**/*.cs 或 *.sln。pattern 可用字符串或数组（任一匹配即命中）。返回相对路径列表。";
    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["pattern"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "glob 模式，支持 ** 跨目录、*、?；可用字符串或字符串数组" },
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "搜索起点目录，默认工作区根目录" },
            ["ignore"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "排除匹配这些 glob 的结果（如 \"*.min.js\"、\"secret*\"），可用字符串或字符串数组" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最多返回的匹配文件数（默认 500，最大 5000）" },
        },
        ["required"] = new JsonArray("pattern"),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        var patterns = ToolArgs.GetStringList(args, "pattern");
        if (patterns is null || patterns.Count == 0)
            throw new ToolException("缺少必填参数 pattern");

        var basePath = ToolArgs.GetString(args, "path");
        var start = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(basePath) ? null : basePath);
        if (!Directory.Exists(start))
            throw new ToolException($"目录不存在: {basePath}");

        var regexes = patterns.Select(Glob.ToRegex).ToList();
        // ignore 模式与 grep 的 include/exclude 同口径：裸 glob 视为任意深度匹配
        static string AsIgnorePattern(string p) => p.Contains('/') || p.Contains('\\') ? p : "**/" + p;
        var ignore = ToolArgs.GetStringList(args, "ignore");
        var ignoreRes = ignore?.Select(p => Glob.ToRegex(AsIgnorePattern(p))).ToList();
        var maxResults = Math.Clamp(ToolArgs.GetInt(args, "max_results", 500), 1, 5000);
        var results = new List<string>();
        var scanned = 0;

        var capped = false;
        foreach (var file in SkipDirs.EnumerateFilesPruned(start))
        {
            if (scanned++ > 200_000 || results.Count >= maxResults)
            {
                capped = true; // 提前停止：结果可能不完整（曾静默截断，总数显示还误导）
                break;
            }
            var rel = Path.GetRelativePath(start, file).Replace('\\', '/');
            // 命中 pattern 且未被 ignore 排除才保留
            if (regexes.Any(r => r.IsMatch(rel)) && (ignoreRes is null || !ignoreRes.Any(r => r.IsMatch(rel))))
                results.Add(rel);
        }

        await Task.Yield();
        if (results.Count == 0)
            return capped
                ? $"(扫描超过 200,000 个文件后中止，未找到匹配 {string.Join(", ", patterns)} 的文件——工作区过大，请缩小 path 或用更精确的 pattern)"
                : $"(没有匹配 {string.Join(", ", patterns)} 的文件)";
        results.Sort(StringComparer.Ordinal); // 确定性输出：枚举顺序跨平台不定
        var shown = string.Join('\n', results.Take(300));
        return shown + (results.Count > 300 ? $"\n…(共 {results.Count} 个，仅显示前 300{(capped ? "，已达上限，可能不完整" : "")})" : "");
    }
}

/// <summary>正则搜索文件内容（智能大小写 + 上下文行）。</summary>
public sealed class GrepTool : ITool
{
    public string Name => "grep";
    public string Description => "用正则搜索文件内容。智能大小写：pattern 全小写时忽略大小写，含大写则精确匹配；case_sensitive=true 可强制区分。可用 include/exclude（glob）限定文件范围。files_only=true 只返回匹配的文件名。multiline=true 允许跨行匹配（\n 当普通字符，适合匹配多行块）。返回 文件:行号: 内容。";
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
            ["files_only"] = new JsonObject { ["type"] = "boolean", ["description"] = "只返回匹配的文件路径列表（不返回行内容），默认 false" },
            ["count_only"] = new JsonObject { ["type"] = "boolean", ["description"] = "只输出每个文件的匹配行数（文件:行数，类似 ripgrep -c），默认 false；multiline 时按命中次数计" },
            ["case_sensitive"] = new JsonObject { ["type"] = "boolean", ["description"] = "强制区分大小写（默认智能大小写：pattern 全小写时忽略大小写）" },
            ["multiline"] = new JsonObject { ["type"] = "boolean", ["description"] = "跨行匹配（\n 视为普通字符参与匹配，适合多行块如 JSON/HTML 片段），默认 false" },
            ["invert"] = new JsonObject { ["type"] = "boolean", ["description"] = "反转匹配（类似 rg -v）：输出不匹配 pattern 的行，默认 false；不支持 multiline 模式" },
            ["word"] = new JsonObject { ["type"] = "boolean", ["description"] = "整词匹配（类似 rg -w）：pattern 两侧加单词边界 \\b，避免命中更长单词的子串（如搜 cat 不命中 category），默认 false" },
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
        var filesOnly = ToolArgs.GetBool(args, "files_only", false);
        var countOnly = ToolArgs.GetBool(args, "count_only", false);
        var include = ToolArgs.GetStringList(args, "include");
        var exclude = ToolArgs.GetStringList(args, "exclude");
        // 无分隔符的 include/exclude（如 "*.cs"）按「任意深度」匹配（与 ripgrep --glob 语义一致）：
        // 裸 glob 只会匹配根目录文件，子目录里的 src/a.cs 会被漏掉；含分隔符的模式（src/**/*.cs）保持原样
        static string AsFilePattern(string p) => p.Contains('/') || p.Contains('\\') ? p : "**/" + p;
        var includeRes = include?.Select(p => Glob.ToRegex(AsFilePattern(p))).ToList();
        var excludeRes = exclude?.Select(p => Glob.ToRegex(AsFilePattern(p))).ToList();

        RegexOptions opts = RegexOptions.Compiled;
        // 智能大小写（ripgrep 风格）：全小写 pattern 默认忽略大小写；case_sensitive=true 强制精确匹配
        var caseSensitive = ToolArgs.GetBool(args, "case_sensitive", false);
        if (!caseSensitive && pattern == pattern.ToLowerInvariant())
            opts |= RegexOptions.IgnoreCase;
        // 跨行匹配：让 `.` 与 `.*` 能匹配换行符（否则 multiline 只改 ^/$ 语义，'.*' 仍被 \n 截断）
        var multiline = ToolArgs.GetBool(args, "multiline", false);
        if (multiline)
            opts |= RegexOptions.Singleline;
        // 整词匹配（rg -w）：两侧加单词边界，避免命中更长单词的子串
        var word = ToolArgs.GetBool(args, "word", false);
        var effectivePattern = word ? $"\\b{pattern}\\b" : pattern;

        Regex re;
        try
        {
            re = new Regex(effectivePattern, opts);
        }
        catch (ArgumentException ex)
        {
            throw new ToolException($"正则表达式无效: {ex.Message}");
        }

        var full = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(target) ? null : target);
        var sb = new StringBuilder();
        var hits = 0;
        var invert = ToolArgs.GetBool(args, "invert", false);
        if (multiline && invert)
            throw new ToolException("invert 不支持 multiline 模式（跨行反转无意义），请关闭 multiline 或 invert。");
        // 匹配判定（invert 时取反，类似 rg -v）；集中一处，files_only/count_only/普通模式共用
        bool Hit(string s) => invert ? !re.IsMatch(s) : re.IsMatch(s);

        void ScanFile(string path)
        {
            if (hits >= max)
                return;
            try
            {
                var rel = ctx.Workspace.ToRelative(path).Replace('\\', '/');
                // include/exclude 对每个扫描到的文件生效（含单文件目标）
                if (includeRes is not null && includeRes.Count > 0 && !includeRes.Any(r => r.IsMatch(rel)))
                    return;
                if (excludeRes is not null && excludeRes.Any(r => r.IsMatch(rel)))
                    return;

                var fi = new FileInfo(path);
                if (fi.Length > 2_000_000)
                    return;
                var text = TextUtil.ReadTextSmart(path); // GBK/ANSI 兜底，避免中文文件乱码
                if (SkipDirs.LooksBinary(text))
                    return;

                if (filesOnly)
                {
                    // 只统计匹配文件数：每文件最多计一次。
                    // 正常模式按整文件是否含匹配行判断；invert 时按「是否含任一非匹配行」判断（逐行），
                    // 否则含匹配行的文件（如 DROP\nkeep）会被整文件命中误判为「无匹配」
                    var fileHits = invert
                        ? text.Split('\n').Any(l => !re.IsMatch(l.TrimEnd('\r')))
                        : re.IsMatch(text);
                    if (fileHits)
                    {
                        hits++;
                        sb.AppendLine(rel);
                    }
                    return;
                }

                if (countOnly)
                {
                    // 计数模式（rg -c 风格）：输出 文件:匹配行数；multiline 时按命中次数计。
                    // invert 时统计非匹配行数。hits 以文件为粒度递增，max_results 限制列出的文件数
                    var n = multiline
                        ? re.Matches(text).Count(m => m.Length > 0)
                        : text.Split('\n').Count(l => Hit(l.TrimEnd('\r')));
                    if (n > 0)
                    {
                        hits++;
                        sb.AppendLine($"{rel}:{n}");
                    }
                    return;
                }

                if (multiline)
                {
                    // 跨行模式：\n 作为普通字符参与匹配（多行块：JSON/HTML 片段等）。
                    // 行号按命中起点计算；跨多行的命中折叠显示（前 3 行 + 总行数）
                    foreach (System.Text.RegularExpressions.Match m in re.Matches(text))
                    {
                        if (hits >= max)
                            break;
                        if (m.Length == 0)
                            continue; // 零宽命中不展示（只产生噪音）
                        hits++;
                        var startLine = 1 + CountNewlines(text, 0, m.Index);
                        var endLine = 1 + CountNewlines(text, 0, m.Index + m.Length);
                        var spanLines = m.Value.Replace("\r", "").Split('\n');
                        sb.AppendLine($"{rel}:{startLine}: {TextUtil.TruncateLine(spanLines[0], 300)}");
                        for (int li = 1; li < Math.Min(spanLines.Length, 4); li++)
                            sb.AppendLine($"  +{li}| {TextUtil.TruncateLine(spanLines[li], 300)}");
                        if (spanLines.Length > 4)
                            sb.AppendLine($"  …(命中跨 {startLine}-{endLine} 共 {spanLines.Length} 行)");
                        sb.AppendLine();
                    }
                    return;
                }

                var lines = text.Split('\n');
                var printedUntil = -1; // 已打印过的上下文行（避免邻近匹配的共享行重复输出）
                for (int i = 0; i < lines.Length && hits < max; i++)
                {
                    var line = lines[i].TrimEnd('\r');
                    if (!Hit(line))
                        continue;
                    hits++;
                    sb.AppendLine($"{rel}:{i + 1}: {TextUtil.TruncateLine(line, 300)}");
                    for (int c = Math.Max(0, i - context); c <= Math.Min(lines.Length - 1, i + context); c++)
                    {
                        // 跳过已作为上个匹配上下文输出过的行，避免重复
                        if (c != i && c > printedUntil)
                            sb.AppendLine($"  {c + 1}| {TextUtil.TruncateLine(lines[c].TrimEnd('\r'), 300)}");
                    }
                    printedUntil = Math.Max(printedUntil, Math.Min(lines.Length - 1, i + context));
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
            // 确定性输出：先收集再排序（枚举顺序跨平台不定），与 glob 保持一致
            var files = SkipDirs.EnumerateFilesPruned(full).ToList();
            files.Sort(StringComparer.Ordinal);
            foreach (var file in files)
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
        var notice = hits >= max ? $"\n…(已达 max_results={max} 上限，可能还有更多匹配；可用 max_results 参数提高)" : "";
        return filesOnly || countOnly
            ? $"匹配 {hits} 个文件:\n" + sb.ToString().TrimEnd() + notice
            : $"匹配 {hits} 处:\n" + sb.ToString().TrimEnd() + notice;
    }

    /// <summary>统计 text[start,end) 内的换行数（跨行匹配的行号计算）。</summary>
    private static int CountNewlines(string text, int start, int end)
    {
        int n = 0;
        for (int i = start; i < end; i++)
            if (text[i] == '\n')
                n++;
        return n;
    }
}
