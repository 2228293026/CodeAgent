using System.Text;

namespace CodeAgent;

/// <summary>
/// 可编辑输入行：文本 + 光标位置（行内编辑的基础单元）。
/// 支持左右移动、Home/End、在光标处插入/删除；所有操作都在内存中进行，
/// 由 InputLine 负责把结果渲染到终端。
/// </summary>
public sealed class EditableLine
{
    private readonly StringBuilder _text = new();

    /// <summary>当前光标位置（0..Length，位于字符之间）。</summary>
    public int Cursor { get; private set; }

    /// <summary>当前文本长度。</summary>
    public int Length => _text.Length;

    /// <summary>完整文本。</summary>
    public string Text => _text.ToString();

    /// <summary>预填初始文本，光标移到末尾。</summary>
    public void SetInitial(string? initial)
    {
        if (string.IsNullOrEmpty(initial))
            return;
        _text.Append(initial);
        Cursor = _text.Length;
    }

    /// <summary>在光标处插入字符，光标后移。</summary>
    public void Insert(char ch)
    {
        _text.Insert(Cursor, ch);
        Cursor++;
    }

    /// <summary>删除光标前一个字符；光标在行首时无操作。返回是否删除。
    /// 代理对（emoji 等）整体删除：只删半个码点会留下损坏的孤立代理，终端渲染为乱码。</summary>
    public bool Backspace()
    {
        if (Cursor <= 0)
            return false;
        var count = 1;
        var start = Cursor - 1;
        // 光标前的低代理 + 它前面的高代理是一个码点：一起删
        if (char.IsLowSurrogate(_text[Cursor - 1]) && Cursor >= 2 && char.IsHighSurrogate(_text[Cursor - 2]))
        {
            count = 2;
            start = Cursor - 2;
        }
        // 光标前的高代理 + 光标处的低代理是一个码点：一起删（光标落在代理对中间时）
        else if (char.IsHighSurrogate(_text[Cursor - 1]) && Cursor < _text.Length && char.IsLowSurrogate(_text[Cursor]))
        {
            count = 2;
            start = Cursor - 1;
        }
        _text.Remove(start, count);
        Cursor -= count;
        return true;
    }

    /// <summary>删除光标处（后一个）字符；光标在行尾时无操作。返回是否删除。
    /// 代理对整体删除（同 Backspace）。</summary>
    public bool Delete()
    {
        if (Cursor >= _text.Length)
            return false;
        var count = 1;
        var start = Cursor;
        // 光标处的高代理 + 后面的低代理是一个码点：一起删
        if (char.IsHighSurrogate(_text[Cursor]) && Cursor + 1 < _text.Length && char.IsLowSurrogate(_text[Cursor + 1]))
            count = 2;
        // 光标处的低代理（前面是高代理）：一起删配对的高+低代理
        else if (Cursor > 0 && char.IsLowSurrogate(_text[Cursor]) && char.IsHighSurrogate(_text[Cursor - 1]))
        {
            count = 2;
            start = Cursor - 1;
        }
        _text.Remove(start, count);
        if (start < Cursor)
            Cursor -= count;
        return true;
    }

    public void MoveLeft()
    {
        if (Cursor > 0)
        {
            Cursor--;
            if (Cursor > 0 && char.IsLowSurrogate(_text[Cursor]) && char.IsHighSurrogate(_text[Cursor - 1]))
                Cursor--; // 代理对作为单个码点移动
        }
    }

    public void MoveRight()
    {
        if (Cursor < _text.Length)
        {
            Cursor++;
            if (Cursor < _text.Length && char.IsHighSurrogate(_text[Cursor - 1]) && char.IsLowSurrogate(_text[Cursor]))
                Cursor++; // 代理对作为单个码点移动
        }
    }

    /// <summary>光标左移一个单词：跳过左侧空白，再跳过一个连续非空白段（Ctrl+←）。</summary>
    public void MoveWordLeft()
    {
        var text = _text.ToString();
        var i = Cursor;
        while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
        while (i > 0)
        {
            // 代理对保护：遇到代理对时视为一个整体，跳过并停止（代理对边界即词边界）
            if (i > 1 && char.IsLowSurrogate(text[i - 1]) && char.IsHighSurrogate(text[i - 2]))
            {
                i -= 2;
                break;
            }
            // 光标紧贴代理对后半（低代理）时，把整对纳入删除范围后停止
            if (i < text.Length && char.IsHighSurrogate(text[i - 1]) && char.IsLowSurrogate(text[i]))
            {
                i--;
                break;
            }
            if (char.IsWhiteSpace(text[i - 1]))
                break;
            i--;
        }
        Cursor = i;
    }

    /// <summary>光标右移一个单词：跳过右侧空白，再跳过一个连续非空白段（Ctrl+→）。</summary>
    public void MoveWordRight()
    {
        var text = _text.ToString();
        var i = Cursor;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        while (i < text.Length)
        {
            // 代理对保护：遇到代理对时视为一个整体，跳过并继续（代理对边界即词边界）
            if (i + 1 < text.Length && char.IsHighSurrogate(text[i]) && char.IsLowSurrogate(text[i + 1]))
            {
                i += 2;
                continue;
            }
            if (char.IsWhiteSpace(text[i]))
                break;
            i++;
        }
        Cursor = i;
    }

    /// <summary>删除光标前一个单词（Ctrl+Backspace，边界与 Ctrl+← 一致）；返回是否删除。</summary>
    public bool DeleteWordBackward()
    {
        var text = _text.ToString();
        var i = Cursor;
        while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
        while (i > 0)
        {
            // 代理对保护：遇到代理对时视为一个整体，跳过并停止（代理对边界即词边界）
            if (i > 1 && char.IsLowSurrogate(text[i - 1]) && char.IsHighSurrogate(text[i - 2]))
            {
                i -= 2;
                break;
            }
            // 光标紧贴代理对后半（低代理）时，把整对纳入删除范围后停止
            if (i < text.Length && char.IsHighSurrogate(text[i - 1]) && char.IsLowSurrogate(text[i]))
            {
                i--;
                break;
            }
            if (char.IsWhiteSpace(text[i - 1]))
                break;
            i--;
        }
        while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--; // 连同单词前的空白一起删
        if (i == Cursor)
            return false;
        // 代理对保护：如果删除范围会留下 dangling surrogate，扩展范围以包含整个代理对
        var count = Cursor - i;
        if (Cursor > 0 && char.IsHighSurrogate(text[Cursor - 1]) && Cursor < text.Length && char.IsLowSurrogate(text[Cursor]))
            count++;
        _text.Remove(i, count);
        Cursor = i;
        return true;
    }
    public bool DeleteWordForward()
    {
        var text = _text.ToString();
        var i = Cursor;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
        if (i == Cursor)
            return false;
        var start = Cursor;
        // 代理对保护：光标在低代理上时，其前的高代理属于同一码点，整对一起删
        if (Cursor > 0 && char.IsLowSurrogate(text[Cursor]) && char.IsHighSurrogate(text[Cursor - 1]))
            start--;
        _text.Remove(start, i - start);
        return true;
    }
    /// <summary>光标向上移一行（多行输入）：移到上一行行首；已在首行则移到行首。返回是否移动。</summary>
    public bool MoveLineUp()
    {
        if (Cursor <= 0)
            return false;
        var text = _text.ToString();
        var first = text.LastIndexOf('\n', Cursor - 1); // 光标前最近 \n（当前行首前）
        int target;
        if (first < 0)
        {
            target = 0; // 已在首行：移到行首
        }
        else
        {
            // 首行就是 \n（如 "\nabc"）时 first==0，LastIndexOf(..., -1) 会抛异常：
            // 此时上一行就是首行，target=0
            var second = first > 0 ? text.LastIndexOf('\n', first - 1) : -1;
            target = second < 0 ? 0 : second + 1;           // 上一行行首（无则首行）
        }
        if (target == Cursor)
            return false;
        Cursor = target;
        return true;
    }

    /// <summary>光标向下移一行（多行输入）：移到下一行行首；已在末行则移到行尾。返回是否移动。</summary>
    public bool MoveLineDown()
    {
        if (Cursor >= _text.Length)
            return false;
        var text = _text.ToString();
        var next = text.IndexOf('\n', Cursor);
        var target = next < 0 ? text.Length : next + 1;
        if (target == Cursor)
            return false;
        Cursor = target;
        return true;
    }

    /// <summary>光标上下移到相邻行，**保持列位置**（↑↓ 的编辑语义）。返回是否移动。
    ///
    /// 与 <see cref="MoveLineUp"/>/<see cref="MoveLineDown"/> 的区别：那两个只跳到行首/行尾，
    /// 适合「快速跳到某行的开头」，但不适合**在行间游走**——
    /// 多行粘贴进来想改中间某行时，按 ↑↓ 却每次都落到行首，
    /// 等于每换一行都要重新数着按 →，实际根本没法用键盘改多行内容。
    ///
    /// 列按**码点**计（不是 UTF-16 单元）：一列 = 一个显示字符，
    /// 中文/emoji 一行里占一个语义位置。用 UTF-16 偏移会在一行有 emoji 时错位。
    /// 目标行比当前列短时落到行尾（不越界，也不回填到上一行）。
    /// </summary>
    /// <param name="up">true 上移，false 下移。</param>
    public bool MoveLinePreservingColumn(bool up)
    {
        var text = _text.ToString();
        if (text.IndexOf('\n') < 0)
            return false; // 单行：交给外层当历史浏览用

        var lineStart = Cursor > 0 ? text.LastIndexOf('\n', Cursor - 1) + 1 : 0;
        var col = CountClusters(text, lineStart, Cursor);

        int targetStart;
        if (up)
        {
            if (lineStart == 0)
                return false; // 已在首行
            targetStart = text.LastIndexOf('\n', lineStart - 2) + 1;
        }
        else
        {
            var lineEnd = text.IndexOf('\n', Cursor);
            if (lineEnd < 0)
                return false; // 已在末行
            targetStart = lineEnd + 1;
        }

        var targetEnd = text.IndexOf('\n', targetStart);
        if (targetEnd < 0)
            targetEnd = text.Length;
        Cursor = SkipClusters(text, targetStart, targetEnd, col);
        return true;
    }

    /// <summary>[from, to) 内的码点数（代理对算一个）。</summary>
    private static int CountClusters(string text, int from, int to)
    {
        var n = 0;
        for (var i = from; i < to; i++)
        {
            if (i + 1 < to && char.IsHighSurrogate(text[i]) && char.IsLowSurrogate(text[i + 1]))
                i++; // 代理对整体算一个
            n++;
        }
        return n;
    }

    /// <summary>从 start 起前进 count 个码点，最多到 end（不跨行）。</summary>
    private static int SkipClusters(string text, int start, int end, int count)
    {
        var i = start;
        for (var n = 0; n < count && i < end; n++)
        {
            if (i + 1 < end && char.IsHighSurrogate(text[i]) && char.IsLowSurrogate(text[i + 1]))
                i += 2;
            else
                i++;
        }
        return i;
    }

    public void Home() => Cursor = 0;

    /// <summary>光标移到当前行行首（多行输入的 Home 语义；单行时等价 Home）。</summary>
    public void LineHome()
    {
        var text = _text.ToString();
        var ls = Cursor > 0 ? text.LastIndexOf('\n', Cursor - 1) : -1;
        Cursor = ls < 0 ? 0 : ls + 1;
    }

    /// <summary>光标移到当前行行尾（多行输入的 End 语义；单行时等价 End）。</summary>
    public void LineEnd()
    {
        var text = _text.ToString();
        var nl = text.IndexOf('\n', Cursor);
        Cursor = nl < 0 ? text.Length : nl;
    }

    /// <summary>删到**本行**行尾（Ctrl+K 的 readline 语义）。返回是否删掉了东西。</summary>
    ///
    /// 刻意不越过换行：readline 里 Ctrl+K 在行尾按两次会删掉换行、把下一行接上来，
    /// 那是「kill ring」体系的一部分，而这里没有 kill ring——只删掉换行的话，
    /// 用户看到的是「按了一下 Ctrl+K，两行合成一行」，比不做更意外。
    /// 多行输入里想删下一行，光标在行尾再按 Delete 语义更清楚。
    public bool DeleteToLineEnd()
    {
        var text = _text.ToString();
        var nl = text.IndexOf('\n', Cursor);
        var end = nl < 0 ? text.Length : nl;
        if (end <= Cursor)
            return false;
        // 代理对保护：截断点落在低代理上时，其前的高代理属于同一码点，整对一起删
        if (end < text.Length && char.IsLowSurrogate(text[end]) && char.IsHighSurrogate(text[end - 1]))
            end--;
        if (end <= Cursor)
            return false;
        _text.Remove(Cursor, end - Cursor);
        return true;
    }

    /// <summary>删到**本行**行首（Ctrl+U，与 <see cref="DeleteToLineEnd"/> 对称）。返回是否删掉了东西。</summary>
    ///
    /// 同样刻意不越过换行：readline 的 Ctrl+U 会把光标前的换行一起吃掉，
    /// 把上一行接到下一行上。这里没有 kill ring，删掉的换行再也找不回来——
    /// 用户看到的是「按了一下 Ctrl+U，两行合成一行」，比不做更意外。
    public bool DeleteToLineStart()
    {
        if (Cursor <= 0)
            return false;
        var text = _text.ToString();
        var start = Cursor > 0 ? text.LastIndexOf('\n', Cursor - 1) + 1 : 0;
        if (start >= Cursor)
            return false;
        // 代理对保护：起点落在低代理上时，前一格的高代理属于同一码点，整对一起删
        if (start > 0 && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1]))
            start--;
        if (start >= Cursor)
            return false;
        _text.Remove(start, Cursor - start);
        Cursor = start;
        return true;
    }

    public void End() => Cursor = _text.Length;

    public void Clear()
    {
        _text.Clear();
        Cursor = 0;
    }

    /// <summary>整体替换文本（历史浏览用），光标移到末尾。</summary>
    public void Replace(string text)
    {
        _text.Clear();
        _text.Append(text);
        Cursor = _text.Length;
    }

    public override string ToString() => _text.ToString();
}
