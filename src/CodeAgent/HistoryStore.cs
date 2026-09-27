namespace CodeAgent;

public sealed class HistoryStore
{
    public const int MaxEntries = 100;

    /// <summary>
    /// 单条历史的上限（字符数，含换行）。
    ///
    /// 粘贴一大段代码会进来几万字符，此前**原样入库**：当次会话里 ↑ 一次就把它全量灌回
    /// 输入框（重绘与折叠都要处理它），下次启动还会从文件里整个读回来。
    /// 更大的代价是文件——<see cref="Save"/> 每次提交都全量重写，100 条大条目就是几 MB 的写放大。
    ///
    /// 超限的条目**直接不入库，而不是截断**：截断后的命令被 ↑ 召回再回车执行，
    /// 是一句"看起来完整、实际缺了尾巴"的命令——可能删错文件、发错请求。
    /// 少一条历史只是少一次召回，远比执行半条命令安全。
    /// </summary>
    public const int MaxEntryChars = 2000;

    /// <summary>一条输入是否值得入库（空行与超长条目都不入）。
    /// 纯函数：这条规则决定了"什么会出现在 ↑ 里"，写死成内联条件就没人能测。</summary>
    internal static bool ShouldRemember(string line) =>
        !string.IsNullOrWhiteSpace(line) && line.Length <= MaxEntryChars;

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

    /// <summary>记录一条输入：空白忽略；超长条目忽略（见 <see cref="MaxEntryChars"/>）；
    /// 重复条目移到末尾（↑/↓ 与 Ctrl+R 里不再出现散落的旧副本）；超上限丢最旧。</summary>
    public void Remember(string line)
    {
        if (!ShouldRemember(line))
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
            if (new FileInfo(_path).LinkTarget is not null)
                return [];
            var entries = new Queue<string>(MaxEntries);
            foreach (var line in File.ReadLines(_path))
            {
                // 同样跳过超长条目：规则只加在写入侧的话，改规则之前写进去的老文件
                // 仍会在启动时把几万字符灌进输入框——那正是这条规则要防的场景
                if (!ShouldRemember(line))
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
        try
        {
            if (new FileInfo(_path).LinkTarget is not null)
                return;
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
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
