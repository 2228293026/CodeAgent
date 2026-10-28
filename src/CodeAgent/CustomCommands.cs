using System.Text;

namespace CodeAgent;

/// <summary>
/// 自定义斜杠命令：把 <c>.codeagent/commands/**/*.md</c> 当成可复用的提示词模板。
///
/// 学自 Claude Code 的 custom slash commands。它填的是一个真实断层：
/// 此前的 <c>/xxx</c> 全部是**内置**的，用户手里那句「每次都差不多的长指令」
/// 只能反复敲，或者塞进 <c>AGENTS.md</c> 里污染每一轮的系统提示。
/// 模板文件让「常用提示词」变成和内置命令同一等公民——输入 <c>/</c> 就进补全菜单。
///
/// 三个刻意的取舍：
///   · **只认 Markdown**，不引入第二种配置格式。模板是写给人读的提示词，
///     Markdown 正文就是它本身，front matter 只借来放两行元数据。
///   · **纯文本替换，不调模型**。展开在毫秒级完成、结果可测；
///     让模型「理解」模板再改写它，等于每敲一次命令付一轮 token 买不确定性。
///   · **项目级盖住用户级**。同名时仓库里那份赢——
///     仓库自己的约定比全局习惯更贴近现场。
/// </summary>
public static class CustomCommands
{
    /// <summary>命令目录名。项目级 <c>./.codeagent/commands</c>，用户级 <c>~/.codeagent/commands</c>。</summary>
    public const string DirectoryName = "commands";

    /// <summary>递归深度上限。命令目录正常只有一两层；
    /// 没有上限时一个手滑的 <c>ln -s . loop</c> 就能让加载卡死。</summary>
    private const int MaxDepth = 8;

    /// <summary>单个模板文件的大小上限。模板是提示词，不是数据文件；
    /// 几百 KB 的「命令」只可能是误放，读进来也只会撑爆上下文。</summary>
    private const long MaxFileBytes = 256L * 1024;

    /// <summary>取给菜单/帮助用的说明上限。说明是一列宽度的配角，
    /// 太长会被截，不如在这里就先收住，免得两侧截断规则打架。</summary>
    private const int MaxDescriptionLength = 120;

    /// <summary>一条自定义命令。
    ///
    /// <paramref name="Name"/> 不含前导斜杠、已小写、子目录用 <c>:</c> 连接
    /// （<c>db/migrate.md</c> → <c>db:migrate</c>，即 <c>/db:migrate</c>）。
    /// 用 <c>:</c> 而不是 <c>/</c>：<c>/db/migrate</c> 在命令分词那一步就会被
    /// 拆成「命令 db、参数 migrate」，用户拿不到自己写的参数。</summary>
    public sealed record Command(
        string Name,
        string Description,
        string ArgumentHint,
        string Body,
        string SourcePath,
        bool IsProject);

    /// <summary>加载结果。<paramref name="Problems"/> 是**给人看**的中文说明：
    /// 被跳过的文件不能只是「不见了」，否则用户会以为功能没生效。</summary>
    public sealed record Result(IReadOnlyList<Command> Commands, IReadOnlyList<string> Problems);

    /// <summary>项目级命令目录（相对当前工作目录，与 <c>.codeagent/history.txt</c> 同一约定）。</summary>
    public static string ProjectDirectory(string projectRoot) =>
        Path.Combine(projectRoot, ".codeagent", DirectoryName);

    /// <summary>用户级命令目录。<paramref name="userRoot"/> 为 null 时取用户主目录；
    /// 取不到主目录（极简容器）时返回空串，调用方据此跳过这一级。</summary>
    public static string UserDirectory(string? userRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(userRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userRoot;
        return string.IsNullOrWhiteSpace(root)
            ? string.Empty
            : Path.Combine(root, ".codeagent", DirectoryName);
    }

    /// <summary>
    /// 扫描两级目录并解析出全部自定义命令。
    ///
    /// <paramref name="reservedNames"/> 是内置命令名（含前导斜杠也可），
    /// 默认取 <see cref="InputLine.Commands"/>。重名的自定义命令会被跳过而不是覆盖内置的：
    /// 让一个 Markdown 文件悄悄改写 <c>/help</c> 的行为，是那种「查半天查不出原因」的故障。
    /// </summary>
    public static Result Load(string projectRoot, string? userRoot = null, IEnumerable<string>? reservedNames = null)
    {
        var problems = new List<string>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { string.Empty };
        foreach (var name in reservedNames ?? InputLine.Commands.Select(c => c.Name))
            reserved.Add(name.TrimStart('/'));

        var byName = new Dictionary<string, Command>(StringComparer.OrdinalIgnoreCase);

        // 先用户级、后项目级：后写覆盖先写，于是「项目级盖住用户级」是赋值顺序的自然结果，
        // 不需要再写一遍优先级分支。
        LoadLevel(UserDirectory(userRoot), false, reserved, byName, problems);
        LoadLevel(ProjectDirectory(projectRoot), true, reserved, byName, problems);

        var commands = byName.Values
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new Result(commands, problems);
    }

    /// <summary>按名字查一条命令。名字与 <see cref="Program.SplitCommand"/> 的输出都已小写，
    /// 但仍按忽略大小写比较：调用方不必记住这个约定。</summary>
    public static Command? Find(Result set, string name)
    {
        foreach (var command in set.Commands)
        {
            if (string.Equals(command.Name, name, StringComparison.OrdinalIgnoreCase))
                return command;
        }
        return null;
    }

    /// <summary>转成补全菜单/帮助表要的 (名称, 说明) 形状。
    /// 有 <c>argument-hint</c> 时并进说明里——菜单只有一列说明，
    /// 再单开一列「参数」会把 9 行的面板挤掉两行。</summary>
    public static (string Name, string Desc)[] MenuEntries(IReadOnlyList<Command> commands)
    {
        var rows = new (string Name, string Desc)[commands.Count];
        for (var i = 0; i < commands.Count; i++)
        {
            var command = commands[i];
            var desc = command.ArgumentHint.Length > 0
                ? $"{command.Description}（{command.ArgumentHint}）"
                : command.Description;
            rows[i] = ("/" + command.Name, desc);
        }
        return rows;
    }

    /// <summary>
    /// 把模板里的占位符换成实参。**单遍替换**：替换进去的内容不再被展开，
    /// 否则一次 <c>$1</c> 里带个 <c>$2</c> 就能引出「参数里有参数」的递归噩梦。
    ///
    /// 支持的写法：<c>$ARGUMENTS</c>（整串）、<c>$1</c>..<c>$9</c>（按空白切分，缺的替空串）、
    /// <c>$$</c>（一个字面 <c>$</c>）。其余 <c>$</c> 原样保留——
    /// 提示词里出现美元符号的场景（价格、Shell 变量）比想象中多。
    /// 模板里一个占位符都没有而用户又给了参数时，参数换行附在末尾：
    /// 这是「模板忘写占位符」唯一合理的补救，丢掉用户输入更糟。
    /// </summary>
    public static string Expand(Command command, string arguments)
    {
        var body = command.Body;
        var parts = SplitArguments(arguments);
        var replaced = false;
        var output = new StringBuilder(body.Length + arguments.Length + 8);

        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c != '$' || i + 1 >= body.Length)
            {
                output.Append(c);
                continue;
            }

            var next = body[i + 1];
            if (next == '$')
            {
                output.Append('$');
                i++;
                continue;
            }
            if (next is >= '1' and <= '9')
            {
                replaced = true;
                var index = next - '1';
                if (index < parts.Count)
                    output.Append(parts[index]);
                i++;
                continue;
            }
            if (body.AsSpan(i + 1).StartsWith("ARGUMENTS", StringComparison.OrdinalIgnoreCase))
            {
                replaced = true;
                output.Append(arguments);
                i += 1 + "ARGUMENTS".Length;
                continue;
            }
            output.Append(c);
        }

        if (!replaced && !string.IsNullOrWhiteSpace(arguments))
            output.Append('\n').Append(arguments.Trim());

        return output.ToString();
    }

    /// <summary>
    /// 按空白切分实参，尊重成对的 <c>"</c> / <c>'</c>。
    /// 引号未闭合时取到结尾——报错比按字面用掉更烦人，用户下一句就能自己看出来。
    /// 空串产生空列表；<c>""</c> 产生一个空参数（与 Shell 一致）。
    /// </summary>
    public static IReadOnlyList<string> SplitArguments(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var started = false;
        char? quote = null;

        foreach (var c in text)
        {
            if (quote is not null)
            {
                if (c == quote) quote = null;
                else current.Append(c);
                continue;
            }
            if (c is '"' or '\'')
            {
                quote = c;
                started = true;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (started)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
                continue;
            }
            current.Append(c);
            started = true;
        }

        if (started) parts.Add(current.ToString());
        return parts;
    }

    /// <summary>扫描一级目录。不存在就安静跳过——绝大多数用户不会建这个目录，
    /// 「没有命令目录」是常态而不是问题。</summary>
    private static void LoadLevel(
        string directory,
        bool isProject,
        HashSet<string> reserved,
        Dictionary<string, Command> byName,
        List<string> problems)
    {
        if (directory.Length == 0 || !Directory.Exists(directory))
            return;

        var files = new List<string>();
        CollectFiles(directory, 0, files, problems);

        foreach (var file in files)
        {
            var name = NameFor(directory, file, problems);
            if (name.Length == 0)
                continue;

            if (reserved.Contains(name))
            {
                problems.Add($"{file}：命令名 /{name} 与内置命令重名，已跳过");
                continue;
            }

            // 同级同名只可能来自大小写差异（Foo.md / foo.md）。
            // 跨级同名是**特性**（项目级盖住用户级），所以只比同一级。
            if (byName.TryGetValue(name, out var existing) && existing.IsProject == isProject)
            {
                problems.Add($"{file}：命令名 /{name} 与 {existing.SourcePath} 重复，已跳过");
                continue;
            }

            var command = ParseFile(file, name, isProject, problems);
            if (command is null)
                continue;

            byName[name] = command;
        }
    }

    /// <summary>相对路径 → 命令名。<c>db/migrate.md</c> → <c>db:migrate</c>。</summary>
    private static string NameFor(string directory, string file, List<string> problems)
    {
        var relative = Path.GetRelativePath(directory, file);
        var parts = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return string.Empty;

        parts[^1] = parts[^1][..^3]; // 去掉 ".md"（CollectFiles 已保证后缀）
        var name = string.Join(':', parts).ToLowerInvariant();

        if (name.Length == 0)
        {
            problems.Add($"{file}：文件名无法转成命令名，已跳过");
            return string.Empty;
        }
        // 空格会让 /db migrate 被拆成「命令 db、参数 migrate」，
        // 于是这个文件永远不可能被调到——与其静默，不如报出来。
        if (name.Any(char.IsWhiteSpace))
        {
            problems.Add($"{file}：命令名 /{name} 含空白字符，已跳过");
            return string.Empty;
        }
        return name;
    }

    /// <summary>解析一个模板文件。任何一步失败都只记问题、返回 null，
    /// 不让一个坏文件带走整批命令。</summary>
    private static Command? ParseFile(string path, string name, bool isProject, List<string> problems)
    {
        string text;
        try
        {
            var length = new FileInfo(path).Length;
            if (length > MaxFileBytes)
            {
                problems.Add($"{path}：文件超过 {MaxFileBytes / 1024} KB，已跳过");
                return null;
            }
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            problems.Add($"{path}：读取失败（{ex.Message}），已跳过");
            return null;
        }

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var start = 0;
        string? description = null;
        string? argumentHint = null;

        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            var end = -1;
            for (var i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---")
                {
                    end = i;
                    break;
                }
            }
            if (end > 0)
            {
                for (var i = 1; i < end; i++)
                {
                    var colon = lines[i].IndexOf(':');
                    if (colon <= 0)
                        continue;
                    var key = lines[i][..colon].Trim();
                    var value = lines[i][(colon + 1)..].Trim().Trim('"', '\'');
                    if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
                        description = value;
                    else if (key.Equals("argument-hint", StringComparison.OrdinalIgnoreCase)
                             || key.Equals("argument_hint", StringComparison.OrdinalIgnoreCase))
                        argumentHint = value;
                    // 其余键忽略：front matter 是外来格式，认不出的键不该变成错误
                }
                start = end + 1;
            }
        }

        var body = string.Join('\n', lines[start..]).TrimStart('\n').TrimEnd();
        if (body.Length == 0)
        {
            problems.Add($"{path}：正文为空，已跳过");
            return null;
        }

        description ??= FirstLineAsDescription(body) ?? name;
        if (description.Length > MaxDescriptionLength)
            description = description[..MaxDescriptionLength] + "…";

        return new Command(name, description, argumentHint ?? string.Empty, body, path, isProject);
    }

    /// <summary>没有 front matter 时，说明取正文第一个非空行（去掉 Markdown 标题的 <c>#</c>）。
    /// 一个只写了正文的文件不该在菜单里显示成一条空白说明。</summary>
    private static string? FirstLineAsDescription(string body)
    {
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim().TrimStart('#').Trim();
            if (line.Length > 0)
                return line;
        }
        return null;
    }

    /// <summary>有界递归收集 <c>*.md</c>。跳过重解析点（符号链接/联接）目录：
    /// 它们能把「一层目录」变成「无限深的同一层」。排序保证加载顺序确定——
    /// 冲突问题上报的先后不该随文件系统变化。</summary>
    private static void CollectFiles(string directory, int depth, List<string> files, List<string> problems)
    {
        if (depth > MaxDepth)
        {
            problems.Add($"{directory}：目录层级超过 {MaxDepth} 层，已跳过");
            return;
        }

        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (Exception ex)
        {
            problems.Add($"{directory}：无法读取目录（{ex.Message}），已跳过");
            return;
        }

        Array.Sort(entries, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(entry);
            }
            catch
            {
                continue; // 列出来之后又消失了（并发删除）：跳过比中断整批好
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                CollectFiles(entry, depth + 1, files, problems);
                continue;
            }

            if (entry.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                files.Add(entry);
        }
    }
}
