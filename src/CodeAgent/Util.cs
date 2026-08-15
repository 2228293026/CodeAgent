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
