namespace CodeAgent;

public sealed class HistoryStore
{
    public const int MaxEntries = 100;

    private readonly string _path;
    private readonly List<string> _entries;

    public HistoryStore(string path)
    {
        _path = path;
        _entries = Load();
    }

    /// <summary>历史条目（旧 → 新）。</summary>
    public IReadOnlyList<string> Entries => _entries;

    public int Count => _entries.Count;

    /// <summary>检查历史中是否包含指定字符串（忽略大小写）。</summary>
    public bool Contains(string line) => _entries.Contains(line, StringComparer.OrdinalIgnoreCase);

    /// <summary>移除历史中第一条匹配的条目（忽略大小写）；未找到返回 false。</summary>
    public bool Remove(string line)
    {
        var idx = _entries.FindIndex(l => l.Equals(line, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            return false;
        _entries.RemoveAt(idx);
        Save();
        return true;
    }

    /// <summary>清空历史条目（/history clear 用）；文件也会删除。</summary>
    public void Clear()
    {
        _entries.Clear();
        try { File.Delete(_path); } catch { }
    }

    /// <summary>记录一条输入：空白忽略；重复条目移到末尾（↑/↓ 与 Ctrl+R 里不再出现
    /// 散落的旧副本）；超上限丢最旧。</summary>
    public void Remember(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        if (_entries.Count > 0 && _entries[^1] == line)
            return;
        // 非相邻的旧重复一并移除：等价于「同一命令多次使用后只保留最新位置」
        _entries.RemoveAll(l => l == line);
        _entries.Add(line);
        if (_entries.Count > MaxEntries)
            _entries.RemoveAt(0);
        Save();
    }

    private List<string> Load()
    {
        try
        {
            if (!File.Exists(_path))
                return [];
            var entries = new Queue<string>(MaxEntries);
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                entries.Enqueue(Decode(line));
                if (entries.Count > MaxEntries)
                    entries.Dequeue();
            }
            return entries.ToList();
        }
        catch
        {
            return [];
        }
    }

    private void Save()
    {
        var tmp = SkipDirs.TempPathFor(_path);
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllLines(tmp, _entries.TakeLast(MaxEntries).Select(Encode));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            // 历史保存失败不影响主流程
        }
    }

    // 多行输入（粘贴的代码块等）会进入历史：文件按行存储，内嵌换行必须转义，
    // 否则一条多行历史会被拆成多条碎片，污染 ↑/↓ 与 Ctrl+R。旧版写入的单行条目
    // （可能含反斜杠路径）必须原样兼容：未识别的转义序列保持字面。

    private static string Encode(string s) =>
        s.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");

    private static string Decode(string s)
    {
        if (!s.Contains('\\'))
            return s;
        return HistoryEscapeRe.Replace(s, m => m.Value switch
        {
            "\\n" => "\n",
            "\\r" => "\r",
            "\\\\" => "\\",
            _ => m.Value, // 未识别的转义（旧版文件里的 \P 等）保持原样
        });
    }

    private static readonly System.Text.RegularExpressions.Regex HistoryEscapeRe =
        new(@"\\\\|\\n|\\r", System.Text.RegularExpressions.RegexOptions.Compiled);
}
