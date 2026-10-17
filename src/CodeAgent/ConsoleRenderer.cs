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
    private readonly System.Text.StringBuilder _codeLang = new(); // 围栏语言标注（```cs 的 cs）
    private const int MaxCodeLangChars = 24; // 标注本身的长度上限：再长就不是语言名了
    private bool _skipCodeIntro; // 围栏起始行的语言标注（如 ```cs 的 cs）应丢弃

    /// <summary>终端列数（0 = 未知）。列表项/块引用按此宽度折行并加悬挂缩进；
    /// 未知时保持原样，交回终端软换行。</summary>
    private int _width;

    public ConsoleRenderer(bool enabled) => _enabled = enabled;

    /// <summary>设置折行宽度（0 = 不折行）。</summary>
    public void SetWidth(int width) => _width = width;

    /// <summary>流式「逐字长出来」的原地重绘。**默认关闭**：关闭时输出与旧版逐字节相同
    /// （测试、重定向、管道全走这条路）；只有真终端上的 Agent 才打开。
    ///
    /// 背景：<see cref="Append"/> 把字符攒进行缓冲，**凑齐一整行才写出去**。模型是逐字
    /// 返回的，于是屏幕一直不动，直到整行结束才「啪」地蹦出一整行——最基础的字符动态都没有。
    /// 打开后未完成的行以预览形式原地重绘，每来一个字就覆盖上一帧；收行时清掉预览、
    /// 再按最终样式整行输出一次（所以预览永远不会成为正式内容的一部分）。
    ///
    /// 预览**必须不折行**：清行只用 <c>\r</c> + ESC[2K，只作用一行，一次折行就会留下
    /// 擦不掉的残字。因此宽度未知（&lt;= 0）时不预览。
    /// </summary>
    public bool LivePreview { get; set; }

    /// <summary>当前光标行上是否正显示着预览（决定要不要先擦掉）。</summary>
    private bool _previewShown;

    /// <summary>能否画预览：开关打开、宽度已知（宽度未知时无法保证不折行），
    /// 且光标**在行首**。
    ///
    /// 最后一条是「擦掉别人的内容」的守卫：<see cref="Flush"/> 会把不以换行结尾的半行
    /// 原样写出去，光标停在行中；此时画预览，下一帧的 <c>\r</c> + ESC[2K 会把刚写出去的
    /// **正式内容**一起擦掉——用户会看到答案的最后半句凭空消失。
    /// 只在行首重绘才安全：那一行上除了预览没有别人的东西。</summary>
    private bool CanPreview => LivePreview && _width > 0 && _lastEmitEndedWithNewline;

    /// <summary>是否该启用逐字预览（纯函数，好测——与 <c>Agent.ShouldShowSpinner</c> 同一手法）。
    ///
    /// 三个前提缺一不可：
    /// <list type="bullet">
    /// <item>Markdown 渲染开着：关掉时 <see cref="Append"/> 直接 <c>Console.Write</c>，本来就逐字；</item>
    /// <item>输出**不是**重定向：<c>\r</c> + ESC[2K 对管道无意义，帧会一路堆进文件；</item>
    /// <item>TUI ANSI 开着：用户关掉 ANSI 说明终端不吃控制序列，原地重绘会变成一屏残帧。</item>
    /// </list>
    ///
    /// 抽成纯函数是因为 <c>Console.IsOutputRedirected</c> 在测试宿主里**恒为 true**，
    /// 端到端测试永远走不到预览路径——不抽出来，这条判定的真值表就没有任何测试能碰。
    /// </summary>
    internal static bool ShouldLivePreview(bool renderMarkdown, bool tuiAnsi, bool outputRedirected) =>
        renderMarkdown && tuiAnsi && !outputRedirected;

    /// <summary>擦掉正在显示的预览行。幂等——没有预览时一个字节都不写。
    /// <c>\r</c> 先把光标拉回行首，ESC[2K 再清空整行：只写其一都会把上一帧的尾巴留在屏幕上。</summary>
    private void ClearPreview()
    {
        if (!_previewShown)
            return;
        _previewShown = false;
        Console.Write("\r\x1b[2K");
    }

    /// <summary>所有正式输出的唯一出口：写之前先擦预览。
    /// 收口成一处是因为「哪条路径忘了擦」的后果是残字留在屏幕上，而这类遗漏
    /// 在代码块、表格、折行、徽标、提示行上各有一条独立的写路径。</summary>
    private void Write(string text)
    {
        ClearPreview();
        Console.Write(text);
        // 光标位置是预览的安全前提（见 CanPreview）：每次正式写入都要如实记账，
        // 否则一次「不换行收尾」会让后面的预览永久失效，或者更糟——照常预览、把正式内容擦掉。
        if (text.Length > 0)
            _lastEmitEndedWithNewline = text[^1] == '\n';
    }

    private void WriteLine()
    {
        ClearPreview();
        Console.WriteLine();
        _lastEmitEndedWithNewline = true;
    }

    private void WriteLine(string text)
    {
        ClearPreview();
        Console.WriteLine(text);
        _lastEmitEndedWithNewline = true;
    }

    /// <summary>把当前半行以**预览**形式画在光标行上（原地重绘上一帧）。
    ///
    /// 只有「还停在行中」时才画：整行已经在 <see cref="EmitLine"/> 里落地了再画一遍，
    /// 屏幕上就会出现两份同一行内容。
    ///
    /// 预览走**纯文本**而不是 Markdown 解析：这是还没写完的一句话，
    /// `**加粗` 此刻只有一个星号，解析它只会得到「闪来闪去的样式」。
    /// 收行时正式输出会按完整语法重新渲染，预览的斤两只有「让字先长出来」。
    /// </summary>
    private void PaintPreview()
    {
        if (!CanPreview)
            return;
        var partial = _line.ToString();
        if (partial.Length == 0)
        {
            ClearPreview();
            return;
        }
        // 换行已经在 HandleTextChar 里被消费掉了，这里的 _line 一定是半行；
        // 但防御性地再剥一次 \r / \n：把带换行的内容画成预览会把光标推下去，
        // 之后再清行只会清掉最后一行的开头。
        var text = partial.Replace("\r", "").Replace("\n", " ");
        // 预览**不能折行**：清行只作用一行。超宽部分截掉，剩余内容在正式收行时完整出现。
        // 预留 1 列给光标：正好占满整行时部分终端的自动换行会把光标推到下一行。
        var budget = _width - 1;
        if (budget <= 0)
            return;
        text = PreviewPlainText(text);
        text = TextUtil.TakeDisplayWidth(text, budget);
        ClearPreview();
        Console.Write(text);
        _previewShown = true;
    }

    /// <summary>取半行文本在**收行后会显示成什么**（只取文字，不带样式）。
    ///
    /// 预览是逐字长出来的：`**加粗` 在闭合的 `**` 到达之前只是一个开标记，
    /// 原样画出去屏幕上会先闪出 `**加粗`、收行时再跳成 `加粗`——每次加粗都闪一下。
    ///
    /// 复用 <see cref="ParseInline"/> 而不是自己数标记：它本来就把未闭合的开标记
    /// 当样式开关消费掉（`**` 翻转样式、结尾不补回来），得到的结果与最终渲染同一套规则。
    /// 各写一份的后果是预览和正式输出对不上——预览显示 `**x`，收行后变成 `x`。
    /// </summary>
    internal static string PreviewPlainText(string text) =>
        string.Concat(ParseInline(text).Select(p => p.text));

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
        // 整段喂完后才画一次预览：逐字符重绘会在一次 Append 里画出几十帧中间状态，
        // 白白闪屏。Append 的入参已经是模型一次吐出的一个增量。
        if (_inCode)
            PaintCodePreview();
        else
            PaintPreview();
    }

    /// <summary>结束输出：刷出剩余缓冲。</summary>
    /// <summary>流结束时是否停在行边界（没有未输出的半行，也没有待冲刷的空行）。
    /// 调用方据此决定要不要补换行：模型最后一段已经带换行时再补一个会多出空行；
    /// 没有结束时必须补，否则后续的工具状态行/摘要行会粘在正文同一行。</summary>
    /// <summary>最近一次真正写出去的内容**是否以换行收尾**。
    /// 与 <see cref="_line"/> 的缓冲状态分开记：<see cref="Flush"/> 会把缓冲清空，
    /// 但模型最后一段不带 \n 时写出去的就是半行，光标停在行中——
    /// 缓冲看着"干净"，光标其实没在行尾。混为一谈会让调用方以为不用补换行。</summary>
    private bool _lastEmitEndedWithNewline = true;

    public bool EndsOnLineBoundary =>
        !_inCode && _line.Length == 0 && _pendingBlanks == 0 && _lastEmitEndedWithNewline;

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
            _codeLang.Clear(); // 语言标注只属于当前代码块，用完即弃（否则下一块会带上上一块的语言）
            _code.Clear();
        }
        else if (_line.Length > 0)
        {
            _codeLang.Clear(); // 非代码路径也要清：上一块残留的语言不能漏到下一个代码块
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
                EmitContentLine(line);
            }
        }
        else
        {
            _codeLang.Clear();
            FlushTable();
        }
        FlushPendingBlanks();
    }

    /// <summary>连续空行最多保留的行数。模型常连发 3–5 个空行：既占屏又把段落节奏切碎。</summary>
    internal const int MaxBlankRun = 1;

    private int _pendingBlanks;

    /// <summary>内容行输出：空行先暂存，连续空行按 MaxBlankRun 收敛后再随下一行一起输出。
    /// 暂存而非立即输出是必要的——流式渲染必须等下一行才知道前面是不是"连发空行"。</summary>
    private void EmitContentLine(string line)
    {
        // 入参仍带行尾 \n（EmitLine 自己会剥离），这里按内容判定是否空行
        if (line.TrimEnd('\n').Length == 0)
        {
            _pendingBlanks++;
            return;
        }
        FlushPendingBlanks();
        EmitLine(line);
    }

    private void FlushPendingBlanks()
    {
        var blanks = Math.Min(_pendingBlanks, MaxBlankRun);
        for (var i = 0; i < blanks; i++)
            WriteLine();
        _pendingBlanks = 0;
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
                FlushPendingBlanks();
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
            EmitContentLine(line);
        }
        else
        {
            _line.Append(ch);
        }
    }

    private void HandleCodeChar(char ch)
    {
        // 围栏起始行（```cs 的 cs）：取出语言标注后丢弃该行剩余内容，避免它混入代码
        if (_skipCodeIntro)
        {
            if (ch == '\n')
            {
                _skipCodeIntro = false;
                // 语言标注到此收齐：边界先画出来，后面的代码行才有地方落
                EmitCodeHeader();
                return;
            }
            // 语言标注此前被直接丢弃——读者只能靠猜。现在留下来单独成行。
            if (ch != '\r' && _codeLang.Length < MaxCodeLangChars)
                _codeLang.Append(ch);
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
                EmitCodeHeader();      // 空代码块（```\n``` ）：边界一次都还没画过
                EmitCodeLine(_code.ToString());
                EmitCodeFooter();
                _codeLang.Clear(); // 围栏一关就要清：同一次 Append 里可能有多个代码块，
                                   // 只在 Flush 清会让第二块带上第一块的语言（▌csjson）
                _code.Clear();
                _inCode = false;
                _tickRun = 0;
                return;
            }
            _code.Append(ch);
            return;
        }
        _tickRun = 0;
        if (ch == '\n')
        {
            // 一整行代码到齐：**立刻**输出。
            //
            // 此前整块攒到围栏关闭才吐，于是屏幕上代码块是「等半天，然后整块蹦出来」——
            // 生成 60 行代码时用户前 59 行都看不到任何动静，与逐字的正文完全是两种体验。
            // 行尾 \n 没有进 _code（它正是本分支的触发条件），必须显式补回：
            // 少了它，代码行会粘成一行，代码块里的空行也会整个消失。
            var codeLine = _code.ToString() + "\n";
            _code.Clear();
            EmitCodeHeader(); // 幂等：正常路径已在围栏行画过
            EmitCodeLine(codeLine);
            return;
        }
        _code.Append(ch);
    }

    /// <summary>把代码块里的**半行**也画成预览：正文能逐字长出来、代码却要等整行到齐，
    /// 长行（base64、长路径）在屏幕上就是「卡一下，然后跳出整行」。
    ///
    /// 与正文预览分开成两个函数，是因为两者取缓冲的位置不同（_line 与 _code），
    /// 且代码行不能做 Markdown 剥标记——代码里的 `*` 是乘号，剥掉就是改内容。
    /// </summary>
    private void PaintCodePreview()
    {
        if (!CanPreview || !_codeHeaderDrawn)
            return;
        var partial = _code.ToString();
        if (partial.Length == 0)
        {
            ClearPreview();
            return;
        }
        var text = partial.Replace("\r", "").Replace("\n", " ");
        var budget = _width - 1;
        if (budget <= 0)
            return;
        // Tab 展开后再算宽度：代码里的 Tab 在终端占 4 列，按 1 列算会画超、进而折行。
        text = TextUtil.TakeDisplayWidth(ExpandCodeTabs(text), budget);
        ClearPreview();
        using (SafeColor.Scope(SafeColor.Success))
            Console.Write(text);
        _previewShown = true;
    }

    /// <summary>列表项 / 块引用的悬挂缩进：续行对齐到正文起始列。
    /// 没有它时终端软换行会让续行顶到行首，视觉上变成新段落，
    /// 读者看不出「这一行还是上一条列表项的内容」。</summary>
    internal static string HangingIndentFor(string content)
    {
        var quote = BlockquoteLevel(content);
        if (quote > 0)
            return string.Concat(Enumerable.Repeat("> ", quote));
        var bullet = System.Text.RegularExpressions.Regex.Match(content, @"^(\s*(?:[-*+]|\d+[.)])\s)");
        return bullet.Success ? bullet.Groups[1].Value : "";
    }

    private void EmitLine(string line)
    {
        // 正式行落地前必须擦掉预览：否则最终样式会接在预览尾巴后面，
        // 屏幕上留下「预览 + 正式」两份同一行内容。
        ClearPreview();
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

        // 列表项/块引用的正文按显示宽度折行并加悬挂缩进：续行仍与首行同属一条内容。
        // WrapDisplay 会把缩进加到**每一行**（含首行），所以传入的必须是去掉标记的正文，
        // 否则首行会出现 "- - 内容" 这样的重复标记。
        // 宽度未知（0）或不够折行时保持原样，交回终端软换行。
        var hanging = HangingIndentFor(content);
        var hangingCols = hanging.Length > 0 ? TextUtil.DisplayWidth(hanging) : 0;
        // 判定必须同单位：_width 是**列数**，hanging.Length 是**字符数**。
        // `- ` / `> ` 恰好两者相等所以一直没暴露，但全角空格缩进的列表项
        // （`　- x`）标记是 3 字符 / 4 列，4 > 3 会误判成"折得下"，
        // 于是 budget = 0，一条 14 列的内容被塞进 4 列终端。
        if (hangingCols > 0 && _width > hangingCols)
        {
            var body = content[hanging.Length..]; // 子串下标只能按字符数切
            // WrapDisplay 的 width 是正文预算：这里要扣掉每行都要加的悬挂缩进
            var budget = _width - hangingCols;
            var wrapped = TextUtil.WrapDisplay(body, budget, hanging);
            var lines = DiffUtil.SplitLines(wrapped, CancellationToken.None);
            if (lines.Length > 1)
            {
                for (var i = 0; i < lines.Length; i++)
                {
                    // 换行标志必须给**除末段外的每一段**，不是只给首段。
                    // 快速路径按这个标志决定要不要写 \n；原实现只传首段，
                    // 于是「三段以上」的列表项/引用从第 2 段起就没有换行，
                    // 被粘成 "- 中文- 文" 这样的残行——内容被拼错，用户只看到一句乱码。
                    EmitStyledLine(lines[i], color, i < lines.Length - 1 || hadNewline);
                }
                // 折行时只有**末段**决定光标位置；末段没带换行，光标就仍停在行中
                _lastEmitEndedWithNewline = hadNewline;
                return;
            }
        }

        // 无任何样式且没有任何标记被剥离：原样输出（保留换行）。
        // 必须比较「剥离标记后的纯文本」与原行是否一致：整行就是一个行内代码时，
        // 解析结果会塌成单个 Normal 段，若只看样式就会误走快速路径，把 ` 标记漏回屏幕。
        var plain = string.Concat(parts.Select(p => p.text));
        if (color is null && parts.Count <= 1 && parts.All(p => p.style == InlineStyleToken.Normal) && plain == content)
        {
            Write(content + (hadNewline ? "\n" : ""));
            _lastEmitEndedWithNewline = hadNewline;
            return;
        }

        // 行级作用域：段落自己也会开作用域（见 EmitStyledParts），这里兜住
        //「段落已输出、还没换行」这段窗口，并保证 EmitStyledParts 抛异常时颜色仍复位
        // ——此前那个裸 SafeColor.Reset() 恰恰在异常路径上到不了。
        using (SafeColor.Scope(color ?? SafeColor.Muted))
        {
            EmitStyledParts(parts, color);
            Console.WriteLine();
        }
        // 这一分支总是换行收尾
        _lastEmitEndedWithNewline = true;
    }

    /// <summary>输出一行带样式的内容（行内代码 / 标题 / 引用配色）。</summary>
    private void EmitStyledLine(string content, ConsoleColor? color, bool hadNewline)
    {
        var parts = ParseInline(content);
        var plain = string.Concat(parts.Select(p => p.text));
        if (color is null && parts.Count <= 1 && parts.All(p => p.style == InlineStyleToken.Normal) && plain == content)
        {
            Write(content + (hadNewline ? "\n" : ""));
            return;
        }
        using (SafeColor.Scope(color ?? SafeColor.Muted))
        {
            EmitStyledParts(parts, color);
            Console.WriteLine();
        }
    }

    private void EmitStyledParts(List<(string text, InlineStyleToken style)> parts, ConsoleColor? color)
    {
        // 颜色可用时按颜色区分；**不可用时颜色是唯一的信息载体**，
        // 于是行内代码与加粗会彻底看不出区别——信息被丢掉了，而不只是样式没了。
        // 关闭颜色时改为还原 Markdown 标记本身：这是用户当初写的原文，
        // 不是凭空加的噪声，比「一段代码和一段散文长得一模一样」好得多。
        if (!SafeColor.Enabled)
        {
            foreach (var (text, style) in parts)
                Console.Write(style switch
                {
                    InlineStyleToken.Code => "`" + text + "`",
                    InlineStyleToken.Bold => "*" + text + "*",
                    _ => text,
                });
            return;
        }
        foreach (var (text, style) in parts)
        {
            using var scope = SafeColor.Scope(style switch
            {
                InlineStyleToken.Bold => SafeColor.Emphasis,
                InlineStyleToken.Code => SafeColor.Warning,
                _ => color ?? SafeColor.Muted,
            });
            Console.Write(text);
        }
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
                1 => SafeColor.Accent,
                2 => SafeColor.Link,
                _ => SafeColor.Accent,
            };
        if (content.StartsWith("---") || content.StartsWith("==="))
            return SafeColor.Muted; // 分隔线
        if (BlockquoteLevel(content) > 0)
            return SafeColor.Muted; // 引用
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

    /// <summary>代码块中显示宽度超过终端的行数。
    /// 代码**不截断**（截断会让用户复制到的是残缺代码，比多占几列糟糕得多），
    /// 但折行后的续行看起来和下一条语句一模一样，读者分不出「这是一行的后半段」。
    /// 因此改为整块统计一次，块后用一行弱化提示说明有几行发生了折行。</summary>
    internal static int CountOverlongCodeLines(string code, int width)
    {
        if (width <= 0 || string.IsNullOrEmpty(code))
            return 0;
        var count = 0;
        foreach (var line in code.Replace("\r\n", "\n").Split('\n'))
        {
            if (TextUtil.DisplayWidth(ExpandCodeTabs(line)) > width)
                count++;
        }
        return count;
    }

    /// <summary>代码块折行提示行（无超长行时返回空串）。</summary>
    internal static string CodeWrapNote(int overlong, int width) =>
        overlong <= 0
            ? string.Empty
            : $"（代码块有 {overlong} 行超过 {width} 列，已折行显示；代码本身未截断）";

    /// <summary>围栏语言徽标：`  ▌cs`（弱化的一行，标出下面这段代码是什么语言）。
    /// 语言标注此前被直接丢弃，读者只能靠猜。宽度未知或放不下时返回空串——
    /// 宁可不标，也不能让一行徽标自己折行、把代码块顶歪。</summary>
    internal static string FormatCodeLangBadge(string lang, int width)
    {
        var text = lang.Trim();
        if (text.Length == 0)
            return string.Empty;
        // 标注里不该有空格（`cs extra` 不是语言名）：只取第一段，避免徽标变成整行废话
        var space = text.IndexOf(' ', StringComparison.Ordinal);
        if (space > 0)
            text = text[..space];
        if (text.Length == 0)
            return string.Empty;
        var badge = "  " + SafeColor.Glyphs.Badge + InputLine.FitToWidth(text, MaxCodeLangChars);
        return width > 0 && TextUtil.DisplayWidth(badge) > width ? string.Empty : badge;
    }

    /// <summary>代码块的边界（开围栏 / 语言徽标）是否已经画过。
    /// 逐行输出时边界只能画一次：每行都画会变成几十个徽标。</summary>
    private bool _codeHeaderDrawn;

    /// <summary>本块已输出的代码行里，显示宽度超过终端的行数（块尾一次性交代）。</summary>
    private int _codeOverlongLines;

    /// <summary>画代码块的开头（围栏行 / 语言徽标）。幂等：正常路径在围栏行画一次，
    /// 空代码块与「围栏行没收到换行」的边缘路径靠这个幂等性兜住。</summary>
    private void EmitCodeHeader()
    {
        if (_codeHeaderDrawn)
            return;
        _codeHeaderDrawn = true;
        _codeOverlongLines = 0;
        var lang = _codeLang.ToString();
        var fence = !SafeColor.Enabled;
        if (fence)
        {
            using var openScope = SafeColor.Scope(SafeColor.Muted);
            WriteLine("```" + InputLine.FitToWidth(lang, MaxCodeLangChars));
        }
        else
        {
            var badge = FormatCodeLangBadge(lang, _width);
            if (badge.Length > 0)
            {
                using var badgeScope = SafeColor.Scope(SafeColor.Muted);
                WriteLine(badge);
            }
        }
    }

    /// <summary>输出一行代码（已含行尾 \n）。逐行落地而不是整块攒着，
    /// 是让代码和正文一样**随着模型生成一行行长出来**。</summary>
    private void EmitCodeLine(string codeLine)
    {
        var normalized = NormalizeCodeBlock(codeLine);
        if (normalized.Length > 0 && _width > 0 && TextUtil.DisplayWidth(normalized) > _width)
            _codeOverlongLines++;
        using (SafeColor.Scope(SafeColor.Success))
            Write(normalized);
    }

    /// <summary>画代码块的收尾（关围栏 + 折行交代）。</summary>
    private void EmitCodeFooter()
    {
        if (!SafeColor.Enabled)
        {
            using var closeScope = SafeColor.Scope(SafeColor.Muted);
            WriteLine("```");
        }
        if (_codeOverlongLines > 0)
        {
            using var noteScope = SafeColor.Scope(SafeColor.Muted);
            WriteLine(CodeWrapNote(_codeOverlongLines, _width));
        }
        _codeOverlongLines = 0;
        _codeHeaderDrawn = false;
    }

    /// <summary>整块渲染（Flush 路径）：代码已全部到齐时一次性输出。
    /// 与逐行路径共用表头/表尾，避免两条路径的徽标与折行交代出现差异。</summary>
    private void EmitCode(string code)
    {
        if (code.Length == 0)
            return;
        EmitCodeHeader();
        _codeOverlongLines = CountOverlongCodeLines(code, _width);
        using (SafeColor.Scope(SafeColor.Success))
        {
            Write(NormalizeCodeBlock(code));
        }
        EmitCodeFooter();
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

    /// <summary>窄屏下最多能完整显示的**前导列数**（宽度未知时返回全部，不做猜测性裁剪）。
    /// 宽表在窄终端里若只靠压缩列宽，每列都会被压到 1–2 列（`a │ b │ c`），毫无信息量；
    /// 宁可丢掉末尾的列并在表后说明丢了多少。</summary>
    internal static int MaxReadableColumns(int[] natural, int budget, int minColWidth = MinTableColWidth, int indent = 2, int separatorWidth = 3)
    {
        if (natural.Length == 0)
            return 0;
        if (budget <= 0)
            return natural.Length;
        var keep = 0;
        var used = indent;
        for (var i = 0; i < natural.Length; i++)
        {
            var w = natural[i] + (i > 0 ? separatorWidth : 0);
            if (i > 0 && used + w > budget)
                break;
            used += w;
            keep = i + 1;
        }
        // 连一列都放不下时至少保留一列，否则表格整个消失
        return Math.Max(1, keep);
    }

    /// <summary>表格可用总宽：终端宽度（含缩进）；不可用时返回 0 = 不限。</summary>
    private int TableBudget()
    {
        if (_width > 0)
            return _width;
        if (Console.IsOutputRedirected)
            return 0;
        int measured;
        try { measured = Console.WindowWidth; }
        catch { return 0; }
        // 终端最小化、拖拽过程中的抖动都会让 WindowWidth 短暂返回 1~2 列。
        // 此前这里直接拿它当预算，于是每一列都被压到 1~2 字符
        // （`a │ b │ c`）——正是 MinPlausibleColumns 当初要防的那种退化。
        // 现在沿用同一套「测量值 + 上一次好值」的解析。
        var resolved = Program.ResolveColumns(measured, _lastGoodWidth);
        if (resolved >= Program.MinPlausibleColumns)
            _lastGoodWidth = resolved;
        return resolved;
    }

    /// <summary>上一次可信的表格预算（用于终端宽度读数抖动时沿用）。</summary>
    private int _lastGoodWidth;

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
        var budget = TableBudget();
        var keep = MaxReadableColumns(natural, budget);
        var droppedCols = cols - keep;
        // 原始列数必须在这里留下：下面 `cols = keep` 会把它覆盖掉，
        // 而截断提示要报的是**总数**。用覆盖后的值会打印成
        // 「表格共 4 列，仅显示前 4 列」——等于告诉用户一列都没丢，
        // 比不给提示更糟。
        var totalCols = cols;
        if (droppedCols > 0)
        {
            natural = natural.Take(keep).ToArray();
            cols = keep;
        }
        var widths = FitColumnWidths(natural, budget);
        foreach (var r in rows)
        {
            // 分隔行判定需精确：Markdown 分隔行单元格形如 ---、:---、---:、:---:
            // （可选冒号 + 至少 3 个 -），否则单字符数据行（如 - 或 :）会被误判为分隔行而丢失
            var isSep = r.Count > 0 && r.All(c => SepRe.IsMatch(c));
            var cells = new string[Math.Min(r.Count, cols)];
            for (int i = 0; i < cells.Length; i++)
            {
                // 超宽单元格先截断到列宽（FitToWidth 代理对安全并补省略号），再补空格对齐
                var cell = InputLine.FitToWidth(r[i], widths[i]);
                cells[i] = isSep ? new string(SafeColor.Glyphs.Rule, widths[i]) : PadToWidth(cell, widths[i]);
            }
            WriteLine("  " + string.Join(" " + SafeColor.Glyphs.ColumnSeparator + " ", cells));
        }
        // 截断提示本身也要受宽度约束：解释截断的那一行**溢出**终端比不解释更糟。
        // budget <= 0 表示宽度未知，此时不做猜测性裁剪（既有约定）。
        var noteWidth = budget - 2;
        if (droppedCols > 0)
            WriteLine("  " + Note(TableDroppedColsNote(totalCols, keep, noteWidth), noteWidth));
        if (truncated)
            WriteLine("  " + Note(TableDroppedRowsNote(_tableBuf.Count, MaxTableRows, noteWidth), noteWidth));
        _tableBuf.Clear();
    }

    /// <summary>把提示压进预算：宽度未知时原样返回（不猜测折行位置）。</summary>
    private static string Note(string note, int width) =>
        width > 0 ? InputLine.FitToWidth(note, width) : note;

    /// <summary>裁列提示：报**总数**而不是保留数。此前 `cols` 已被改写成 `keep`，
    /// 于是输出「表格共 4 列，仅显示前 4 列」——谎报一列都没丢，比不给提示更糟。
    /// 放不下完整句时改用紧凑的 <c>…(N 列 → M 列)</c>：按**显示宽度**判定，
    /// 不能按字符数——「仅显示前」三个汉字只占 3 个字符却占 6 列。</summary>
    internal static string TableDroppedColsNote(int total, int keep, int width = 0)
    {
        var dots = SafeColor.Glyphs.Ellipsis;
        var arrow = SafeColor.Glyphs.Arrow;
        var full = $"{dots}(表格共 {total} 列，终端较窄，仅显示前 {keep} 列)";
        return width > 0 && TextUtil.DisplayWidth(full) > width ? $"{dots}({total} 列 {arrow} {keep} 列)" : full;
    }

    /// <summary>裁行提示。与 <see cref="TableDroppedColsNote"/> 同样的宽度判定。</summary>
    internal static string TableDroppedRowsNote(int total, int keep, int width = 0)
    {
        var dots = SafeColor.Glyphs.Ellipsis;
        var arrow = SafeColor.Glyphs.Arrow;
        var full = $"{dots}(表格共 {total} 行，仅显示前 {keep} 行)";
        return width > 0 && TextUtil.DisplayWidth(full) > width ? $"{dots}({total} 行 {arrow} {keep} 行)" : full;
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
