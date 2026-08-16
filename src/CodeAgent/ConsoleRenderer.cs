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
    private bool _skipCodeIntro; // 围栏起始行的语言标注（如 ```cs 的 cs）应丢弃

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
                // 前两个反引号已追加进 _line：检查围栏前缀（反引号之前的文本）是否全为空白。
                // 仅当 ``` 位于行首（可含前导空白）时才视为围栏开始；
                // 行中出现的 ```（如 use ```literal``` here）应作为字面文本保留，
                // 否则 _skipCodeIntro 会吞掉本行剩余内容。
                var prefixLen = _line.Length - (_tickRun - 1); // 去掉已追加的两个反引号
                var prefix = prefixLen > 0 ? _line.ToString(0, prefixLen) : "";
                if (!string.IsNullOrWhiteSpace(prefix))
                {
                    _line.Append('`');
                    _tickRun = 0;
                    return;
                }
                // 围栏开始：先输出围栏前的内容，进入代码模式
                EmitLine(_line.ToString());
                _line.Clear();
                _inCode = true;
                _tickRun = 0;
                _skipCodeIntro = true; // 丢弃本行剩余的语言标注（```cs）
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
        // 围栏起始行（```cs 的 cs）：丢弃直到换行，避免语言标注混入代码内容
        if (_skipCodeIntro)
        {
            if (ch == '\n')
                _skipCodeIntro = false;
            return;
        }
        if (ch == '\r')
            return; // 跳过 CRLF 的 \r，避免混入代码内容（终端会把 \r 当回车覆盖渲染）
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
        // 剥离行尾 \r（Windows 换行残留）：否则终端把 \r 当回车，光标跳回行首覆盖本行
        var hadNewline = line.EndsWith('\n');
        var content = hadNewline ? line[..^1] : line;
        content = content.TrimEnd('\r');
        var color = ResolveLineColor(content);
        var parts = ParseInline(content);

        // 无任何样式：原样输出（保留换行）
        if (color is null && parts.Count <= 1)
        {
            Console.Write(content + (hadNewline ? "\n" : ""));
            return;
        }

        foreach (var (text, style) in parts)
        {
            SafeColor.Foreground(style switch
            {
                InlineStyleToken.Bold => ConsoleColor.White,
                InlineStyleToken.Code => ConsoleColor.DarkYellow,
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

    /// <summary>行内样式（internal 以便测试断言样式序列）。</summary>
    internal enum InlineStyleToken { Normal, Bold, Code }

    /// <summary>解析行内样式：`行内代码` 与 **加粗**（internal 以便测试样式序列）。</summary>
    internal static List<(string text, InlineStyleToken style)> ParseInline(string s)
    {
        var result = new List<(string, InlineStyleToken)>();
        var cur = new StringBuilder();
        var style = InlineStyleToken.Normal;
        var codePrevStyle = InlineStyleToken.Normal; // 进入行内代码前的外层样式，退出时恢复

        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '`')
            {
                // 连续 3 个及以上反引号是字面文本（围栏语法，行内解析不处理），
                // 只有单个反引号对才作为行内代码开关
                var run = 1;
                while (i + run < s.Length && s[i + run] == '`')
                    run++;
                if (run >= 3)
                {
                    cur.Append(s, i, run);
                    i += run - 1;
                    continue;
                }
                Flush();
                if (style == InlineStyleToken.Code)
                    style = codePrevStyle; // 退出代码：恢复外层样式（如加粗）
                else
                {
                    codePrevStyle = style; // 进入代码：记住外层样式
                    style = InlineStyleToken.Code;
                }
            }
            else if (s[i] == '*' && i + 1 < s.Length && s[i + 1] == '*')
            {
                // 行内代码内一切按字面处理：`a**b` 的 ** 应保留，不能作为加粗开关消费
                if (style == InlineStyleToken.Code)
                {
                    cur.Append(s[i]);
                    cur.Append(s[i + 1]);
                    i++;
                    continue;
                }
                // 连续 3 个及以上星号按字面文本保留（如 *** 分隔符），
                // 否则 *** 会被解析成 ** 加粗开关 + 单个 *，渲染时丢失两个星号
                var run = 2;
                while (i + run < s.Length && s[i + run] == '*')
                    run++;
                if (run >= 3)
                {
                    cur.Append(s, i, run);
                    i += run - 1;
                    continue;
                }
                Flush();
                style = style == InlineStyleToken.Bold ? InlineStyleToken.Normal : InlineStyleToken.Bold;
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

    /// <summary>单张表格最大渲染行数，超出截断（防止模型输出的超大表格撑爆终端）。</summary>
    private const int MaxTableRows = 50;

    /// <summary>Markdown 分隔行单元格：可选冒号 + 至少 3 个 -（---、:---、---:、:---:）。</summary>
    private static readonly System.Text.RegularExpressions.Regex SepRe = new(@"^:?-{3,}:?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>输出缓冲的表格行：按列宽对齐、分隔行用横线、考虑 CJK 显示宽度。</summary>
    private void FlushTable()
    {
        if (_tableBuf.Count == 0)
            return;
        var truncated = _tableBuf.Count > MaxTableRows;
        var visible = truncated ? _tableBuf.Take(MaxTableRows).ToList() : _tableBuf;
        var rows = visible.Select(SplitCells).ToList();
        var cols = rows.Max(r => r.Count);
        var widths = new int[cols];
        foreach (var r in rows)
            for (int i = 0; i < r.Count; i++)
                widths[i] = Math.Max(widths[i], DisplayWidth(r[i]));
        foreach (var r in rows)
        {
            // 分隔行判定需精确：Markdown 分隔行单元格形如 ---、:---、---:、:---:
            // （可选冒号 + 至少 3 个 -），否则单字符数据行（如 - 或 :）会被误判为分隔行而丢失
            var isSep = r.Count > 0 && r.All(c => SepRe.IsMatch(c));
            var cells = new string[r.Count];
            for (int i = 0; i < r.Count; i++)
                cells[i] = isSep ? new string('─', widths[i]) : PadToWidth(r[i], widths[i]);
            Console.WriteLine("  " + string.Join(" │ ", cells));
        }
        if (truncated)
            Console.WriteLine($"  …(表格共 {_tableBuf.Count} 行，仅显示前 {MaxTableRows} 行)");
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

    /// <summary>显示宽度：CJK/全角字符按 2 列计算；emoji 等代理对按 2 列（两个 surrogate 只算一次）。</summary>
    private static int DisplayWidth(string s)
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
                w += c > 0x2E7F ? 2 : 1;
            }
        }
        return w;
    }
}
