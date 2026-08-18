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

    /// <summary>删除光标前一个字符；光标在行首时无操作。返回是否删除。</summary>
    public bool Backspace()
    {
        if (Cursor <= 0)
            return false;
        _text.Remove(Cursor - 1, 1);
        Cursor--;
        return true;
    }

    /// <summary>删除光标处（后一个）字符；光标在行尾时无操作。返回是否删除。</summary>
    public bool Delete()
    {
        if (Cursor >= _text.Length)
            return false;
        _text.Remove(Cursor, 1);
        return true;
    }

    public void MoveLeft()
    {
        if (Cursor > 0)
            Cursor--;
    }

    public void MoveRight()
    {
        if (Cursor < _text.Length)
            Cursor++;
    }

    /// <summary>光标左移一个单词：跳过左侧空白，再跳过一个连续非空白段（Ctrl+←）。</summary>
    public void MoveWordLeft()
    {
        var text = _text.ToString();
        var i = Cursor;
        while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(text[i - 1])) i--;
        Cursor = i;
    }

    /// <summary>光标右移一个单词：跳过右侧空白，再跳过一个连续非空白段（Ctrl+→）。</summary>
    public void MoveWordRight()
    {
        var text = _text.ToString();
        var i = Cursor;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
        Cursor = i;
    }

    /// <summary>删除光标前一个单词（Ctrl+Backspace，边界与 Ctrl+← 一致）；返回是否删除。</summary>
    public bool DeleteWordBackward()
    {
        var text = _text.ToString();
        var i = Cursor;
        while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(text[i - 1])) i--;
        while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--; // 连同单词前的空白一起删
        if (i == Cursor)
            return false;
        _text.Remove(i, Cursor - i);
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
        _text.Remove(Cursor, i - Cursor);
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
            var second = text.LastIndexOf('\n', first - 1); // 上一行行首前的 \n
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

    public void Home() => Cursor = 0;

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
