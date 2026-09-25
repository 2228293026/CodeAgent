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
    private bool _inInlineCode; // 是否在行内代码 `` `...` `` 内
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
        // 流式边界：若上一个 chunk 停在语言标识符行且代码缓冲为空，
        // 说明标识符已被截断——结束 intro 状态，让本 chunk 的内容进入 _code
        if (_inCode && _skipCodeIntro && _code.Length == 0 && text.Length > 0 && text[0] != '\n')
            _skipCodeIntro = false;
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
        _inInlineCode = false; // 流结束时丢弃未闭合的行内代码状态
        _skipCodeIntro = false; // 刷新时重置：避免跨 Append 调用丢弃有效代码内容
        _tickRun = 0; // 反引号连续计数不能跨消息：否则上一条消息结尾的 ` 会和下一条开头的 ` 凑成围栏/行内代码
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
                // 围栏开始：先冲刷缓冲中的表格（表格后紧跟代码块时表格应先输出——
                // EmitLine 不带表格冲刷，漏了会把表格渲染到代码块之后甚至丢失），进入代码模式
                FlushTable();
                _line.Clear(); // 不输出围栏分隔符本身（``` 是 Markdown 语法，不是内容）
                _inCode = true;
                _tickRun = 0;
                _skipCodeIntro = true; // 丢弃本行剩余的语言标注（```cs）
                return;
            }
            if (_tickRun == 2 && !_inInlineCode)
            {
                // 两个反引号进入行内代码：不删 _line 内容（避免影响后续围栏前缀计算），
                // 仅切换状态，让 EmitLine/ParseInline 负责剥掉反引号对。
                _inInlineCode = true;
            }
            else if (_tickRun == 2 && _inInlineCode)
            {
                // 行内代码闭合：两个连续 ` 作为闭合标记，恢复状态以便后续字符正常处理
                _inInlineCode = false;
                _tickRun = 0;
            }
            _line.Append(ch);
            return;
        }
        _tickRun = 0;
        if (ch == '\r')
            return; // 跳过 CRLF 的 \r，避免混入普通文本（终端会把 \r 当回车覆盖渲染）
        if (_inInlineCode && ch == '\n')
        {
            // 未闭合的行内代码到行尾：原样输出（含开头的 `），恢复状态继续
            _inInlineCode = false;
        }
        if (ch == '\n')
        {
            _line.Append(ch);
            var line = _line.ToString();
            _line.Clear();
            // 反引号连续计数不能跨行：行尾单个 ` 与下一行行首 ` 不是行内代码对
            _tickRun = 0;
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
                // 围栏结束：移除末尾属于分隔符的两个反引号（避免 ``` 泄漏进代码内容）
                if (_code.Length >= 2)
                    _code.Length -= 2;
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
        // ATX 标题的闭合序列（## 标题 ##）是 Markdown 语法，不该原样出现在渲染结果里
        if (HeadingLevel(content) > 0)
            content = StripClosingHashes(content);
        else if (BlockquoteLevel(content) > 0)
            content = NormalizeBlockquote(content);
        else
            content = NormalizeListMarker(content) ?? content; // 列表标记统一，正文不变
        var color = ResolveLineColor(content);
        var parts = ParseInline(content);

        // 无任何样式且没有任何标记被剥离：原样输出（保留换行）。
        // 必须比较「剥离标记后的纯文本」与原行是否一致：整行就是一个行内代码时，
        // 解析结果会塌成单个 Normal 段，若只看样式就会误走快速路径，把 ` 标记漏回屏幕。
        var plain = string.Concat(parts.Select(p => p.text));
        if (color is null && parts.Count <= 1 && parts.All(p => p.style == InlineStyleToken.Normal) && plain == content)
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

    /// <summary>
    /// ATX 标题级别（CommonMark 规则），非标题返回 0：
    /// 最多 3 个前导空格（4 个及以上是代码块）；1–6 个 #；其后必须是空格/Tab 或行尾——
    /// `#Title` 不是标题（无空格），旧实现只看首字符，会把井号开头的普通文本当成标题上色。
    /// </summary>
    internal static int HeadingLevel(string line)
    {
        var i = 0;
        var spaces = 0;
        while (i < line.Length && line[i] == ' ' && spaces < 4)
        {
            i++;
            spaces++;
        }
        if (spaces > 3)
            return 0;
        var hashes = 0;
        while (i < line.Length && line[i] == '#' && hashes < 7)
        {
            i++;
            hashes++;
        }
        if (hashes == 0 || hashes > 6)
            return 0;
        if (i < line.Length && line[i] != ' ' && line[i] != '\t')
            return 0; // #Title / ###foo：井号后无空格，不是标题
        return hashes;
    }

    /// <summary>去掉标题末尾的闭合 # 序列（序列前必须有空格或行首才是闭合标记）：「## 标题 ##」→「## 标题」。</summary>
    internal static string StripClosingHashes(string line)
    {
        var end = line.Length;
        while (end > 0 && line[end - 1] == '#')
            end--;
        if (end == line.Length || end == 0)
            return line; // 无闭合序列
        if (line[end - 1] != ' ' && line[end - 1] != '\t')
            return line; // 闭合标记前必须空白；「a#b#」不算
        return line[..end].TrimEnd();
    }

    /// <summary>块引用层级（CommonMark：最多 3 个前导空格，`>` 后可跟一个空格），非引用返回 0。
    /// 旧实现只看首字符 `>`：`  > 内容`（合法引用，带前导空格）不上色，识别漏判。</summary>
    internal static int BlockquoteLevel(string line)
    {
        var i = 0;
        var spaces = 0;
        while (i < line.Length && line[i] == ' ' && spaces < 4)
        {
            i++;
            spaces++;
        }
        if (spaces > 3)
            return 0;
        var level = 0;
        while (i < line.Length && line[i] == '>')
        {
            level++;
            i++;
            if (i < line.Length && line[i] == ' ')
                i++; // `>` 后至多吞一个空格（`>>x` 是两层引用）
        }
        return level;
    }

    /// <summary>把块引用标记规范为每层「&gt; 」：`&gt;内容` / `  &gt;&gt;  x` → `&gt; &gt; x`，正文原样保留。</summary>
    internal static string NormalizeBlockquote(string line)
    {
        var level = BlockquoteLevel(line);
        if (level == 0)
            return line;
        var i = 0;
        var spaces = 0;
        while (i < line.Length && line[i] == ' ' && spaces < 3)
        {
            i++;
            spaces++;
        }
        var used = 0;
        while (i < line.Length && line[i] == '>' && used < level)
        {
            i++;
            used++;
            if (i < line.Length && line[i] == ' ')
                i++;
        }
        return string.Concat(Enumerable.Repeat("> ", level)) + line[i..];
    }

    /// <summary>列表标记规范：无序 `- * +` 统一为 `-`，有序 `1)` 统一为 `1.`，标记后恰好一个空格。
    /// 返回 null 表示不是列表行（保持原样）。</summary>
    internal static string? NormalizeListMarker(string line)
    {
        var indent = 0;
        while (indent < line.Length && line[indent] == ' ')
            indent++;
        if (indent > 3)
            return null; // 4 个以上空格是代码块，不是列表
        var rest = line[indent..];
        if (rest.Length == 0)
            return null;
        var markerLen = 0;
        string marker;
        if (rest[0] is '-' or '*' or '+')
        {
            marker = "-";
            markerLen = 1;
        }
        else
        {
            var digits = 0;
            while (digits < rest.Length && digits < 9 && char.IsAsciiDigit(rest[digits]))
                digits++;
            if (digits == 0 || digits + 1 >= rest.Length || (rest[digits] != '.' && rest[digits] != ')'))
                return null;
            marker = rest[..digits] + ".";
            markerLen = digits + 1;
        }
        // 标记后必须至少留一个空格或直接到行尾，否则不是列表项（如 "-5 是负数"）
        var after = rest[markerLen..];
        if (after.Length > 0 && after[0] != ' ' && after[0] != '\t')
            return null;
        return line[..indent] + marker + " " + after.TrimStart(' ', '\t');
    }

    private static ConsoleColor? ResolveLineColor(string content)
    {
        var heading = HeadingLevel(content);
        if (heading > 0)
            return heading switch // 标题按层级区分色深，一眼看出结构层级
            {
                1 => ConsoleColor.Cyan,
                2 => ConsoleColor.Blue,
                _ => ConsoleColor.DarkCyan,
            };
        if (content.StartsWith("---") || content.StartsWith("==="))
            return ConsoleColor.DarkGray; // 分隔线
        if (BlockquoteLevel(content) > 0)
            return ConsoleColor.DarkGray; // 引用
        return null;
    }

    /// <summary>代码块内的制表位宽度（终端默认 8，代码缩进按 4 更可读且对齐稳定）。</summary>
    internal const int CodeTabWidth = 4;

    /// <summary>把一行里的 Tab 展开到 <paramref name="tabWidth"/> 列制表位（按显示列而非字符数推进）。
    /// 代码里的制表符在终端按 8 列渲染，缩进层级看起来参差；展开后同一层级始终对齐。</summary>
    internal static string ExpandCodeTabs(string line, int tabWidth = CodeTabWidth)
    {
        if (line.IndexOf('\t') < 0)
            return line;
        var sb = new StringBuilder();
        var col = 0;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\t')
            {
                var pad = tabWidth - col % tabWidth;
                sb.Append(' ', pad);
                col += pad;
            }
            else if (char.IsHighSurrogate(c) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]))
            {
                sb.Append(c).Append(line[i + 1]); // emoji 占 2 列，整对推进
                col += 2;
                i++;
            }
            else
            {
                sb.Append(c);
                col += !char.IsSurrogate(c) && c > 0x2E7F ? 2 : 1; // CJK/全角与 DisplayWidth 同口径
            }
        }
        return sb.ToString();
    }

    /// <summary>代码块规整：Tab 展开、去掉行尾空白、CRLF 归一为 LF。
    /// 行首缩进原样保留（那是代码语义），只清理不可见噪音——行尾空白会让复制粘贴带上脏字符、
    /// 撑长终端回滚缓冲，行尾 Tab 还会让"选中到行尾"高亮出多余空白。</summary>
    internal static string NormalizeCodeBlock(string code)
    {
        var lines = code.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                sb.Append('\n');
            sb.Append(ExpandCodeTabs(lines[i]).TrimEnd(' '));
        }
        return sb.ToString();
    }

    private void EmitCode(string code)
    {
        if (code.Length == 0)
            return;
        SafeColor.Foreground(ConsoleColor.Green);
        Console.Write(NormalizeCodeBlock(code));
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

    /// <summary>单元格最大显示宽度，超出截断加省略号（base64/长 URL 等会把其他列挤出终端折行破版）。</summary>
    private const int MaxTableColWidth = 40;

    /// <summary>表格列宽收缩下限：低于此值内容已不可读，宁可让表格换行也不继续削。</summary>
    internal const int MinTableColWidth = 3;

    /// <summary>表格总宽预算下的收敛：先夹到单列上限，再对最宽的一批列削峰，直到总宽不超过 budget。
    /// 总宽 = 缩进 2 + Σ列宽 + 3 × (列数-1)（" │ " 分隔）。
    /// 旧实现只限单列 40 列：宽表在窄终端上仍会整行折行，列对齐彻底看不出对应关系。
    /// budget &lt;= 0 表示不限（宽度未知或重定向）。</summary>
    internal static int[] FitColumnWidths(int[] widths, int budget, int indent = 2, int separatorWidth = 3)
    {
        var result = (int[])widths.Clone();
        if (budget <= 0 || result.Length == 0)
            return result;
        int Total()
        {
            var sum = 0;
            foreach (var w in result)
                sum += w;
            return indent + sum + separatorWidth * (result.Length - 1);
        }
        while (Total() > budget)
        {
            var max = 0;
            foreach (var w in result)
                if (w > max)
                    max = w;
            if (max <= MinTableColWidth)
                break; // 已削到不可读，再削没有意义
            var peak = 0;
            foreach (var w in result)
                if (w == max)
                    peak++;
            var cut = Math.Min(max - MinTableColWidth, Math.Max(1, (int)Math.Ceiling((Total() - budget) / (double)peak)));
            for (var i = 0; i < result.Length; i++)
                if (result[i] == max)
                    result[i] -= cut;
        }
        return result;
    }

    /// <summary>表格可用总宽：终端宽度（含缩进）；不可用时返回 0 = 不限。</summary>
    private static int TableBudget()
    {
        if (Console.IsOutputRedirected)
            return 0;
        try { return Math.Clamp(Console.WindowWidth, 0, 400); } catch { return 0; }
    }

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
        var natural = new int[cols];
        foreach (var r in rows)
            for (int i = 0; i < r.Count; i++)
                natural[i] = Math.Min(MaxTableColWidth, Math.Max(natural[i], DisplayWidth(r[i])));
        // 窄终端下收敛总宽（削最宽的一批列），否则宽表整行折行、列对应关系全丢
        var widths = FitColumnWidths(natural, TableBudget());
        foreach (var r in rows)
        {
            // 分隔行判定需精确：Markdown 分隔行单元格形如 ---、:---、---:、:---:
            // （可选冒号 + 至少 3 个 -），否则单字符数据行（如 - 或 :）会被误判为分隔行而丢失
            var isSep = r.Count > 0 && r.All(c => SepRe.IsMatch(c));
            var cells = new string[r.Count];
            for (int i = 0; i < r.Count; i++)
            {
                // 超宽单元格先截断到列宽（FitToWidth 代理对安全并补省略号），再补空格对齐
                var cell = InputLine.FitToWidth(r[i], widths[i]);
                cells[i] = isSep ? new string('─', widths[i]) : PadToWidth(cell, widths[i]);
            }
            Console.WriteLine("  " + string.Join(" │ ", cells));
        }
        if (truncated)
            Console.WriteLine($"  …(表格共 {_tableBuf.Count} 行，仅显示前 {MaxTableRows} 行)");
        _tableBuf.Clear();
    }

    private static List<string> SplitCells(string row)
    {
        var s = row.Trim();
        if (s.StartsWith('|'))
            s = s[1..];
        // 末尾 | 可选；只有未被反斜杠转义的才是外层分隔符。
        if (s.EndsWith('|') && !IsEscapedPipe(s, s.Length - 1))
            s = s[..^1];

        // \| 是表格内的字面竖线；连续反斜杠按奇偶判定：奇数转义，偶数后的 | 仍为分隔符。
        var cells = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '|' && IsEscapedPipe(s, i))
            {
                if (sb.Length > 0 && sb[^1] == '\\') sb.Length--; // 消费一个转义反斜杠
                sb.Append('|');
            }
            else if (s[i] == '|')
            {
                cells.Add(sb.ToString().Trim());
                sb.Clear();
            }
            else
            {
                sb.Append(s[i]);
            }
        }
        cells.Add(sb.ToString().Trim());
        return cells;

        static bool IsEscapedPipe(string text, int pipeIndex)
        {
            var backslashes = 0;
            for (var i = pipeIndex - 1; i >= 0 && text[i] == '\\'; i--)
                backslashes++;
            return (backslashes & 1) != 0;
        }
    }

    private static string PadToWidth(string s, int width)
    {
        var pad = width - DisplayWidth(s);
        return pad > 0 ? s + new string(' ', pad) : s;
    }

    /// <summary>显示宽度：CJK/全角字符按 2 列计算；emoji 等代理对按 2 列（两个 surrogate 只算一次）。</summary>
    private static int DisplayWidth(string s) => TextUtil.DisplayWidth(s);
}
