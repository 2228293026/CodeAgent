using System.Text;
using System.Text.RegularExpressions;

namespace CodeAgent;

/// <summary>安全的颜色输出：终端不支持颜色时静默降级为普通输出（避免绘制/日志崩溃）。</summary>
public static class SafeColor
{
    public static void Foreground(ConsoleColor c)
    {
        try { Console.ForegroundColor = c; } catch { /* 不支持颜色 */ }
    }

    public static void Background(ConsoleColor c)
    {
        try { Console.BackgroundColor = c; } catch { /* 不支持颜色 */ }
    }

    public static void Reset()
    {
        try { Console.ResetColor(); } catch { /* 不支持颜色 */ }
    }
}

/// <summary>通用小工具。</summary>
public static class TextUtil
{
    static TextUtil() =>
        // GB18030/GBK 等本地代码页在 .NET（Core）需显式注册，ReadTextSmart 的兜底解码依赖它
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    /// <summary>显示宽度：CJK/全角字符按 2 列计算；emoji 等代理对按 2 列（两个 surrogate 只算一次）。
    /// 孤立代理（半个码点）按 1 列——终端渲染为单宽替换符，按 2 列算会让对齐偏一列。
    /// 终端对齐/截断共用（InputLine、ConsoleRenderer 曾各自实现一份）。</summary>
    public static int DisplayWidth(string s)
    {
        int w = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                w += 2; // 代理对（emoji）：终端按 2 列显示
                i++;
            }
            else
            {
                w += !char.IsSurrogate(c) && c > 0x2E7F ? 2 : 1;
            }
        }
        return w;
    }

    /// <summary>截断尾部的半个多字节序列：截断点可能切在 UTF-8 多字节序列中间，
    /// 不回退会让严格 UTF-8 校验失败、整段输出被误判为 GBK 解码。序列完整时原样返回。</summary>
    internal static int TrimPartialTail(byte[] bytes, int end)
    {
        int cont = 0, scan = end;
        while (scan > 0 && cont < 3 && (bytes[scan - 1] & 0xC0) == 0x80)
        {
            scan--;
            cont++;
        }
        if (end > 0 && (bytes[end - 1] & 0xC0) == 0xC0)
            return end - 1; // 尾字节本身是首字节：后续续字节全被截掉，必残缺
        if (scan == end || scan == 0)
            return end; // 尾字节是 ASCII（边界完好）或全是续字节（原样交解码器）
        var lead = bytes[scan - 1];
        if ((lead & 0xC0) != 0xC0)
            return end; // 续字节前面不是首字节（杂散）：原样交给解码器处理
        var expected = lead >= 0xF0 ? 4 : lead >= 0xE0 ? 3 : 2;
        return cont + 1 == expected ? end : scan - 1; // 完整保留；残缺连首字节一起丢
    }

    /// <summary>读文本文件：BOM 优先；无 BOM 时严格校验 UTF-8，非法则按 GB18030 兜底。
    /// 老 Windows 工具保存的 ANSI（GBK）中文文件若按 UTF-8 读会出现乱码，
    /// grep 搜不到中文、read_file 显示 &#65533; 替换符。</summary>
    public static string ReadTextSmart(string path) => DecodeSmart(File.ReadAllBytes(path));

    /// <summary>ReadTextSmart 的异步版本。</summary>
    public static async Task<string> ReadTextSmartAsync(string path, CancellationToken ct = default) =>
        DecodeSmart(await File.ReadAllBytesAsync(path, ct));

    /// <summary>写入文本：目标已存在且带 UTF-8 BOM 时保留 BOM。
    /// 部分 Windows 工具（PowerShell 5.1、老编辑器）靠 BOM 识别 UTF-8，
    /// 改写后 BOM 丢失会让其中的中文变成乱码。</summary>
    public static async Task WriteTextPreserveBomAsync(string path, string content, CancellationToken ct = default)
    {
        bool keepBom = false;
        if (File.Exists(path))
        {
            var head = new byte[3];
            using (var fs = File.OpenRead(path))
                keepBom = await fs.ReadAsync(head.AsMemory(0, 3), ct) == 3
                          && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
        }
        await File.WriteAllTextAsync(path, content, new System.Text.UTF8Encoding(keepBom), ct);
    }

    /// <summary>探测文件编码（撤销原样恢复用）："utf8-bom"（带 BOM）| "gb18030"（非 UTF-8 的旧编码）
    /// | null（无 BOM 的 UTF-8 / 新文件）。只读前 4KB 做判定，不整读大文件。</summary>
    public static string? DetectFileEncoding(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[4096];
            var n = fs.Read(head);
            if (n >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
                return "utf8-bom";
            var end = TrimPartialTail(head, n);
            try
            {
                _ = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(head[..end]);
                return null;
            }
            catch (System.Text.DecoderFallbackException)
            {
                return "gb18030";
            }
        }
        catch
        {
            return null; // 读不到按 UTF-8 处理
        }
    }
    /// <summary>改写文件时保持原编码（edit/write 工具用）：
    /// 带 BOM 的 UTF-8 保留 BOM；无 BOM 且非合法 UTF-8（GBK 旧文件）按 GB18030 写回，文件编码不被静默转换；
    /// 新建文件一律无 BOM UTF-8。</summary>
    public static async Task WriteTextPreserveEncodingAsync(string path, string content, CancellationToken ct = default)
    {
        System.Text.Encoding enc = new System.Text.UTF8Encoding(false);
        if (File.Exists(path))
        {
            var head = new byte[4096];
            int n;
            using (var fs = File.OpenRead(path))
                n = await fs.ReadAsync(head.AsMemory(0, head.Length), ct);
            var prefix = head[..n];
            if (n >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
                enc = new System.Text.UTF8Encoding(true); // 原 BOM 保留
            else
            {
                // 前缀可能截在多字节序列中间：先回退边界再做严格 UTF-8 校验
                var end = TrimPartialTail(prefix, prefix.Length);
                try
                {
                    _ = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(prefix[..end]);
                }
                catch (System.Text.DecoderFallbackException)
                {
                    enc = System.Text.Encoding.GetEncoding("GB18030"); // 原 GBK：按 GB18030 写回
                }
            }
        }
        // 原子写：先写到同目录临时文件再 rename 覆盖，避免进程在写入中途崩溃/断电时把目标文件留在半截（损坏）。
        // File.Move(overwrite: true) 在同一卷上是原子操作；临时文件写在目标同目录确保在同一卷、不会被并行 GC 偷走。
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir))
            dir = ".";
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(tmp, content, enc, ct);
            // File.Move(overwrite: true) 是原子替换：移动失败回退到 File.Copy+Delete,覆盖到 .NET 5 之前不支持的旧环境时也工作
            try
            {
                File.Move(tmp, path, overwrite: true);
            }
            catch (PlatformNotSupportedException)
            {
                File.Copy(tmp, path, overwrite: true);
                File.Delete(tmp);
            }
        }
        catch
        {
            // 任何步骤失败:尝试清理临时文件后重新抛出原始异常
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }
    public static void WriteTextPreserveBom(string path, string content)
    {
        bool keepBom = false;
        if (File.Exists(path))
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[3];
            keepBom = fs.Read(head) == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
        }
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(keepBom));
    }

    internal static string DecodeSmart(byte[] bytes) // internal：ShellRunner 命令输出解码复用
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            // 严格 UTF-8：非法序列抛 DecoderFallbackException → 说明不是 UTF-8，走 GB18030
            return new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException)
        {
            return System.Text.Encoding.GetEncoding("GB18030").GetString(bytes);
        }
    }
    /// <summary>粗略 token 估算：ASCII 段按 4 字符/token，CJK/全角按每字 1 token
    ///（spinner ↑ 与 ctx 无 usage 时的回退口径，中文会话 chars/4 会低估约 4 倍）。
    /// 代理对（emoji）按 1 个码点计——按码元算会把一个 emoji 记成 2 个非 ASCII 字符，高估一倍。</summary>
    public static long EstimateTokens(string s)
    {
        long cjk = 0;
        for (int i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (char.IsHighSurrogate(ch))
            {
                // 高+低代理对整体算 1 个非 ASCII 码点；孤立代理按单字符走通用分支
                if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    cjk++;
                    i++;
                    continue;
                }
                continue; // 孤立高代理：不计入 CJK（落入 ASCII 桶按 4 字符/token）
            }
            if (ch >= 0x2E80 && !char.IsLowSurrogate(ch))
                cjk++;
        }
        return (s.Length - cjk) / 4 + cjk;
    }

    public static string Truncate(string s, int max)
    {
        if (s.Length <= max)
            return s;
        return SafeCut(s, max) + $"\n…(共 {s.Length} 字符，已截断)";
    }

    /// <summary>超长命令输出保留头尾（编译/测试的错误摘要几乎总在末尾，纯头部截断会把
    /// 最关键的报错丢掉）：头部占 2/3、尾部占 1/3，中段以省略标记替代并注明丢弃字符数。</summary>
    public static string TruncateHeadTail(string s, int max)
    {
        if (s.Length <= max)
            return s;
        const string markerFormat = "\n…[中间省略 {0:N0} 字符]…\n";
        var markerLen = string.Format(markerFormat, 0L).Length + 8; // 预留数字位宽
        var head = Math.Max(0, max * 2 / 3);
        var tail = Math.Max(0, max - head - markerLen);
        if (tail == 0)
            return Truncate(s, max);
        return SafeCut(s, head) + string.Format(markerFormat, (long)s.Length - head - tail) + s[^tail..];
    }

    /// <summary>
    /// 工具输出截断（供 Agent 落给模型前调用）：保留头部约 2/3 与尾部 1/3，中间省略，
    /// 并附一行明确说明——模型据此知道输出被裁剪、需要改用 offset/分段或换参数缩小范围，
    /// 而不是把残缺内容当完整结果。
    /// </summary>
    public static string TruncateToolOutput(string s, int max)
    {
        if (s.Length <= max)
            return s;
        var head = Math.Max(0, max * 2 / 3);
        var tail = Math.Max(0, max - head);
        var marker = $"\n…[工具输出过长，已截断：原 {s.Length:N0} 字符，保留头 {head:N0} 与尾 {tail:N0}，中间省略]…\n";
        return SafeCut(s, head) + marker + s[^tail..];
    }

    public static string TruncateLine(string s, int max)
    {
        s = s.Replace("\t", "    ");
        return s.Length <= max ? s : SafeCut(s, max) + " …";
    }

    /// <summary>按字符数截断，但不劈开 UTF-16 代理对（emoji 半个码点会显示为乱码）。</summary>
    private static string SafeCut(string s, int max)
    {
        if (max > 0 && max < s.Length && char.IsHighSurrogate(s[max - 1]))
            max--; // 切点落在高位代理上：后退一位，保持代理对完整
        return s[..max];
    }

    public static int CountOccurrences(string text, string sub)
    {
        // 空子串会令 IndexOf 恒返回 idx 且 idx += 0 永不前进 → 死循环（回归：测试曾触发主机挂起）
        if (sub.Length == 0)
            return 0;
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(sub, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += sub.Length;
        }
        return count;
    }

    /// <summary>
    /// 空白归一化：把每段连续空白（含换行）压成单个空格并去首尾。
    /// 用于「old_string 未命中」时的相似度判断——缩进/换行差异导致的失配可被识别出来并给出提示。
    /// </summary>
    public static string NormalizeWhitespace(string s)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;
        var sb = new StringBuilder(s.Length);
        bool pendingSpace = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0; // 首部空白直接丢弃
            }
            else
            {
                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    /// <summary>紧凑 token 数格式（如 1937 → 1.9k，1.2M → 1.2M）。</summary>
    public static string CompactTokenCount(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:F1}M" : n >= 1000 ? $"{n / 1000.0:F1}k" : n.ToString();

    /// <summary>会话总时长文本（如 2h 5m / 2m 5s / 22s，不足 1 分钟取整秒）。</summary>
    public static string FormatSessionTime(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s"
        : $"{t.TotalSeconds:F0}s";

    /// <summary>相对时间文本（刚刚 / N 分钟前 / N 小时前 / N 天前；超过 30 天回退到日期，
    /// 跨年带年份）。/resume 列表用：文件名时间戳不便认会话新旧。</summary>
    public static string RelativeTime(DateTime utc, DateTime nowUtc)
    {
        var span = nowUtc - utc;
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero; // 时钟回拨/未来文件：按刚刚处理
        if (span.TotalMinutes < 1)
            return "刚刚";
        if (span.TotalHours < 1)
            return $"{(int)span.TotalMinutes} 分钟前";
        if (span.TotalDays < 1)
            return $"{(int)span.TotalHours} 小时前";
        if (span.TotalDays <= 30)
            return $"{(int)span.TotalDays} 天前";
        return utc.Year == nowUtc.Year
            ? $"{utc.Month}月{utc.Day}日"
            : $"{utc.Year}年{utc.Month}月{utc.Day}日";
    }

    /// <summary>耗时格式（如 1m 5s / 22.0s，保留一位小数秒）。</summary>
    public static string FormatElapsed(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{t.TotalSeconds:F1}s";

    /// <summary>耗时格式：不足 1 秒用毫秒（避免快操作显示 0.0s），否则显示秒。</summary>
    public static string FormatDuration(TimeSpan t) =>
        t.TotalSeconds < 1 ? $"{t.TotalMilliseconds:F0}ms" : $"{t.TotalSeconds:F1}s";

    /// <summary>人类可读字节数（B/KB/MB/GB/TB，1024 进制）：/session、/diag 等展示文件大小时共用，避免裸字节数难以辨认。</summary>
    public static string FormatBytes(long bytes)
    {
        double v = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        while (i < units.Length - 1 && v >= 1024)
        {
            v /= 1024.0;
            i++;
        }
        return i == 0 ? $"{bytes} B" : $"{v:F1} {units[i]}";
    }

    /// <summary>模型短名：取 '/' 后的末段；末段过短（&lt;5 字符）不具辨识度时保留完整名。</summary>
    public static string ShortModelName(string model)
    {
        var slash = model.LastIndexOf('/');
        if (slash >= 0)
        {
            var last = model[(slash + 1)..];
            if (last.Length >= 5)
                return last;
        }
        return model;
    }

    /// <summary>百分比（0-100 收敛）：part/total 的整数百分比，total ≤ 0 时返回 0，负值收敛到 0。</summary>
    public static int PercentOf(long part, long total) =>
        total <= 0 ? 0 : (int)Math.Clamp(part * 100.0 / total, 0, 100);

    /// <summary>token 成本（美元）：单价按每百万 token 计；任一单价 ≤ 0 时返回 null（不显示费用）。</summary>
    public static double? UsdCost(long inputTokens, long outputTokens, double pricePerMillionInput, double pricePerMillionOutput)
    {
        if (pricePerMillionInput <= 0 || pricePerMillionOutput <= 0)
            return null;
        return inputTokens * pricePerMillionInput / 1_000_000.0
             + outputTokens * pricePerMillionOutput / 1_000_000.0;
    }

    /// <summary>费用文本（回合摘要与 /stats 共用口径）：≥ $0.01 保留两位小数，
    /// 小额保留四位（$0.00 会吞掉真实开销）。</summary>
    public static string FormatCost(double cost) =>
        cost < 0.01 ? cost.ToString("F4") : cost.ToString("F2");

    /// <summary>递归统计目录下所有文件的总字节数（不含目录自身）。</summary>
    public static long GetDirectorySizeBytes(string dir)
    {
        if (!Directory.Exists(dir))
            return 0;
        long total = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(f).Length; }
            catch { /* 无权限/竞态文件跳过，不影响其余统计 */ }
        }
        return total;
    }
}

/// <summary>搜索时需要跳过的构建/缓存/版本控制目录。</summary>
public static class SkipDirs
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".vs", ".idea", ".vscode", ".trae",
        "bin", "obj", "node_modules", "packages", "dist", "build", "out",
        "target", "Library", "Temp", "temp", "logs", "Logs", ".codeagent", "__pycache__", ".tox", "DerivedData",
        ".venv", "venv", ".gradle", ".terraform", ".dart_tool", ".pytest_cache",
        ".next", ".nuxt", "Pods", ".mypy_cache", ".ruff_cache", ".coverage",
        // 常见包管理器缓存与前端构建缓存：glob/grep 不应扫进这些目录
        ".cache", ".npm", ".yarn", ".pnpm-store", ".turbo", ".eslintcache", ".parcel-cache",
        ".angular", ".svelte-kit", ".astro", ".cargo", "vendor", ".bundle",
        ".egg-info", ".eggs", ".ipynb_checkpoints", ".serverless", ".stack-work",
        // 依赖库与测试/构建产物目录：libs（mod/游戏项目的引用 DLL）、third_party/external
        //（vendored 源码）、TestResults（dotnet trx）、artifacts（dotnet 产物约定）、coverage
        "libs", "third_party", "ThirdParty", "3rdparty", "external", "Externals",
        "TestResults", "artifacts", "coverage", "Coverage",
    };

    public static bool IsSkipped(string dirName) => Names.Contains(dirName);

    /// <summary>
    /// 递归枚举文件，但剪枝掉被跳过的目录（不进入其中遍历），避免 glob/grep
    /// 在 node_modules / bin / obj 等目录里做无用扫描。
    /// </summary>
    public static IEnumerable<string> EnumerateFilesPruned(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        // 已访问目录集合：junction/symlink 成环（A→B→A）时避免死循环（曾会永久挂起 glob/grep）
        var visited = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            if (!visited.Add(dir))
                continue;
            IEnumerable<string> subDirs;
            IEnumerable<string> files;
            try
            {
                subDirs = Directory.EnumerateDirectories(dir);
                files = Directory.EnumerateFiles(dir);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var f in files)
                yield return f;
            foreach (var d in subDirs)
            {
                var name = Path.GetFileName(d);
                if (IsSkipped(name))
                    continue; // 剪枝：不进入被跳过的目录
                stack.Push(d);
            }
        }
    }

    /// <summary>判断文本是否疑似二进制（含 NUL 字节）。</summary>
    public static bool LooksBinary(string text)
    {
        if (text.Length == 0) return false;
        var span = text.AsSpan(0, Math.Min(text.Length, 8192));
        return span.Contains('\0');
    }
}

/// <summary>把 glob 模式（支持 **、*、?、字符类 [ab]/[a-z]/[!abc]）转换为正则。
/// 模式 → 正则结果带缓存：grep 的 include/exclude 每次调用都重复编译同样的几个模式
/// （RegexOptions.Compiled 编译有可感知开销），缓存后只编译一次。</summary>
public static class Glob
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> Cache = new();
    private const int MaxCacheEntries = 512; // 防御：模型批量发 pattern 时缓存不无限膨胀

    public static Regex ToRegex(string pattern)
    {
        if (Cache.Count > MaxCacheEntries)
            Cache.Clear(); // 简单兜底：超限全清（模式集合通常很小，重建代价低）
        return Cache.GetOrAdd(pattern, static p => Build(p));
    }

    private static Regex Build(string pattern)
    {
        // Windows 风格反斜杠分隔符归一化为 /（与工具层 rel.Replace('\\','/') 一致）：
        // 否则 src\**\*.cs 的 pattern 匹配不到已归一化成正斜杠的相对路径
        pattern = pattern.Replace('\\', '/');
        var sb = new System.Text.StringBuilder("^");
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '*')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < pattern.Length && (pattern[i + 1] == '/' || pattern[i + 1] == '\\'))
                    {
                        // **/ 表示「零或多个目录段 + 分隔符」：a/**/b 应匹配 a/b、a/x/b，
                        // 但不能匹配 a/xb（x 不是目录段）。因此用 (?:…/)* 而非 .*
                        sb.Append("(?:[^/\\\\]*/)*");
                        i++;
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                }
                else
                {
                    sb.Append("[^/\\\\]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/\\\\]");
            }
            else if (c == '{')
            {
                // 花括号多选：{a,b,c} → (?:a|b|c)；无右花括号、无逗号或全为空时按字面 '{' 处理
                var close = pattern.IndexOf('}', i + 1);
                if (close > i + 1)
                {
                    var inner = pattern[(i + 1)..close];
                    var parts = inner.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                    if (inner.Contains(',') && parts.Count >= 1)
                    {
                        sb.Append("(?:");
                        sb.Append(string.Join('|', parts.Select(Regex.Escape)));
                        sb.Append(')');
                        i = close;
                        continue;
                    }
                }
                sb.Append(Regex.Escape("{"));
            }
            else if (c == '[')
            {
                // 字符类：[abc]、[a-z]、[!abc]（否定）；未闭合或空类按字面 '[' 处理
                var close = pattern.IndexOf(']', i + 1);
                if (close > i + 1)
                {
                    var cls = pattern[(i + 1)..close];
                    var neg = false;
                    if (cls.StartsWith('!') || cls.StartsWith('^'))
                    {
                        neg = true;
                        cls = cls[1..];
                    }
                    if (cls.Length > 0)
                    {
                        cls = cls.Replace("\\", "\\\\").Replace("]", "\\]");
                        sb.Append(neg ? "[^" : "[");
                        sb.Append(cls);
                        sb.Append(']');
                        i = close;
                        continue;
                    }
                }
                sb.Append(Regex.Escape("["));
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}

/// <summary>行级 diff（LCS），输出简化的 unified 风格文本（/diff 用）。</summary>
public static class DiffUtil
{
    /// <summary>比较两份文本，返回 unified 风格 diff；无差异时返回空字符串。</summary>
    public static string Unified(string oldText, string newText, string path)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);
        if (oldLines.SequenceEqual(newLines))
            return "";

        // 空原文（新建文件）或空新文（文件被清空/删除）：跳过 LCS，直接输出全新增/全删除行。
        if (oldLines.Length == 0 && newLines.Length > 0)
        {
            var sbNew = new System.Text.StringBuilder();
            sbNew.AppendLine($"--- a/{path}");
            sbNew.AppendLine($"+++ b/{path}");
            sbNew.AppendLine($"@@ -0,0 +1,{newLines.Length} @@");
            for (int i = 0; i < newLines.Length; i++)
                sbNew.AppendLine("+ " + newLines[i]);
            return sbNew.ToString().TrimEnd();
        }
        if (newLines.Length == 0 && oldLines.Length > 0)
        {
            var sbDel = new System.Text.StringBuilder();
            sbDel.AppendLine($"--- a/{path}");
            sbDel.AppendLine($"+++ b/{path}");
            sbDel.AppendLine($"@@ -1,{oldLines.Length} +0,0 @@");
            for (int i = 0; i < oldLines.Length; i++)
                sbDel.AppendLine("- " + oldLines[i]);
            return sbDel.ToString().TrimEnd();
        }

        int n = oldLines.Length, m = newLines.Length;

        // LCS 的 dp 矩阵是 O(n*m) 内存：几万行的文件会分配数 GB 数组直接 OOM，
        // 超限时退化为行数摘要（/diff 大文件场景）。
        const long MaxDpCells = 2_000_000;
        if ((long)n * m > MaxDpCells)
        {
            var sb0 = new System.Text.StringBuilder();
            sb0.AppendLine($"--- a/{path}");
            sb0.AppendLine($"+++ b/{path}");
            sb0.AppendLine($"@@ -{n} +{m} @@");
            sb0.AppendLine($"（内容差异过大（{n} 行 → {m} 行），跳过逐行 diff）");
            return sb0.ToString().TrimEnd();
        }

        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                dp[i, j] = oldLines[i] == newLines[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var ops = new List<(char Op, string Line)>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (oldLines[x] == newLines[y]) { ops.Add((' ', oldLines[x])); x++; y++; }
            else if (dp[x + 1, y] >= dp[x, y + 1]) { ops.Add(('-', oldLines[x])); x++; }
            else { ops.Add(('+', newLines[y])); y++; }
        }
        while (x < n) ops.Add(('-', oldLines[x++]));
        while (y < m) ops.Add(('+', newLines[y++]));

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"--- a/{path}");
        sb.AppendLine($"+++ b/{path}");
        // 多处修改分段输出：每段一个 @@ 头（头内行号准确），
        // 曾全程只发一个头，第二处修改之后的行号全部错位
        const int ctx = 2;
        int oldPos = 0, newPos = 0;
        var inHunk = false;
        for (int i = 0; i < ops.Count; i++)
        {
            var (op, line) = ops[i];
            var near = ops.Skip(Math.Max(0, i - ctx)).Take(ctx).Concat(ops.Skip(i + 1).Take(ctx)).Any(o => o.Op != ' ');
            var emit = op != ' ' || near;
            if (emit && !inHunk)
            {
                sb.AppendLine($"@@ -{oldPos + 1} +{newPos + 1} @@");
                inHunk = true;
            }
            else if (!emit)
            {
                inHunk = false;
            }
            if (emit)
                sb.AppendLine(op == ' ' ? "  " + line : $"{op} {line}");
            if (op != '+') oldPos++;
            if (op != '-') newPos++;
        }
        return sb.ToString().TrimEnd();
    }

    private static string[] SplitLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        // 去掉末尾空串（结尾换行产生的），使 "a\nb\n" 与 "a\nb" 都得到 ["a","b"]
        if (lines.Length > 0 && lines[^1].Length == 0)
            lines = lines[..^1];
        return lines;
    }
}
