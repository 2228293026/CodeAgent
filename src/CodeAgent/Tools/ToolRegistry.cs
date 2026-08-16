using System.Text.Json;
using System.Text.Json.Nodes;
using CodeAgent.Providers;

namespace CodeAgent.Tools;

/// <summary>工具执行失败（应把消息返回给模型而非抛出到外层）。</summary>
public sealed class ToolException : Exception
{
    public ToolException(string message) : base(message) { }
}

/// <summary>工作区路径沙箱：工具只能访问工作区内的路径。</summary>
public sealed class Workspace
{
    private readonly string _rootPrefix;

    public Workspace(string root)
    {
        Root = Path.GetFullPath(root);
        _rootPrefix = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                      + Path.DirectorySeparatorChar;
    }

    /// <summary>规范化的工作区根路径。</summary>
    public string Root { get; }

    /// <summary>把相对路径解析为工作区内的绝对路径；越界或非法则抛 ToolException。</summary>
    public string Resolve(string? path)
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
        // 解析符号链接后的真实路径再检查沙箱：工作区内的 symlink 可能指向外部
        if (!IsWithin(ResolveRealPath(full)))
            throw new ToolException($"路径 '{path}' 位于工作区之外，已拒绝访问。");
        return full;
    }

    /// <summary>
    /// 解析路径的真实位置（跟随符号链接）。逐级向上找到最深已存在的组件，
    /// 解析其链接目标后拼回未存在的尾部，用于沙箱越界检查。
    /// </summary>
    private static string ResolveRealPath(string fullPath)
    {
        var current = fullPath;
        var tail = new Stack<string>();
        while (!File.Exists(current) && !Directory.Exists(current))
        {
            var name = Path.GetFileName(current);
            if (name.Length == 0)
                break;
            tail.Push(name);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
                break;
            current = parent;
        }

        try
        {
            FileSystemInfo info = File.Exists(current)
                ? new FileInfo(current)
                : new DirectoryInfo(current);
            var target = info.ResolveLinkTarget(true);
            if (target is not null)
                current = target.FullName;
        }
        catch
        {
            // 无法解析时按字面路径处理（IsWithin 仍会拦截明显的越界）
        }

        foreach (var seg in tail)
            current = Path.Combine(current, seg);
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

    /// <summary>把绝对路径转为相对工作区的展示路径。</summary>
    public string ToRelative(string fullPath)
    {
        var rel = Path.GetRelativePath(Root, fullPath);
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

    /// <summary>读取整数；兼容模型把数字序列化为字符串的情况（如 "300"）。</summary>
    public static int GetInt(JsonObject? args, string key, int def)
    {
        if (args?[key] is not JsonValue v)
            return def;
        if (v.TryGetValue<int>(out var i))
            return i;
        return v.TryGetValue<string>(out var s) && int.TryParse(s, out var p) ? p : def;
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
            throw new ToolException($"未知工具: {name}（可用: {string.Join(", ", _tools.Keys)}）");

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
