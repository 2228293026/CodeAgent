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
            return File.ReadAllLines(_path)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .TakeLast(MaxEntries)
                .Select(Decode)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllLines(_path, _entries.TakeLast(MaxEntries).Select(Encode));
        }
        catch
        {
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
        return HistoryEscapeRe.Replace(s, m => m.Groups[1].Value switch
        {
            "n" => "\n",
            "r" => "\r",
            "\\" => "\\",
            _ => m.Value, // 未识别的转义（旧版文件里的 \P 等）保持原样
        });
    }

    private static readonly System.Text.RegularExpressions.Regex HistoryEscapeRe =
        new(@"\\(.)", System.Text.RegularExpressions.RegexOptions.Compiled);
}
