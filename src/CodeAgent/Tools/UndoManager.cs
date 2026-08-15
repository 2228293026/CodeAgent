namespace CodeAgent.Tools;

/// <summary>一次可撤销的文件修改记录。</summary>
public sealed class UndoEntry
{
    /// <summary>操作类型：write（整文件覆盖）| edit（局部替换）。</summary>
    public required string Kind { get; init; }

    /// <summary>文件完整路径。</summary>
    public required string Path { get; init; }

    /// <summary>write：修改前的完整内容；edit：修改前在文件中的原文（撤销恢复目标）。</summary>
    public string? OldText { get; init; }

    /// <summary>edit：当前文件中的新文本（撤销时替换回 OldText 的对象）。</summary>
    public string? NewText { get; init; }

    /// <summary>write：修改前文件是否已存在。</summary>
    public bool HadFile { get; init; }
}

/// <summary>文件修改撤销栈（REPL 的 /undo 命令）。</summary>
public sealed class UndoManager
{
    private const int MaxEntries = 50;
    private readonly object _lock = new();
    private readonly List<UndoEntry> _entries = [];

    public int Count => _entries.Count;

    public void Push(UndoEntry entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
                _entries.RemoveAt(0);
        }
    }

    /// <summary>撤销最近一次文件修改，返回操作描述；无记录时返回 null。</summary>
    public string? TryUndo()
    {
        if (_entries.Count == 0)
            return null;

        var e = _entries[^1];
        _entries.RemoveAt(_entries.Count - 1);
        try
        {
            Apply(e);
        }
        catch (Exception ex)
        {
            return $"撤销失败: {ex.Message}";
        }
        return Describe(e);
    }

    private static void Apply(UndoEntry e)
    {
        if (e.Kind == "write")
        {
            if (e.HadFile && e.OldText is not null)
                File.WriteAllText(e.Path, e.OldText);
            else if (!e.HadFile && File.Exists(e.Path))
                File.Delete(e.Path);
        }
        else if (e.Kind == "edit")
        {
            if (!File.Exists(e.Path))
                throw new InvalidOperationException($"文件已不存在: {e.Path}");
            var text = File.ReadAllText(e.Path);
            File.WriteAllText(e.Path, text.Replace(e.NewText ?? "", e.OldText ?? ""));
        }
    }

    private static string Describe(UndoEntry e) =>
        e.Kind == "write"
            ? $"已撤销 write_file: {Path.GetFileName(e.Path)}（{(e.HadFile ? "恢复原内容" : "删除新建文件")}）"
            : $"已撤销 edit_file: {Path.GetFileName(e.Path)}";
}
