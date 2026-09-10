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
            ["depth"] = new JsonObject { ["type"] = "integer", ["description"] = "递归深度限制（0=仅根目录，默认无限制）" },
            ["sort_by"] = new JsonObject { ["type"] = "string", ["description"] = "排序方式：name（默认，按路径字母序）、size（按文件大小降序）、modified（按修改时间降序）" },
            ["show_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示隐藏文件/目录（默认 false；Unix 以 . 开头，Windows 带 Hidden/System 属性）" },
            ["include_ignored"] = new JsonObject { ["type"] = "boolean", ["description"] = "搜索被跳过目录内的文件（如 .git、node_modules、bin、obj 等，默认 false；需要时设为 true 可搜索这些目录）" },
            ["show_modified"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示文件修改时间（默认 false；设为 true 时在路径后附加修改时间，如 file.txt (2025-01-15 10:30)）" },
            ["show_size"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示文件大小（默认 false；设为 true 时在路径后附加文件大小，如 file.txt (1.5 KB)）" },
            ["show_byte_count"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示字节数（默认 false；设为 true 时在路径后附加精确字节数，如 file.txt (1536 bytes)）" },
            ["show_hash"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示 SHA256 哈希（默认 false；设为 true 时在路径后附加哈希值，如 file.txt (sha256:abc123...)）" },
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
        var depth = ToolArgs.GetInt(args, "depth", -1);
        var sortBy = ToolArgs.GetString(args, "sort_by");
        var showHidden = ToolArgs.GetBool(args, "show_hidden", false);
        var includeIgnored = ToolArgs.GetBool(args, "include_ignored", false);
        var showModified = ToolArgs.GetBool(args, "show_modified", false);
        var showSize = ToolArgs.GetBool(args, "show_size", false);
        var showByteCount = ToolArgs.GetBool(args, "show_byte_count", false);
        var showHash = ToolArgs.GetBool(args, "show_hash", false);
        var results = new List<string>();
        var scanned = 0;

        var capped = false;
        foreach (var file in SkipDirs.EnumerateFilesPruned(start, depth >= 0 ? depth : int.MaxValue, includeIgnored))
        {
            if (scanned++ > 200_000 || results.Count >= maxResults)
            {
                capped = true; // 提前停止：结果可能不完整（曾静默截断，总数显示还误导）
                break;
            }
            var rel = Path.GetRelativePath(start, file).Replace('\\', '/');
            if (!showHidden && SkipDirs.IsHidden(file))
                continue; // 跳过隐藏文件/目录
            // 命中 pattern 且未被 ignore 排除才保留
            if (regexes.Any(r => r.IsMatch(rel)) && (ignoreRes is null || !ignoreRes.Any(r => r.IsMatch(rel))))
                results.Add(rel);
        }

        await Task.Yield();
        if (results.Count == 0)
            return capped
                ? $"(扫描超过 200,000 个文件后中止，未找到匹配 {string.Join(", ", patterns)} 的文件——工作区过大，请缩小 path 或用更精确的 pattern)"
                : $"(没有匹配 {string.Join(", ", patterns)} 的文件)";
        if (string.IsNullOrEmpty(sortBy) || sortBy == "name")
            results.Sort(StringComparer.Ordinal); // 确定性输出：枚举顺序跨平台不定
        else if (sortBy == "size" || sortBy == "modified")
        {
            var fullPaths = results.Select(r => Path.Combine(start, r.Replace('/', Path.DirectorySeparatorChar))).ToList();
            var tuples = results.Select((r, i) => (r, i)).ToList();
            if (sortBy == "size")
                tuples.Sort((a, b) => new FileInfo(fullPaths[b.i]).Length.CompareTo(new FileInfo(fullPaths[a.i]).Length));
            else
                tuples.Sort((a, b) => File.GetLastWriteTimeUtc(fullPaths[b.i]).CompareTo(File.GetLastWriteTimeUtc(fullPaths[a.i])));
            results = tuples.Select(t => t.r).ToList();
        }
        var displayLimit = Math.Min(maxResults, 500); // 单次输出上限 500，防止结果过多撑爆上下文
        var shown = showModified || showSize || showByteCount || showHash
            ? string.Join('\n', results.Take(displayLimit).Select(r =>
            {
                var full = Path.Combine(start, r.Replace('/', Path.DirectorySeparatorChar));
                var parts = new List<string>();
                if (showSize && File.Exists(full))
                {
                    var length = new FileInfo(full).Length;
                    parts.Add(length < 1024 ? $"{length} B" : length < 1024 * 1024 ? $"{length / 1024.0:F1} KB" : $"{length / 1024.0 / 1024.0:F1} MB");
                }
                if (showByteCount && File.Exists(full))
                {
                    var length = new FileInfo(full).Length;
                    parts.Add($"{length:N0} bytes");
                }
                if (showModified && File.Exists(full))
                {
                    var modified = File.GetLastWriteTime(full).ToString("yyyy-MM-dd HH:mm");
                    parts.Add(modified);
                }
                if (showHash && File.Exists(full))
                {
                    using var sha = System.Security.Cryptography.SHA256.Create();
                    var hash = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(full)));
                    parts.Add($"sha256:{hash[..8]}..."); // 只显示前 8 位，避免输出过长
                }
                return parts.Count > 0 ? $"{r} ({string.Join(", ", parts)})" : r;
            }))
            : string.Join('\n', results.Take(displayLimit));
        var truncated = results.Count > displayLimit || capped;
        return shown + (truncated ? $"\n…(共 {results.Count} 个，仅显示前 {displayLimit}{(capped ? "，已达上限，可能不完整" : "")})" : "");
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
            ["context"] = new JsonObject { ["type"] = "integer", ["description"] = "上下文行数（默认 3，最大 10；被 context_before/context_after 覆盖时忽略）" },
            ["context_before"] = new JsonObject { ["type"] = "integer", ["description"] = "匹配行前的上下文行数（默认 0，最大 10；与 context 互斥）" },
            ["context_after"] = new JsonObject { ["type"] = "integer", ["description"] = "匹配行后的上下文行数（默认 0，最大 10；与 context 互斥）" },
            ["max_results"] = new JsonObject { ["type"] = "integer", ["description"] = "最大结果数（默认 50，最大 500）" },
            ["depth"] = new JsonObject { ["type"] = "integer", ["description"] = "递归深度限制（0=仅目标文件/目录本身，默认无限制）" },
            ["max_line_length"] = new JsonObject { ["type"] = "integer", ["description"] = "单行截断阈值（0=不截断，默认 2000，最大 50000；匹配内容过长时压缩输出）" },
            ["include"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "仅搜索匹配这些 glob 的文件（如 \"*.cs\"），可用字符串或数组" },
            ["exclude"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "跳过匹配这些 glob 的文件（如 \"**/*.g.cs\"），可用字符串或数组" },
            ["files_only"] = new JsonObject { ["type"] = "boolean", ["description"] = "只返回匹配的文件路径列表（不返回行内容），默认 false" },
            ["count_only"] = new JsonObject { ["type"] = "boolean", ["description"] = "只输出每个文件的匹配行数（文件:行数，类似 ripgrep -c），默认 false；multiline 时按命中次数计" },
            ["case_sensitive"] = new JsonObject { ["type"] = "boolean", ["description"] = "强制区分大小写（默认智能大小写：pattern 全小写时忽略大小写）" },
            ["multiline"] = new JsonObject { ["type"] = "boolean", ["description"] = "跨行匹配（\n 视为普通字符参与匹配，适合多行块如 JSON/HTML 片段），默认 false" },
            ["invert"] = new JsonObject { ["type"] = "boolean", ["description"] = "反转匹配（类似 rg -v）：输出不匹配 pattern 的行，默认 false；不支持 multiline 模式" },
            ["word"] = new JsonObject { ["type"] = "boolean", ["description"] = "整词匹配（类似 rg -w）：pattern 两侧加单词边界 \\b，避免命中更长单词的子串（如搜 cat 不命中 category），默认 false" },
            ["binary_files"] = new JsonObject { ["type"] = "string", ["description"] = "二进制文件处理：skip（默认，跳过）、text（当作文本搜索）、without-match（视为不匹配）" },
            ["show_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "搜索隐藏文件/目录（默认 false；Unix 以 . 开头，Windows 带 Hidden/System 属性）" },
            ["line_number"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示行号（默认 true；设为 false 时输出格式为 file: content，去掉行号前缀，方便模型直接提取匹配文本）" },
            ["output_mode"] = new JsonObject { ["type"] = "string", ["description"] = "输出模式：text（默认，file:line: content）、content（仅匹配文本，无文件路径和行号）、content_without_filename（行号: 内容，无文件路径）、content_without_line_number（文件: 内容，无行号）" },
            ["include_ignored"] = new JsonObject { ["type"] = "boolean", ["description"] = "搜索被跳过目录内的文件（如 .git、node_modules、bin、obj 等，默认 false；需要时设为 true 可搜索这些目录）" },
            ["heading"] = new JsonObject { ["type"] = "boolean", ["description"] = "文件路径单独成行（默认 true；类似 rg --heading，每个文件的匹配前先输出文件路径，方便区分不同文件的匹配）" },
            ["max_matches_per_file"] = new JsonObject { ["type"] = "integer", ["description"] = "每个文件最多显示的匹配数（默认 0=不限制；设为正数可防止单个文件匹配过多撑爆输出）" },
            ["literal"] = new JsonObject { ["type"] = "boolean", ["description"] = "字面量搜索（默认 false；设为 true 时 pattern 被视为普通字符串而非正则表达式，自动转义特殊字符）" },
            ["stats"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示搜索统计（默认 false；设为 true 时在输出末尾附加统计信息，如扫描文件数、匹配数、耗时）" },
            ["show_total_matches"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示总匹配数（默认 false；设为 true 时在输出末尾附加总匹配数，而非仅显示匹配行数）" },
            ["show_modified"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示文件修改时间（默认 false；files_only=true 时在文件路径后附加修改时间，如 file.txt (2025-01-15 10:30)）" },
            ["show_encoding"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示文件编码（默认 false；files_only=true 时在文件路径后附加编码信息，如 file.txt (UTF-8 BOM)）" },
            ["show_size"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示文件大小（默认 false；files_only=true 时在文件路径后附加大小，如 file.txt (1.2 KB)）" },
            ["show_word_count"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示单词数（默认 false；files_only=true 时在文件路径后附加单词数，如 file.txt [42 words]）" },
        },
        ["required"] = new JsonArray("pattern"),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        var pattern = ToolArgs.GetString(args, "pattern");
        if (string.IsNullOrWhiteSpace(pattern))
            throw new ToolException("缺少必填参数 pattern");

        var target = ToolArgs.GetString(args, "path");
        var contextBefore = ToolArgs.GetInt(args, "context_before", -1);
        if (contextBefore > 10) contextBefore = 10;
        var contextAfter = ToolArgs.GetInt(args, "context_after", -1);
        if (contextAfter > 10) contextAfter = 10;
        var context = Math.Clamp(ToolArgs.GetInt(args, "context", 3), 0, 10);
        var max = Math.Clamp(ToolArgs.GetInt(args, "max_results", 50), 1, 500);
        var useAsymmetric = contextBefore >= 0 || contextAfter >= 0;
        var before = useAsymmetric ? (contextBefore >= 0 ? contextBefore : context) : context;
        var after = useAsymmetric ? (contextAfter >= 0 ? contextAfter : context) : context;
        var depth = ToolArgs.GetInt(args, "depth", -1);
        var maxLineLength = Math.Clamp(ToolArgs.GetInt(args, "max_line_length", 2000), 0, 50000);
        var truncateLine = maxLineLength == 0 ? (Func<string, string>)(s => s) : s => TextUtil.TruncateLine(s, maxLineLength);
        var binaryFiles = ToolArgs.GetString(args, "binary_files");
        var skipBinary = string.IsNullOrEmpty(binaryFiles) || binaryFiles == "skip";
        var showHidden = ToolArgs.GetBool(args, "show_hidden", false);
        var includeIgnored = ToolArgs.GetBool(args, "include_ignored", false);
        var heading = ToolArgs.GetBool(args, "heading", false);
        var maxMatchesPerFile = Math.Clamp(ToolArgs.GetInt(args, "max_matches_per_file", 0), 0, 10000);
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
        var showLineNumber = ToolArgs.GetBool(args, "line_number", true);
        var outputMode = ToolArgs.GetString(args, "output_mode") ?? "text";
        if (multiline)
            opts |= RegexOptions.Singleline;
        // 整词匹配（rg -w）：两侧加单词边界，避免命中更长单词的子串
        var word = ToolArgs.GetBool(args, "word", false);
        var literal = ToolArgs.GetBool(args, "literal", false);
        var effectivePattern = literal ? System.Text.RegularExpressions.Regex.Escape(pattern) : (word ? $"\\b(?:{pattern})\\b" : pattern);

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
        int totalMatches = 0;
        var invert = ToolArgs.GetBool(args, "invert", false);
        var showStats = ToolArgs.GetBool(args, "stats", false);
        var showTotalMatches = ToolArgs.GetBool(args, "show_total_matches", false);
        var showModified = ToolArgs.GetBool(args, "show_modified", false);
        var showEncoding = ToolArgs.GetBool(args, "show_encoding", false);
        var showSize = ToolArgs.GetBool(args, "show_size", false);
        var showWordCount = ToolArgs.GetBool(args, "show_word_count", false);
        int filesScanned = 0;
        int filesSkipped = 0;
        if (multiline && invert)
            throw new ToolException("invert 不支持 multiline 模式（跨行反转无意义），请关闭 multiline 或 invert。");
        // 匹配判定（invert 时取反，类似 rg -v）；集中一处，files_only/count_only/普通模式共用
        bool Hit(string s) => invert ? !re.IsMatch(s) : re.IsMatch(s);

        void ScanFile(string path, int fileMaxMatches = 0)
        {
            if (hits >= max)
                return;
            var fileMatchCount = 0;
            try
            {
                var rel = ctx.Workspace.ToRelative(path).Replace('\\', '/');
                // include/exclude 对每个扫描到的文件生效（含单文件目标）
                if (includeRes is not null && includeRes.Count > 0 && !includeRes.Any(r => r.IsMatch(rel)))
                {
                    filesScanned++;
                    filesSkipped++;
                    return;
                }
                if (excludeRes is not null && excludeRes.Any(r => r.IsMatch(rel)))
                {
                    filesScanned++;
                    filesSkipped++;
                    return;
                }

                var fi = new FileInfo(path);
                if (fi.Length > 2_000_000)
                {
                    filesScanned++;
                    filesSkipped++;
                    return;
                }
                var text = TextUtil.ReadTextSmart(path); // GBK/ANSI 兜底，避免中文文件乱码
                if (SkipDirs.LooksBinary(text))
                {
                    filesScanned++;
                    if (skipBinary || binaryFiles == "without-match")
                    {
                        filesSkipped++;
                        return; // skip 或 without-match：二进制文件不处理
                    }
                    // binary_files=text:当作文本继续搜索（可能产生乱码，但用户显式要求）
                }
                filesScanned++;

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
                        var extra = "";
                        if (showModified)
                        {
                            var modified = File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm");
                            extra += $" ({modified})";
                        }
                        if (showEncoding)
                        {
                            var enc = TextUtil.DetectFileEncoding(path) ?? "UTF-8";
                            var encLabel = enc switch { "utf8-bom" => "UTF-8 BOM", "gb18030" => "GBK/GB18030", _ => enc };
                            extra += $" [{encLabel}]";
                        }
                        if (showSize)
                        {
                            var size = new FileInfo(path).Length;
                            var sizeStr = size < 1024 ? $"{size} B" : size < 1024 * 1024 ? $"{size / 1024.0:F1} KB" : $"{size / 1024.0 / 1024.0:F1} MB";
                            extra += $" ({sizeStr})";
                        }
                        if (showWordCount)
                        {
                            var fileText = File.ReadAllText(path);
                            var words = fileText.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
                            extra += $" [{words} words]";
                        }
                        sb.AppendLine(rel + extra);
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
                        totalMatches += n;
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
                        if (fileMaxMatches > 0 && fileMatchCount >= fileMaxMatches)
                            break; // 达到单文件匹配上限
                        if (m.Length == 0)
                            continue; // 零宽命中不展示（只产生噪音）
                        fileMatchCount++;
                        hits++;
                        var startLine = 1 + CountNewlines(text, 0, m.Index);
                        var endLine = 1 + CountNewlines(text, 0, m.Index + m.Length);
                        var spanLines = m.Value.Replace("\r", "").Split('\n');
                        string firstLine;
                        if (outputMode == "content")
                            firstLine = truncateLine(spanLines[0]);
                        else if (outputMode == "content_without_filename")
                            firstLine = showLineNumber ? $"{startLine}: {truncateLine(spanLines[0])}" : truncateLine(spanLines[0]);
                        else if (outputMode == "content_without_line_number")
                            firstLine = $"{rel}: {truncateLine(spanLines[0])}";
                        else
                            firstLine = showLineNumber ? $"{rel}:{startLine}: {truncateLine(spanLines[0])}" : $"{rel}: {truncateLine(spanLines[0])}";
                        sb.AppendLine(firstLine);
                        for (int li = 1; li < Math.Min(spanLines.Length, 4); li++)
                        {
                            string contLine;
                            if (outputMode == "content")
                                contLine = truncateLine(spanLines[li]);
                            else if (outputMode == "content_without_filename")
                                contLine = showLineNumber ? $"+{li}| {truncateLine(spanLines[li])}" : truncateLine(spanLines[li]);
                            else if (outputMode == "content_without_line_number")
                                contLine = $"  {truncateLine(spanLines[li])}";
                            else
                                contLine = showLineNumber ? $"  +{li}| {truncateLine(spanLines[li])}" : $"  {truncateLine(spanLines[li])}";
                            sb.AppendLine(contLine);
                        }
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
                    if (fileMaxMatches > 0 && fileMatchCount >= fileMaxMatches)
                        break; // 达到单文件匹配上限
                    var line = lines[i].TrimEnd('\r');
                    if (!Hit(line))
                        continue;
                    fileMatchCount++;
                    hits++;
                    totalMatches++;
                    string matchLine;
                    if (outputMode == "content")
                        matchLine = truncateLine(line);
                    else if (outputMode == "content_without_filename")
                        matchLine = showLineNumber ? $"{i + 1}: {truncateLine(line)}" : truncateLine(line);
                    else if (outputMode == "content_without_line_number")
                        matchLine = $"{rel}: {truncateLine(line)}";
                    else
                        matchLine = showLineNumber ? $"{rel}:{i + 1}: {truncateLine(line)}" : $"{rel}: {truncateLine(line)}";
                    sb.AppendLine(matchLine);
                    for (int c = Math.Max(0, i - before); c <= Math.Min(lines.Length - 1, i + after); c++)
                    {
                        // 跳过已作为上个匹配上下文输出过的行，避免重复
                        if (c != i && c > printedUntil)
                        {
                            string ctxLine;
                            if (outputMode == "content")
                                ctxLine = truncateLine(lines[c].TrimEnd('\r'));
                            else if (outputMode == "content_without_filename")
                                ctxLine = showLineNumber ? $"{c + 1}| {truncateLine(lines[c].TrimEnd('\r'))}" : truncateLine(lines[c].TrimEnd('\r'));
                            else if (outputMode == "content_without_line_number")
                                ctxLine = $"  {truncateLine(lines[c].TrimEnd('\r'))}";
                            else
                                ctxLine = showLineNumber ? $"  {c + 1}| {truncateLine(lines[c].TrimEnd('\r'))}" : $"  {truncateLine(lines[c].TrimEnd('\r'))}";
                            sb.AppendLine(ctxLine);
                        }
                    }
                    printedUntil = Math.Max(printedUntil, Math.Min(lines.Length - 1, i + after));
                    sb.AppendLine();
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        await Task.Yield();
        if (File.Exists(full))
        {
            ScanFile(full, maxMatchesPerFile);
        }
        else if (Directory.Exists(full))
        {
            // 确定性输出：先收集再排序（枚举顺序跨平台不定），与 glob 保持一致
            var files = SkipDirs.EnumerateFilesPruned(full, depth >= 0 ? depth : int.MaxValue, includeIgnored).ToList();
            files.Sort(StringComparer.Ordinal);
            string? lastRel = null;
            foreach (var file in files)
            {
                if (hits >= max)
                    break;
                if (!showHidden && SkipDirs.IsHidden(file))
                    continue; // 跳过隐藏文件
                var rel = Path.GetRelativePath(full, file).Replace('\\', '/');
                if (heading && rel != lastRel)
                {
                    if (lastRel != null)
                        sb.AppendLine(); // 文件之间空行分隔
                    sb.AppendLine(rel); // 文件路径单独成行
                    lastRel = rel;
                }
                ScanFile(file, maxMatchesPerFile);
            }
        }
        else
        {
            throw new ToolException($"路径不存在: {target}");
        }

        if (hits == 0)
            return $"(无匹配: {pattern})";
        var notice = hits >= max ? $"\n…(已达 max_results={max} 上限，可能还有更多匹配；可用 max_results 参数提高)" : "";
        if (countOnly && totalMatches > 0)
            sb.AppendLine($"---\n共 {totalMatches} 处匹配");
        var result = filesOnly || countOnly
            ? $"匹配 {hits} 个文件:\n" + sb.ToString().TrimEnd() + notice
            : $"匹配 {hits} 处:\n" + sb.ToString().TrimEnd() + notice;
        if (showStats)
            result += $"\n[stats] 扫描 {filesScanned} 个文件，跳过 {filesSkipped} 个，匹配 {hits} 处";
        if (showTotalMatches && totalMatches > 0)
            result += $"\n[total] 共 {totalMatches} 处匹配";
        return result;
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
