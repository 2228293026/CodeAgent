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
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(sub, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += sub.Length;
        }
        return count;
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
