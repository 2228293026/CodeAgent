namespace CodeAgent;

/// <summary>输入行的编辑模式（学自 Claude Code 的 <c>/vim</c>）。</summary>
public enum VimInputMode
{
    /// <summary>插入：按键直接落字，与没开 vim 时完全一样。</summary>
    Insert,

    /// <summary>普通：按键是命令（移动、删除），**不落字**。</summary>
    Normal,
}

/// <summary>一次按键处理之后的输入状态。<paramref name="Changed"/> 为 false 时
/// <paramref name="Text"/>/<paramref name="Cursor"/> 与传入值相同，调用方可以跳过重绘。</summary>
public readonly record struct VimResult(string Text, int Cursor, VimInputMode Mode, bool Changed);

/// <summary>
/// vim 普通模式的按键状态机：把「当前文本 + 光标 + 一个按键」映射成新的「文本 + 光标 + 模式」。
///
/// 之所以做成**纯状态机**而不是直接写在 <see cref="InputLine"/> 的按键循环里：
///   · 输入循环那一大段负责渲染、粘贴、菜单、历史，任何改动都在冒险；
///   · 而「按了 dw 应该删掉哪几个字符」是纯函数，可以逐条钉死；
///   · 状态机不认识终端，测试不需要伪造 Console。
///
/// 刻意只做**可用的子集**，不做 vim 模拟器：计数前缀（<c>3l</c>、<c>2dd</c>）、
/// 移动 <c>h l w b e 0 ^ $ gg G</c>、删除 <c>x dd dw db de D</c>、修改 <c>cw cc C</c>、
/// 进入插入 <c>i a A I</c>、撤销 <c>u</c>。
/// 没做的：可视模式、寄存器与 <c>p</c>、<c>f/t</c>、<c>r</c>、宏、<c>/</c> 搜索——
/// 这些在**单行提示框**里收益远小于它们带来的状态复杂度，做一半比不做更容易误伤。
///
/// 光标语义沿用 <see cref="EditableLine"/> 的「插入点」（0..Length 的字符间隙），
/// 不是 vim 的「停在字符上」。提示框里要的是能直接落字的位置，
/// 若照搬 vim 的字符位语义，<c>i</c> 还得再补偿一格，反而处处别扭。
/// </summary>
public sealed class VimSession
{
    /// <summary>撤销栈深度上限。提示框里一次输入不会产生上百次编辑，
    /// 但长粘贴 + 连续 <c>dw</c> 可以；设上限是为了不让它无限涨。</summary>
    public const int MaxUndo = 100;

    private readonly List<(string Text, int Cursor)> _undo = new();
    private int _count;
    private char _operator; // '\0' | 'd' | 'c'
    private bool _gArmed;

    /// <summary>当前模式。<c>Read</c> 每次开始都是插入模式——
    /// 用户按 Enter 交出去之后，下一次输入不该还停在上次的普通模式里。</summary>
    public VimInputMode Mode { get; private set; } = VimInputMode.Insert;

    /// <summary>Esc：插入模式下回到普通模式（光标左移一个字符，vim 的既有习惯）；
    /// already-normal 时只清掉待定前缀（<c>d</c>、<c>2</c>），不做别的事。</summary>
    public VimResult Escape(string text, int cursor)
    {
        if (Mode == VimInputMode.Insert)
        {
            Mode = VimInputMode.Normal;
            var at = Math.Clamp(cursor, 0, text.Length);
            return new VimResult(text, PrevCluster(text, at), Mode, true);
        }
        CancelPending();
        return new VimResult(text, cursor, Mode, false);
    }

    /// <summary>清掉待定前缀（Esc、未知键、外部改动之后都该清）。</summary>
    public void CancelPending()
    {
        _count = 0;
        _operator = '\0';
        _gArmed = false;
    }

    /// <summary>
    /// 处理普通模式下的一个可打印字符。插入模式下返回 Changed=false（按键该落字去，
    /// 由调用方原有的插入逻辑处理，不在这里抢）。
    /// </summary>
    public VimResult Feed(string text, int cursor, char key)
    {
        if (Mode != VimInputMode.Normal)
            return new VimResult(text, cursor, Mode, false);

        var at = Math.Clamp(cursor, 0, text.Length);

        // 计数前缀。0 是特例：没有计数时它是「行首」，有计数时它是数字（10l）。
        if (key is >= '0' and <= '9' && _operator == '\0' && (key != '0' || _count > 0))
        {
            _count = Math.Min(_count * 10 + (key - '0'), 999);
            return new VimResult(text, at, Mode, false);
        }

        var count = _count == 0 ? 1 : _count;
        var op = _operator;

        // gg 的第二个 g
        if (_gArmed)
        {
            _gArmed = false;
            if (key == 'g')
                return Motion(text, at, 0, false, count);
            CancelPending();
            return new VimResult(text, at, Mode, false);
        }

        switch (key)
        {
            case 'g':
                if (op == '\0')
                {
                    CancelPending();
                    _gArmed = true;
                    return new VimResult(text, at, Mode, false);
                }
                CancelPending();
                return new VimResult(text, at, Mode, false);

            case 'h':
                return Motion(text, at, MoveClusters(text, at, -count), false, count);

            case 'l':
            case ' ':
                return Motion(text, at, MoveClusters(text, at, count), false, count);

            case '0':
                return Motion(text, at, LineStart(text, at), false, count);

            case '^':
                return Motion(text, at, FirstNonBlank(text, at), false, count);

            case '$':
                return Motion(text, at, LineEnd(text, at), false, count);

            case 'G':
                return Motion(text, at, LineStart(text, Math.Max(0, text.Length - 1)), false, count);

            // cw 在 vim 里等价于 ce（只换掉词本身，不吃词后的空格）；
            // dw 则连词后空白一起删。两者都留着，因为用户对它们的预期确实不同。
            case 'w' when op == 'c':
                return Motion(text, at, NextWordEnd(text, at, count), true, count);

            case 'w':
                return Motion(text, at, NextWordStart(text, at, count), false, count);

            case 'b':
                return Motion(text, at, PrevWordStart(text, at, count), false, count);

            case 'e':
                return Motion(text, at, NextWordEnd(text, at, count), true, count);

            case 'x':
                return DeleteChars(text, at, count);

            // D 就是 d$ 的简写：**总是**删到行尾，不看当前有没有待定操作符。
            case 'D':
                return ApplyDelete(text, at, LineEnd(text, at), enterInsert: false);

            case 'C':
                return ChangeToLineEnd(text, at);

            case 'd':
                if (op == 'd')
                    return DeleteLines(text, at, count, enterInsert: false);
                // 只清 g 前缀，**保留计数**：2d 之后接 w/d 时那个 2 还得生效。
                // 早先在这里调 CancelPending()，2dd 于是退化成 dd——计数被操作符自己吃掉了。
                _gArmed = false;
                _operator = 'd';
                return new VimResult(text, at, Mode, false);

            case 'c':
                if (op == 'c')
                    return DeleteLines(text, at, count, enterInsert: true);
                _gArmed = false;
                _operator = 'c';
                return new VimResult(text, at, Mode, false);

            case 'i':
                PushUndo(text, at);
                CancelPending();
                Mode = VimInputMode.Insert;
                return new VimResult(text, at, Mode, false);

            case 'a':
                PushUndo(text, at);
                CancelPending();
                Mode = VimInputMode.Insert;
                return new VimResult(text, NextCluster(text, at), Mode, true);

            case 'A':
                PushUndo(text, at);
                CancelPending();
                Mode = VimInputMode.Insert;
                return new VimResult(text, LineEnd(text, at), Mode, true);

            case 'I':
                PushUndo(text, at);
                CancelPending();
                Mode = VimInputMode.Insert;
                return new VimResult(text, FirstNonBlank(text, at), Mode, true);

            case 'u':
                return Undo(text, at);

            default:
                // 普通模式下按了没绑定的字符：**吃掉**、不落字，也不上报变化。
                // 落到默认分支去插入字符就等于普通模式失效（敲 h 会打出 h）。
                CancelPending();
                return new VimResult(text, at, Mode, false);
        }
    }

    // ── 动作 ─────────────────────────────────────────────────────────────

    /// <summary>移动或「操作符 + 移动」。</summary>
    private VimResult Motion(string text, int cursor, int target, bool includeTarget, int count)
    {
        target = Math.Clamp(target, 0, text.Length);
        if (_operator == '\0')
        {
            CancelPending();
            if (target == cursor)
                return new VimResult(text, cursor, Mode, false);
            // 计数只影响移动距离，移动本身不产生撤销点
            _count = 0;
            return new VimResult(text, target, Mode, true);
        }

        var from = Math.Min(cursor, target);
        var to = Math.Max(cursor, target);
        if (includeTarget)
            to = Math.Min(NextCluster(text, to), text.Length);
        return ApplyDelete(text, from, to, enterInsert: _operator == 'c');
    }

    private VimResult DeleteChars(string text, int cursor, int count)
    {
        var end = cursor;
        var limit = LineEnd(text, cursor);
        for (var i = 0; i < count && end < limit; i++)
            end = NextCluster(text, end);
        return ApplyDelete(text, cursor, end, enterInsert: _operator == 'c');
    }

    private VimResult ChangeToLineEnd(string text, int cursor) =>
        ApplyDelete(text, cursor, LineEnd(text, cursor), enterInsert: true);

    /// <summary>删除 [from, to)，光标落到 from。</summary>
    private VimResult ApplyDelete(string text, int from, int to, bool enterInsert)
    {
        CancelPending();
        if (to <= from)
        {
            if (enterInsert)
                Mode = VimInputMode.Insert;
            return new VimResult(text, from, Mode, enterInsert);
        }
        PushUndo(text, from);
        var newText = text.Remove(from, to - from);
        if (enterInsert)
            Mode = VimInputMode.Insert;
        return new VimResult(newText, from, Mode, true);
    }

    /// <summary><c>dd</c> / <c>cc</c>：删除整行（含换行），<c>cc</c> 再进插入模式。</summary>
    private VimResult DeleteLines(string text, int cursor, int count, bool enterInsert)
    {
        CancelPending();
        var start = LineStart(text, cursor);
        var end = start;
        for (var i = 0; i < count; i++)
        {
            var lineEnd = LineEnd(text, end);
            if (lineEnd < text.Length)
            {
                end = lineEnd + 1; // 连同换行一起删
                continue;
            }
            end = lineEnd;
            // 删的是**最后一行**且它前面还有内容：把上一行的换行也吃掉，
            // 否则会留下一个空行——看起来像「dd 没删干净」。
            if (start > 0)
                start--;
            break;
        }
        return ApplyDelete(text, start, Math.Max(end, start), enterInsert);
    }

    private VimResult Undo(string text, int cursor)
    {
        CancelPending();
        if (_undo.Count == 0)
            return new VimResult(text, cursor, Mode, false);
        var (snapshotText, snapshotCursor) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        return new VimResult(snapshotText, Math.Clamp(snapshotCursor, 0, snapshotText.Length), Mode, true);
    }

    private void PushUndo(string text, int cursor)
    {
        _undo.Add((text, cursor));
        if (_undo.Count > MaxUndo)
            _undo.RemoveAt(0);
    }

    // ── 文本几何 ─────────────────────────────────────────────────────────

    private static int LineStart(string text, int cursor)
    {
        var at = Math.Clamp(cursor, 0, text.Length);
        if (at <= 0)
            return 0;
        var nl = text.LastIndexOf('\n', at - 1);
        return nl < 0 ? 0 : nl + 1;
    }

    private static int LineEnd(string text, int cursor)
    {
        var at = Math.Clamp(cursor, 0, text.Length);
        var nl = at >= text.Length ? -1 : text.IndexOf('\n', at);
        return nl < 0 ? text.Length : nl;
    }

    private static int FirstNonBlank(string text, int cursor)
    {
        var start = LineStart(text, cursor);
        var end = LineEnd(text, cursor);
        while (start < end && (text[start] == ' ' || text[start] == '\t'))
            start++;
        return start;
    }

    /// <summary>向前第 count 个单词的词首（vim 的 <c>w</c>：先走完当前词，再跳过空白）。</summary>
    private static int NextWordStart(string text, int cursor, int count)
    {
        var at = cursor;
        for (var n = 0; n < count; n++)
        {
            if (at < text.Length && !char.IsWhiteSpace(text[at]))
                while (at < text.Length && !char.IsWhiteSpace(text[at]))
                    at = NextCluster(text, at);
            while (at < text.Length && char.IsWhiteSpace(text[at]))
                at = NextCluster(text, at);
        }
        return at;
    }

    /// <summary>向后第 count 个单词的词首（vim 的 <c>b</c>）。</summary>
    private static int PrevWordStart(string text, int cursor, int count)
    {
        var at = cursor;
        for (var n = 0; n < count; n++)
        {
            if (at > 0)
                at = PrevCluster(text, at);
            while (at > 0 && char.IsWhiteSpace(text[at]))
                at = PrevCluster(text, at);
            while (at > 0 && !char.IsWhiteSpace(text[at - 1]))
                at = PrevCluster(text, at);
        }
        return at;
    }

    /// <summary>向前第 count 个单词的词尾（vim 的 <c>e</c>，停在词末字符上）。</summary>
    private static int NextWordEnd(string text, int cursor, int count)
    {
        var at = cursor;
        for (var n = 0; n < count; n++)
        {
            if (at < text.Length)
                at = NextCluster(text, at);
            while (at < text.Length && char.IsWhiteSpace(text[at]))
                at = NextCluster(text, at);
            while (at + 1 < text.Length && !char.IsWhiteSpace(text[at + 1]))
                at = NextCluster(text, at);
        }
        return at;
    }

    private static int MoveClusters(string text, int cursor, int delta)
    {
        var at = Math.Clamp(cursor, 0, text.Length);
        for (var i = 0; i < Math.Abs(delta); i++)
            at = delta < 0 ? PrevCluster(text, at) : NextCluster(text, at);
        return at;
    }

    /// <summary>前一个码点起点（代理对整体跳过）。</summary>
    private static int PrevCluster(string text, int at)
    {
        if (at <= 0)
            return 0;
        var i = at - 1;
        if (i > 0 && char.IsLowSurrogate(text[i]) && char.IsHighSurrogate(text[i - 1]))
            return i - 1;
        return i;
    }

    /// <summary>下一个码点起点（代理对整体跳过）。</summary>
    private static int NextCluster(string text, int at)
    {
        if (at >= text.Length)
            return text.Length;
        if (char.IsHighSurrogate(text[at]) && at + 1 < text.Length && char.IsLowSurrogate(text[at + 1]))
            return at + 2;
        return at + 1;
    }
}
