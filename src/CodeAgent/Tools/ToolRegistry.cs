using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAgent.Providers;

namespace CodeAgent.Tools;

/// <summary>工具执行失败（应把消息返回给模型而非抛出到外层）。</summary>
public sealed class ToolException : Exception
{
    public ToolException(string message) : base(message) { }
}

/// <summary>工作区路径沙箱：strict 模式读写都限工作区（忽略白名单）；whitelist 模式读工具额外允许只读白名单目录；full 模式完全放开。</summary>
public sealed class Workspace
{
    private readonly string _rootPrefix;
    private readonly List<(string Dir, string Prefix)> _readOnly = new();
    private bool _fullAccess;        // 非 readonly：运行时可用 SetFileAccess 切换（Shift+Tab / /access）
    private bool _whitelistEnabled;  // strict 模式下白名单不生效，只有 whitelist/full 模式读工具才放行白名单目录

    /// <param name="readOnlyDirs">只读白名单目录（whitelist 模式生效；工作区之外也可，相对路径按工作区解析）；读工具可访问，写工具仍被拦截。</param>
    /// <param name="fileAccess">strict（默认）| whitelist | full——full 表示所有文件可读可写，完全放开沙箱。</param>
    public Workspace(string root, IReadOnlyList<string>? readOnlyDirs = null, string fileAccess = "strict")
    {
        Root = Path.GetFullPath(root);
        _rootPrefix = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                      + Path.DirectorySeparatorChar;
        _fullAccess = string.Equals(fileAccess, "full", StringComparison.OrdinalIgnoreCase);
        _whitelistEnabled = string.Equals(fileAccess, "whitelist", StringComparison.OrdinalIgnoreCase);
        if (readOnlyDirs is not null)
        {
            foreach (var d in readOnlyDirs)
            {
                if (string.IsNullOrWhiteSpace(d))
                    continue;
                var full = Path.GetFullPath(Path.Combine(Root, d))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); // 配置常写尾斜杠（libs/）：归一化后 ReadOnlyRoots 展示与目录相等判断才一致
                var prefix = full + Path.DirectorySeparatorChar; // full 已去尾分隔符，这里补回作为子路径前缀
                _readOnly.Add((full, prefix));
            }
        }
    }

    /// <summary>规范化的工作区根路径。</summary>
    public string Root { get; }

    /// <summary>只读白名单目录（读工具可用，写工具不可用）。</summary>
    public IReadOnlyList<string> ReadOnlyRoots => _readOnly.Select(x => x.Dir).ToList();

    /// <summary>是否完全放开沙箱（fileAccess=full）：所有文件可读可写。</summary>
    public bool FullAccess => _fullAccess;

    /// <summary>运行时切换文件访问模式（strict | whitelist | full）——Shift+Tab 与 /access 命令用，无需重启。</summary>
    public void SetFileAccess(string fileAccess)
    {
        _fullAccess = string.Equals(fileAccess, "full", StringComparison.OrdinalIgnoreCase);
        _whitelistEnabled = string.Equals(fileAccess, "whitelist", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把路径解析为工作区内的绝对路径（写工具/命令用）：whitelist 的白名单目录也会被拒绝；full 模式放行一切。</summary>
    public string Resolve(string? path) => ResolveCore(path, allowReadOnly: false);

    /// <summary>把路径解析为可读绝对路径（读/搜索工具用）：工作区 + 只读白名单目录（whitelist）均可；full 模式放行一切。</summary>
    public string ResolveRead(string? path) => ResolveCore(path, allowReadOnly: true);

    private string ResolveCore(string? path, bool allowReadOnly)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Root;

        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(Root, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // 非法字符（如 NUL）、空段等会让 Path 抛异常，应转为清晰的工具错误而非裸异常
            throw new ToolException($"路径非法: '{path}'（{ex.Message}）");
        }
        // 模型常给路径带尾分隔符（如 "src/a.txt/"）：GetFullPath 会保留它，
        // 导致 File.Exists / Directory.Exists 双双失配而误报「不存在」。归一化掉（工作区根本身除外）
        if (full.Length > Root.Length &&
            (full.EndsWith(Path.DirectorySeparatorChar) || full.EndsWith(Path.AltDirectorySeparatorChar)))
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (_fullAccess)
            return full; // full 模式：所有文件可读可写，不做沙箱检查（Path.GetFullPath 已做规范化）
        // 解析符号链接后的真实路径再检查沙箱：工作区内的 symlink 可能指向外部
        var real = ResolveRealPath(full);
        if (IsWithin(real))
            return full;
        if (allowReadOnly && _whitelistEnabled && IsInReadOnly(real))
            return full;
        throw new ToolException($"路径 '{path}' 位于工作区之外，已拒绝访问。");
    }

    /// <summary>判断真实路径是否在任一只读白名单目录内。</summary>
    private bool IsInReadOnly(string fullPath)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var (dir, prefix) in _readOnly)
        {
            if (string.Equals(fullPath, dir, cmp))
                return true;
            if (fullPath.StartsWith(prefix, cmp))
                return true;
        }
        return false;
    }

    /// <summary>目录段真实路径缓存（带 TTL）：ResolveRealPath 对每个路径逐段做符号链接解析，
    /// grep/glob 扫描上千文件时同一目录前缀被重复解析成千次——目录段缓存后只解析一次。
    /// 只缓存非末段（文件本身永远现解析，防止「删文件重建为外链」绕过沙箱）；
    /// 短 TTL 兜底目录中途被换成链接的极端时序。</summary>
    private sealed record CachedRealPath(string Real, DateTime ExpiresUtc);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedRealPath> RealPathDirCache = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private const double RealPathCacheTtlSeconds = 2;

    /// <summary>
    /// 解析路径的真实位置（从根开始逐段跟随符号链接）。只解析最深一段时，
    /// 若路径本身已存在（如 read_file 经过 symlink 目录读取一个已存在的文件），
    /// 对非链接的叶子调 ResolveLinkTarget 返回 null，中间层的链接不会被发现，沙箱可被穿越。
    /// </summary>
    private static string ResolveRealPath(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? fullPath;
        var segments = fullPath.Substring(root.Length)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        var now = DateTime.UtcNow;
        for (int i = 0; i < segments.Length; i++)
        {
            var next = Path.Combine(current, segments[i]);
            var isLast = i == segments.Length - 1;
            if (!isLast && RealPathDirCache.TryGetValue(next, out var hit) && hit.ExpiresUtc > now)
            {
                current = hit.Real; // 目录段命中缓存：跳过逐段链接解析
                continue;
            }
            string? resolved = null;
            try
            {
                FileSystemInfo info = File.Exists(next) ? new FileInfo(next) : new DirectoryInfo(next);
                resolved = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            }
            catch
            {
                // 解析失败（无权限等）按字面路径继续，后续段仍会尝试
            }
            current = resolved ?? next;
            if (!isLast)
                RealPathDirCache[next] = new CachedRealPath(current, now.AddSeconds(RealPathCacheTtlSeconds));
        }
        return current;
    }

    /// <summary>判断完整路径是否在工作区内（等于根目录也视为合法，如 path="."）。</summary>
    private bool IsWithin(string fullPath)
    {
        // Windows 文件系统大小写不敏感用 OrdinalIgnoreCase；Linux/macOS 大小写敏感必须精确匹配，
        // 否则 ../Proj/x 之类的大小写变体可绕过沙箱（/home/proj 与 /home/Proj 是两个目录）。
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(fullPath, Root, cmp))
            return true;
        return fullPath.StartsWith(_rootPrefix, cmp);
    }

    /// <summary>把绝对路径转为相对工作区的展示路径。
    /// 跨盘符（D:\ 工作区 + C:\ 文件）等无法相对化的路径原样返回——
    /// Path.GetRelativePath 对不同根会抛 ArgumentException，full 模式扫描白名单/外部目录时曾崩溃。</summary>
    public string ToRelative(string fullPath)
    {
        string rel;
        try
        {
            rel = Path.GetRelativePath(Root, fullPath);
        }
        catch (ArgumentException)
        {
            return fullPath;
        }
        // 尾部分隔符（如 dir\）会让相对路径带 \ 后缀，破坏 glob 匹配与展示；归一化掉
        rel = rel.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return rel == "." ? "" : rel;
    }
}

/// <summary>一次 Agent 会话的执行上下文。</summary>
public sealed class AgentContext
{
    public required AgentConfig Config { get; init; }
    public required Workspace Workspace { get; init; }

    /// <summary>stop 工具置位后，Agent 主循环立即结束本轮。</summary>
    public bool StopRequested { get; set; }

    /// <summary>文件修改撤销栈（/undo 命令用）。</summary>
    public UndoManager Undo { get; } = new();
}

/// <summary>工具接口：模型通过 JSON 参数调用，返回文本结果。</summary>
public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonObject Parameters { get; }
    Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct);
}

/// <summary>工具参数读取助手。</summary>
internal static class ToolArgs
{
    /// <summary>读取字符串；兼容模型把值序列化为数字/布尔的情况（如 content=123 → "123"）。</summary>
    public static string GetString(JsonObject? args, string key, string def = "")
    {
        if (args?[key] is not JsonValue v)
            return def;
        if (v.TryGetValue<string>(out var s))
            return s;
        // 非字符串值（数字/布尔/null 之外的标量）转为其 JSON 文本表示
        return v.ToJsonString().Trim('"');
    }

    /// <summary>读取整数；兼容模型把数字序列化为字符串的情况（如 "300"），
    /// 以及浮点字面量（如 10.0——部分模型坚持给整型参数发浮点）：整数值直接采用，带小数部分视为非法回默认。</summary>
    public static int GetInt(JsonObject? args, string key, int def)
    {
        if (args?[key] is not JsonValue v)
            return def;
        if (v.TryGetValue<int>(out var i))
            return i;
        if (v.TryGetValue<double>(out var d))
            return double.IsFinite(d) && d == Math.Truncate(d) && d >= int.MinValue && d <= int.MaxValue
                ? (int)d
                : def;
        if (v.TryGetValue<string>(out var s))
        {
            if (int.TryParse(s.Trim(), out var p))
                return p;
            // 字符串浮点（"10.0"）与原生浮点同一口径：只有整数值才接受
            return double.TryParse(s.Trim(), out var pd) && double.IsFinite(pd) && pd == Math.Truncate(pd)
                   && pd >= int.MinValue && pd <= int.MaxValue
                ? (int)pd
                : def;
        }
        return def;
    }

    /// <summary>读取布尔；兼容 "true"/"false"/"1"/"0"/"yes"/"no" 字符串。</summary>
    public static bool GetBool(JsonObject? args, string key, bool def)
    {
        if (args?[key] is not JsonValue v)
            return def;
        if (v.TryGetValue<bool>(out var b))
            return b;
        if (v.TryGetValue<string>(out var s))
        {
            if (s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("y", StringComparison.OrdinalIgnoreCase))
                return true;
            if (s.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("n", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return def;
    }

    /// <summary>读取一个字符串键值对对象（env 等）；key 缺失或不是对象时返回 null。</summary>
    public static Dictionary<string, string>? GetStringDict(JsonObject? args, string key)
    {
        if (args?[key] is not JsonObject obj)
            return null;
        var dict = new Dictionary<string, string>();
        foreach (var kv in obj)
        {
            if (kv.Value is JsonValue v)
                dict[kv.Key] = v.TryGetValue<string>(out var s) ? s : v.ToJsonString();
        }
        return dict.Count == 0 ? null : dict;
    }

    /// <summary>读取字符串或字符串数组（include/exclude 等）；缺失时返回 null。</summary>
    public static List<string>? GetStringList(JsonObject? args, string key)
    {
        if (args?[key] is not JsonNode node)
            return null;
        var list = new List<string>();
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0)
                    list.Add(s);
            }
        }
        else if (node is JsonValue v2 && v2.TryGetValue<string>(out var single) && single.Length > 0)
        {
            list.Add(single);
        }
        return list.Count == 0 ? null : list;
    }
}

/// <summary>工具注册表：注册、生成 ToolSpec、按名分发执行。</summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ITool tool) => _tools[tool.Name] = tool;

    public IReadOnlyList<ToolSpec> ToToolSpecs() =>
        _tools.Values.Select(t => new ToolSpec
        {
            Name = t.Name,
            Description = t.Description,
            Parameters = t.Parameters,
        }).ToList();

    public async Task<string> ExecuteAsync(string name, string argsJson, AgentContext ctx, CancellationToken ct)
    {
        if (!_tools.TryGetValue(name, out var tool))
            throw new ToolException($"未知工具: {name}（可用: {string.Join(", ", _tools.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))}）");

        JsonObject? args;
        try
        {
            // 空白参数视为空对象（与各 Provider 的 ?? "{}" 兜底一致），避免模型给空字符串时误报非法 JSON
            if (string.IsNullOrWhiteSpace(argsJson))
                args = new JsonObject();
            else
                args = JsonNode.Parse(argsJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            throw new ToolException($"工具参数不是合法 JSON: {TextUtil.Truncate(argsJson, 200)}");
        }

        return await tool.ExecuteAsync(args, ctx, ct);
    }

    /// <summary>按配置组装默认工具集。</summary>
    public static ToolRegistry CreateDefault()
    {
        var registry = new ToolRegistry();
        registry.Register(new ReadFileTool());
        registry.Register(new WriteFileTool());
        registry.Register(new EditFileTool());
        registry.Register(new ListDirectoryTool());
        registry.Register(new GlobTool());
        registry.Register(new GrepTool());
        registry.Register(new CommandTool());
        registry.Register(new BashTool());
        registry.Register(new PowerShellTool());
        registry.Register(new StopTool());
        return registry;
    }
}
