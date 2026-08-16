using System.Text;

namespace CodeAgent;

/// <summary>终端 Markdown 渲染：流式安全（逐字符处理），支持代码块、行内代码、加粗、标题。</summary>
public sealed class ConsoleRenderer
{
    private readonly bool _enabled;
    private readonly StringBuilder _line = new();  // 代码块外的当前行缓冲
    private readonly StringBuilder _code = new();  // 代码块内的当前行缓冲
    private readonly List<string> _tableBuf = new(); // Markdown 表格行缓冲（表格结束统一按列对齐输出）
    private bool _inCode;
    private int _tickRun; // 当前连续反引号数

    public ConsoleRenderer(bool enabled) => _enabled = enabled;

    /// <summary>流式追加文本；完整行 / 完整代码块立即输出。</summary>
    public void Append(string text)
    {
        if (!_enabled)
        {
            Console.Write(text);
            return;
        }
        foreach (var ch in text)
        {
            if (_inCode)
                HandleCodeChar(ch);
            else
                HandleTextChar(ch);
        }
    }

    /// <summary>结束输出：刷出剩余缓冲。</summary>
    public void Flush()
    {
        if (!_enabled)
            return;
        if (_inCode)
        {
            EmitCode(_code.ToString());
            _code.Clear();
        }
        else if (_line.Length > 0)
        {
            var line = _line.ToString();
            _line.Clear();
            if (line.TrimStart().StartsWith('|'))
            {
                // 流以未换行的表格行结束：仍按表格对齐输出，而不是当普通文本
                _tableBuf.Add(line.TrimEnd('\n'));
                FlushTable();
            }
            else
            {
                FlushTable();
                EmitLine(line);
            }
        }
        else
        {
            FlushTable();
        }
    }

    private void HandleTextChar(char ch)
    {
        if (ch == '`')
        {
            _tickRun++;
            if (_tickRun == 3)
            {
                // 围栏开始：先输出围栏前的内容，进入代码模式
                EmitLine(_line.ToString());
                _line.Clear();
                _inCode = true;
                _tickRun = 0;
                return;
            }
            _line.Append(ch);
            return;
        }
        _tickRun = 0;
        if (ch == '\n')
        {
            _line.Append(ch);
            var line = _line.ToString();
            _line.Clear();
            if (line.TrimStart().StartsWith('|'))
            {
                // Markdown 表格行：缓冲，表格结束时统一按列对齐输出
                _tableBuf.Add(line.TrimEnd('\n'));
                return;
            }
            FlushTable(); // 表格结束（遇到非表格行）：先输出对齐的表格
            EmitLine(line);
        }
        else
        {
            _line.Append(ch);
        }
    }

    private void HandleCodeChar(char ch)
    {
        if (ch == '`')
        {
            _tickRun++;
            if (_tickRun == 3)
            {
                // 围栏结束
                EmitCode(_code.ToString());
                _code.Clear();
                _inCode = false;
                _tickRun = 0;
                return;
            }
            _code.Append(ch);
            return;
        }
        _tickRun = 0;
        _code.Append(ch);
    }

    private void EmitLine(string line)
    {
        var content = line.EndsWith('\n') ? line[..^1] : line;
        var color = ResolveLineColor(content);
        var parts = ParseInline(content);

        // 无任何样式：原样输出（保留换行）
        if (color is null && parts.Count <= 1)
        {
            Console.Write(line);
            return;
        }

        foreach (var (text, style) in parts)
        {
            SafeColor.Foreground(style switch
            {
                InlineStyle.Bold => ConsoleColor.White,
                InlineStyle.Code => ConsoleColor.DarkYellow,
                _ => color ?? ConsoleColor.Gray,
            });
            Console.Write(text);
        }
        SafeColor.Reset();
        Console.WriteLine();
    }

    private static ConsoleColor? ResolveLineColor(string content)
    {
        if (content.StartsWith('#'))
            return ConsoleColor.Cyan; // 标题
        if (content.StartsWith("---") || content.StartsWith("==="))
            return ConsoleColor.DarkGray; // 分隔线
        if (content.StartsWith('>'))
            return ConsoleColor.DarkGray; // 引用
        return null;
    }

    private void EmitCode(string code)
    {
        if (code.Length == 0)
            return;
        SafeColor.Foreground(ConsoleColor.Green);
        Console.Write(code);
        SafeColor.Reset();
    }

    private enum InlineStyle { Normal, Bold, Code }

    /// <summary>解析行内样式：`行内代码` 与 **加粗**。</summary>
    private static List<(string text, InlineStyle style)> ParseInline(string s)
    {
        var result = new List<(string, InlineStyle)>();
        var cur = new StringBuilder();
        var style = InlineStyle.Normal;

        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '`')
            {
                Flush();
                style = style == InlineStyle.Code ? InlineStyle.Normal : InlineStyle.Code;
            }
            else if (s[i] == '*' && i + 1 < s.Length && s[i + 1] == '*')
            {
                Flush();
                style = style == InlineStyle.Bold ? InlineStyle.Normal : InlineStyle.Bold;
                i++;
            }
            else
            {
                cur.Append(s[i]);
            }
        }
        Flush();
        return result;

        void Flush()
        {
            if (cur.Length > 0)
            {
                result.Add((cur.ToString(), style));
                cur.Clear();
            }
        }
    }

    // —— Markdown 表格渲染 ——

    /// <summary>输出缓冲的表格行：按列宽对齐、分隔行用横线、考虑 CJK 显示宽度。</summary>
    private void FlushTable()
    {
        if (_tableBuf.Count == 0)
            return;
        var rows = _tableBuf.Select(SplitCells).ToList();
        var cols = rows.Max(r => r.Count);
        var widths = new int[cols];
        foreach (var r in rows)
            for (int i = 0; i < r.Count; i++)
                widths[i] = Math.Max(widths[i], DisplayWidth(r[i]));
        foreach (var r in rows)
        {
            var isSep = r.Count > 0 && r.All(c => c.Length > 0 && c.All(ch => ch is '-' or ':' or ' '));
            var cells = new string[r.Count];
            for (int i = 0; i < r.Count; i++)
                cells[i] = isSep ? new string('─', widths[i]) : PadToWidth(r[i], widths[i]);
            Console.WriteLine("  " + string.Join(" │ ", cells));
        }
        _tableBuf.Clear();
    }

    private static List<string> SplitCells(string row)
    {
        var s = row.Trim().TrimStart('|').TrimEnd('|');
        return s.Split('|').Select(c => c.Trim()).ToList();
    }

    private static string PadToWidth(string s, int width)
    {
        var pad = width - DisplayWidth(s);
        return pad > 0 ? s + new string(' ', pad) : s;
    }

    /// <summary>显示宽度：CJK/全角字符按 2 列计算。</summary>
    private static int DisplayWidth(string s)
    {
        int w = 0;
        foreach (var c in s)
            w += c > 0x2E7F ? 2 : 1;
        return w;
    }
}
