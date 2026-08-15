using System.Text.RegularExpressions;

namespace CodeAgent;

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
    };

    public static bool IsSkipped(string dirName) => Names.Contains(dirName);

    /// <summary>判断文本是否疑似二进制（含 NUL 字节）。</summary>
    public static bool LooksBinary(string text)
    {
        if (text.Length == 0) return false;
        var span = text.AsSpan(0, Math.Min(text.Length, 8192));
        return span.Contains('\0');
    }
}

/// <summary>把 glob 模式（支持 **、*、?）转换为正则。</summary>
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
                    sb.Append(".*");
                    i++;
                    if (i + 1 < pattern.Length && (pattern[i + 1] == '/' || pattern[i + 1] == '\\'))
                        i++;
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
