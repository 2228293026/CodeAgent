using System.Text;

namespace CodeAgent.Tools;

/// <summary>一次可撤销的文件修改记录。</summary>
public sealed class UndoEntry
{
    /// <summary>操作类型：write（整文件覆盖）| edit（局部替换）| cmd（命令副作用：新增/修改/删除文件）。</summary>
    public required string Kind { get; init; }

    /// <summary>文件完整路径。</summary>
    public required string Path { get; init; }

    /// <summary>write：修改前的完整内容；edit：修改前在文件中的原文（撤销恢复目标）。</summary>
    public string? OldText { get; init; }

    /// <summary>edit：当前文件中的新文本（撤销时替换回 OldText 的对象）。</summary>
    public string? NewText { get; init; }

    /// <summary>write：修改前文件是否已存在。</summary>
    /// <summary>write：修改前文件是否已存在。</summary>
    public bool HadFile { get; init; }

    /// <summary>撤销时按原编码写回（"utf8-bom" | "gb18030" | null=无 BOM UTF-8）：GBK 文件撤销后仍是 GBK。</summary>
    public string? EncodingName { get; init; }
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

    /// <summary>撤销最近 count 次文件修改（/undo N 与 /undo 选择用），返回合并描述；无记录时返回 null。</summary>
    public string? TryUndo(int count = 1)
    {
        lock (_lock)
        {
            if (_entries.Count == 0)
                return null;

            count = Math.Clamp(count, 1, _entries.Count);
            var sb = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                var e = _entries[^1];
                _entries.RemoveAt(_entries.Count - 1);

                // 大文件（>4MB write / 未入快照的 cmd）没有原内容，无法恢复：如实说明而非谎报成功
                if (e.Kind is "write" or "cmd" && e.HadFile && e.OldText is null)
                {
                    sb.AppendLine(e.Kind == "write"
                        ? $"无法撤销: {Path.GetFileName(e.Path)} 过大，未记录原内容（仅限 ≤4MB 的文件可撤销覆盖）。"
                        : $"无法撤销: {Path.GetFileName(e.Path)} 超出命令快照范围（单文件 >1MB 或快照总容量已满），原内容未记录。");
                    continue;
                }

                try
                {
                    // Apply 前记录文件是否存在：cmd 撤销重建后无法区分「恢复原内容」还是「重建被删文件」
                    var existedBeforeUndo = File.Exists(e.Path);
                    Apply(e);
                    sb.AppendLine(Describe(e, existedBeforeUndo));
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"撤销失败: {ex.Message}");
                    continue;
                }
            }
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>列出最近 max 条可撤销操作（编号 1 = 最近），无记录返回空串。</summary>
    public string ListEntries(int max = 10)
    {
        lock (_lock)
        {
            if (_entries.Count == 0)
                return "";
            var sb = new StringBuilder();
            var start = Math.Max(0, _entries.Count - max);
            for (int i = _entries.Count - 1; i >= start; i--)
                sb.AppendLine($"  {_entries.Count - i}) {Describe(_entries[i], File.Exists(_entries[i].Path))}");
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>按撤销条目记录的原编码写回：GBK 文件撤销后仍是 GBK，BOM 文件保 BOM。</summary>
    private static void WriteEntryText(UndoEntry e, string text)
    {
        switch (e.EncodingName)
        {
            case "gb18030":
                File.WriteAllText(e.Path, text, System.Text.Encoding.GetEncoding("GB18030"));
                break;
            case "utf8-bom":
                File.WriteAllText(e.Path, text, new System.Text.UTF8Encoding(true));
                break;
            default:
                TextUtil.WriteTextPreserveBom(e.Path, text); // 未知/纯 UTF-8：保 BOM 逻辑
                break;
        }
    }
    private static void Apply(UndoEntry e)
    {
        if (e.Kind is "write" or "cmd")
        {
            // cmd 与 write 的恢复逻辑一致：修改=写回旧内容、新增=删除、删除=重建
            if (e.HadFile && e.OldText is not null)
                WriteEntryText(e, e.OldText);
            else if (!e.HadFile && File.Exists(e.Path))
                File.Delete(e.Path);
        }
        else if (e.Kind == "edit")
        {
            if (!File.Exists(e.Path))
                throw new InvalidOperationException($"文件已不存在: {e.Path}");
            if (e.NewText is null)
            {
                // 小文件：记录了完整原文，直接写回（精确恢复）
                WriteEntryText(e, e.OldText ?? "");
            }
            else
            {
                // 大文件退化：仅替换 old/new 片段（可能有精度损失，但避免整文件内存开销）
                var text = File.ReadAllText(e.Path);
                WriteEntryText(e, text.Replace(e.NewText, e.OldText ?? ""));
            }
        }
    }

    private static string Describe(UndoEntry e, bool existedBeforeUndo) =>
        e.Kind == "write"
            ? $"已撤销 write_file: {Path.GetFileName(e.Path)}（{(e.HadFile ? "恢复原内容" : "删除新建文件")}）"
            : e.Kind == "cmd"
                ? $"已撤销命令副作用: {Path.GetFileName(e.Path)}（{DescribeCmdSideEffect(e, existedBeforeUndo)}）"
                : $"已撤销 edit_file: {Path.GetFileName(e.Path)}";

    private static string DescribeCmdSideEffect(UndoEntry e, bool existedBeforeUndo) =>
        !e.HadFile ? "删除新建文件" : existedBeforeUndo ? "恢复原内容" : "重建被删文件";

    // ===== 命令副作用撤销（run_command / bash / powershell 执行前快照，执行后差异入栈）=====

    private const long SnapshotMaxFileBytes = 1 * 1024 * 1024;   // 单文件上限：>1MB 不记录（无法撤销）
    private const long SnapshotMaxTotalBytes = 20 * 1024 * 1024; // 快照总大小上限：防止大项目拖慢每次命令

    /// <summary>对目录做文本文件快照（相对路径 → 内容）：跳过构建/缓存目录、二进制与超大文件。</summary>
    public static Dictionary<string, string> SnapshotDir(string cwd)
    {
        var snap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        try
        {
            foreach (var file in SkipDirs.EnumerateFilesPruned(cwd))
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (fi.Length <= 0 || fi.Length > SnapshotMaxFileBytes)
                        continue;
                    if (total + fi.Length > SnapshotMaxTotalBytes)
                        break;
                    var text = TextUtil.ReadTextSmart(file); // GBK 等旧编码快照内容不乱码
                    total += fi.Length;
                    snap[Path.GetRelativePath(cwd, file).Replace('\\', '/')] = text;
                }
                catch
                {
                    // 二进制/不可读/被占用：跳过（该文件无法撤销，其余照常）
                }
            }
        }
        catch
        {
            // 快照失败按无快照处理：本次命令不记录副作用
        }
        return snap;
    }

    /// <summary>对比执行前后快照，把新增/修改/删除的文件作为 cmd 条目推入撤销栈（/undo 可回滚）。</summary>
    public static void RecordCommandSideEffects(string cwd, Dictionary<string, string> before, UndoManager undo)
    {
        var after = SnapshotDir(cwd);
        var paths = new HashSet<string>(before.Keys, StringComparer.OrdinalIgnoreCase);
        paths.UnionWith(after.Keys);
        foreach (var rel in paths)
        {
            var had = before.TryGetValue(rel, out var old);
            var has = after.TryGetValue(rel, out var cur);
            if (had && has && old == cur)
                continue; // 内容未变
            var full = Path.GetFullPath(Path.Combine(cwd, rel.Replace('/', Path.DirectorySeparatorChar)));
            undo.Push(new UndoEntry
            {
                Kind = "cmd",
                Path = full,
                OldText = had ? old : null, // 新增文件无旧内容（撤销=删除）；修改/删除则记录执行前内容
                HadFile = had,
                // 命令副作用无法拿到执行前文件的编码：按当前文件尽力推断（命令未改编码时即原编码）
                EncodingName = had && has ? TextUtil.DetectFileEncoding(full) : null,
            });
        }
    }

    /// <summary>栈内所有涉及过的文件路径（去重，最近优先）——/files 审查本次会话改动面。</summary>
    public IReadOnlyList<string> AllPaths()
    {
        lock (_lock)
            return _entries
                .Select(e => e.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    /// <summary>显示最近一次修改的 diff（结合撤销快照与当前文件内容对比）；无记录时返回 null。</summary>
    public string? LastDiff() => AllDiffs(max: 1);

    /// <summary>显示所有待撤销改动的 diff（最多 max 条，最近优先，含新建/删除文件）；无记录时返回 null。</summary>
    public string? AllDiffs(int max = 20)
    {
        lock (_lock)
        {
            if (_entries.Count == 0)
                return null;
            var sb = new StringBuilder();
            var start = Math.Max(0, _entries.Count - max);
            for (int i = _entries.Count - 1; i >= start; i--)
            {
                var e = _entries[i];
                var diff = DiffFor(e);
                if (string.IsNullOrEmpty(diff))
                    continue;
                sb.AppendLine($"== {Path.GetFileName(e.Path)}（{KindLabel(e)}）==");
                sb.AppendLine(diff);
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }
    }

    private static string DiffFor(UndoEntry e)
    {
        try
        {
            var current = File.Exists(e.Path) ? TextUtil.ReadTextSmart(e.Path) : ""; // GBK 文件的 diff 不乱码
            string original;
            if (e.Kind == "write")
                original = e.HadFile ? e.OldText ?? "" : "";
            else if (e.NewText is null)
                original = e.OldText ?? ""; // 完整原文快照
            else
                original = current.Replace(e.NewText, e.OldText ?? "");

            var diff = DiffUtil.Unified(original, current, Path.GetFileName(e.Path));
            return diff.Length == 0 ? "（内容无差异）" : diff;
        }
        catch (Exception ex)
        {
            return $"读取失败: {ex.Message}";
        }
    }

    private static string KindLabel(UndoEntry e) =>
        e.Kind switch
        {
            "write" => "write_file",
            "edit" => "edit_file",
            "cmd" => "命令",
            _ => e.Kind,
        };
}
