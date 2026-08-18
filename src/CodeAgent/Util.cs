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
    public static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + $"\n…(共 {s.Length} 字符，已截断)";

    public static string TruncateLine(string s, int max)
    {
        s = s.Replace("\t", "    ");
        return s.Length <= max ? s : s[..max] + " …";
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

    /// <summary>紧凑 token 数格式（如 1937 → 1.9k，1.2M → 1.2M）。</summary>
    public static string CompactTokenCount(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:F1}M" : n >= 1000 ? $"{n / 1000.0:F1}k" : n.ToString();

    /// <summary>会话总时长文本（如 2m 5s / 22s，不足 1 分钟取整秒）。</summary>
    public static string FormatSessionTime(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{t.TotalSeconds:F0}s";

    /// <summary>耗时格式（如 1m 5s / 22.0s，保留一位小数秒）。</summary>
    public static string FormatElapsed(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{t.TotalSeconds:F1}s";

    /// <summary>耗时格式：不足 1 秒用毫秒（避免快操作显示 0.0s），否则显示秒。</summary>
    public static string FormatDuration(TimeSpan t) =>
        t.TotalSeconds < 1 ? $"{t.TotalMilliseconds:F0}ms" : $"{t.TotalSeconds:F1}s";

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
}

/// <summary>搜索时需要跳过的构建/缓存/版本控制目录。</summary>
public static class SkipDirs
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".vs", ".idea", ".vscode", ".trae",
        "bin", "obj", "node_modules", "packages", "dist", "build", "out",
        "target", "Library", "Temp", "temp", "logs", "Logs", ".codeagent",
        ".venv", "venv", ".gradle", ".terraform", ".dart_tool", ".pytest_cache",
        ".next", ".nuxt", "Pods", ".mypy_cache", ".ruff_cache", ".coverage",
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
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
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

/// <summary>把 glob 模式（支持 **、*、?、字符类 [ab]/[a-z]/[!abc]）转换为正则。</summary>
public static class Glob
{
    public static Regex ToRegex(string pattern)
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
        // 结尾换行会让 SplitLines 多出一个空串元素，计数时去掉（如 "a\nb\n" 逻辑上是 2 行）
        if (oldLines.Length == 1 && oldLines[0].Length == 0 && newLines.Length > 0)
        {
            var newCount = newLines[^1].Length == 0 ? newLines.Length - 1 : newLines.Length;
            var sbNew = new System.Text.StringBuilder();
            sbNew.AppendLine($"--- a/{path}");
            sbNew.AppendLine($"+++ b/{path}");
            sbNew.AppendLine($"@@ -0,0 +1,{newCount} @@");
            for (int i = 0; i < newCount; i++)
                sbNew.AppendLine("+ " + newLines[i]);
            return sbNew.ToString().TrimEnd();
        }
        if (newLines.Length == 1 && newLines[0].Length == 0 && oldLines.Length > 0)
        {
            var oldCount = oldLines[^1].Length == 0 ? oldLines.Length - 1 : oldLines.Length;
            var sbDel = new System.Text.StringBuilder();
            sbDel.AppendLine($"--- a/{path}");
            sbDel.AppendLine($"+++ b/{path}");
            sbDel.AppendLine($"@@ -1,{oldCount} +0,0 @@");
            for (int i = 0; i < oldCount; i++)
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
        var firstChange = ops.FindIndex(o => o.Op != ' ');
        if (firstChange >= 0)
        {
            var oldLine = 1 + ops.Take(firstChange).Count(o => o.Op != '+');
            var newLine = 1 + ops.Take(firstChange).Count(o => o.Op != '-');
            sb.AppendLine($"@@ -{oldLine} +{newLine} @@");
        }
        const int ctx = 2;
        for (int i = 0; i < ops.Count; i++)
        {
            var near = ops.Skip(Math.Max(0, i - ctx)).Take(ctx).Concat(ops.Skip(i + 1).Take(ctx)).Any(o => o.Op != ' ');
            if (ops[i].Op == ' ')
            {
                if (near)
                    sb.AppendLine("  " + ops[i].Line);
            }
            else
            {
                sb.AppendLine($"{(ops[i].Op == '-' ? "- " : "+ ")}{ops[i].Line}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Split('\n');
}
