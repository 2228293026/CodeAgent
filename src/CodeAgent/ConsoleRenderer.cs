using System.Text;

namespace CodeAgent;

/// <summary>终端 Markdown 渲染：流式安全（逐字符处理），支持代码块、行内代码、加粗、标题。</summary>
public sealed class ConsoleRenderer
{
    private readonly bool _enabled;
    private readonly StringBuilder _line = new();  // 代码块外的当前行缓冲
    private readonly StringBuilder _code = new();  // 代码块内的当前行缓冲
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
            EmitLine(_line.ToString());
            _line.Clear();
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
            EmitLine(_line.ToString());
            _line.Clear();
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
            Console.ForegroundColor = style switch
            {
                InlineStyle.Bold => ConsoleColor.White,
                InlineStyle.Code => ConsoleColor.DarkYellow,
                _ => color ?? ConsoleColor.Gray,
            };
            Console.Write(text);
        }
        Console.ResetColor();
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
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write(code);
        Console.ResetColor();
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
}
