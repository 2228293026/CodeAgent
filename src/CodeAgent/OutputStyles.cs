using System.Text;

namespace CodeAgent;

/// <summary>
/// 输出风格（学自 Claude Code 的 output styles）：一份可切换的系统提示片段。
///
/// 它填的是真实断层：想「解释着做」「边做边教」只能整段改写 systemPrompt，
/// 而 systemPrompt 一改就丢掉默认提示里的工具约定与安全边界——用户要么忍，
/// 要么自己维护一份必然过期的全量提示。风格是**追加**，不是替换。
///
/// 内置三档（default / explanatory / learning）+ 两级自定义目录
/// （项目 .codeagent/output-styles、用户 ~/.codeagent/output-styles），
/// 同名时项目级盖住用户级。纯文件解析，不调用模型。
/// </summary>
public static class OutputStyles
{
    public const string DirectoryName = "output-styles";

    public const string DefaultName = "default";

    private const long MaxFileBytes = 64L * 1024;

    private const int MaxDescriptionLength = 120;

    /// <summary>一种输出风格。Body 为空表示「不追加任何内容」（即 default）。</summary>
    public sealed record Style(string Name, string Description, string Body, string SourcePath, bool IsBuiltin, bool IsProject = false);

    /// <summary>加载结果：风格列表 + 逐条问题（坏文件不静默丢弃）。</summary>
    public sealed record Result(IReadOnlyList<Style> Styles, IReadOnlyList<string> Problems);

    /// <summary>内置风格。default 的 Body 是空串：它等于「什么都不额外说」。</summary>
    public static IReadOnlyList<Style> Builtin { get; } =
    [
        new Style(DefaultName, "默认：直接做事，只在需要时简短说明", string.Empty, string.Empty, true),
        new Style(
            "explanatory",
            "解释：动手前后说明做了什么、为什么这么做",
            "在完成用户请求的同时解释你的思路：动手前简要说明打算怎么做、为什么这么选；"
            + "遇到取舍时说明放弃了什么；改动文件后点出关键改动点。解释要短，不要复述代码。",
            string.Empty,
            true),
        new Style(
            "learning",
            "学习：把任务变成一边做一边讲的教学",
            "把任务当作教学：先给出一个小而完整的可运行步骤，说明这一步为什么这样写；"
            + "再让用户自己动手补一小段（留下 TODO 标记），用户完成后继续。"
            + "不要一次给出全部实现，也不要把简单问题讲成教科书。",
            string.Empty,
            true),
    ];

    /// <summary>项目级风格目录：&lt;项目&gt;/.codeagent/output-styles。</summary>
    public static string ProjectDirectory(string projectRoot) =>
        Path.Combine(projectRoot, ".codeagent", DirectoryName);

    /// <summary>用户级风格目录：~/.codeagent/output-styles；取不到 home 时返回空串。</summary>
    public static string UserDirectory(string? userRoot = null)
    {
        var home = userRoot;
        if (string.IsNullOrWhiteSpace(home))
        {
            try
            {
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            catch
            {
                return string.Empty;
            }
        }
        return string.IsNullOrWhiteSpace(home) ? string.Empty : Path.Combine(home, ".codeagent", DirectoryName);
    }

    /// <summary>加载：内置 → 用户级 → 项目级（后者按同名覆盖前者）。</summary>
    public static Result Load(string projectRoot, string? userRoot = null)
    {
        var byName = new Dictionary<string, Style>(StringComparer.OrdinalIgnoreCase);
        foreach (var builtin in Builtin)
            byName[builtin.Name] = builtin;

        var problems = new List<string>();
        LoadDirectory(UserDirectory(userRoot), false, byName, problems);
        LoadDirectory(ProjectDirectory(projectRoot), true, byName, problems);

        var styles = byName.Values
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new Result(styles, problems);
    }

    /// <summary>按名查找（大小写不敏感）；找不到时返回 null，由调用方决定怎么报。</summary>
    public static Style? Find(Result library, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return library.Styles.FirstOrDefault(s => s.Name.Equals(DefaultName, StringComparison.OrdinalIgnoreCase));
        var wanted = name.Trim();
        return library.Styles.FirstOrDefault(s => s.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 把风格片段**追加**到基础系统提示后面。
    /// 空片段直接返回原串（不产生多余空行，默认风格下提示内容与不装风格完全一致）。
    /// </summary>
    public static string Append(string basePrompt, string? styleBody)
    {
        if (string.IsNullOrWhiteSpace(styleBody))
            return basePrompt;
        return basePrompt.TrimEnd()
            + "\n\n[输出风格]\n"
            + styleBody.Trim()
            + "\n[/输出风格]";
    }

    private static void LoadDirectory(
        string directory,
        bool project,
        Dictionary<string, Style> byName,
        List<string> problems)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            return;

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*.md", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            problems.Add($"{directory}：无法读取目录（{ex.Message}），已跳过");
            return;
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var style = ParseFile(file, project, problems);
            if (style is null)
                continue;
            // 内置风格不许被文件顶掉：内置档是「我知道它一定在」的那一份。
            if (byName.TryGetValue(style.Name, out var existing) && existing.IsBuiltin)
            {
                problems.Add($"{file}：与内置风格 {style.Name} 重名，已跳过");
                continue;
            }
            // 同一级目录里出现同名（大小写不同）时只可能有一个生效，必须说出来。
            // 跨级同名是有意的「项目盖用户」，不报。
            if (existing is not null && !existing.IsBuiltin && existing.IsProject == project)
                problems.Add($"{file}：与 {existing.SourcePath} 风格名重复，已跳过");
            else
                byName[style.Name] = style;
        }
    }

    private static Style? ParseFile(string path, bool project, List<string> problems)
    {
        try
        {
            if (new FileInfo(path).Length > MaxFileBytes)
            {
                problems.Add($"{path}：文件超过 {MaxFileBytes / 1024} KB，已跳过");
                return null;
            }
            var text = File.ReadAllText(path).Replace("\r\n", "\n").Replace('\r', '\n');
            var (frontMatter, body) = SplitFrontMatter(text);

            var description = "";
            foreach (var raw in frontMatter)
            {
                var idx = raw.IndexOf(':');
                if (idx <= 0)
                    continue;
                var key = raw[..idx].Trim();
                var value = raw[(idx + 1)..].Trim().Trim('"', '\'');
                if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
                    description = value;
            }

            var name = Path.GetFileNameWithoutExtension(path).Trim();
            if (name.Length == 0)
            {
                problems.Add($"{path}：文件名为空，已跳过");
                return null;
            }
            var content = body.Trim();
            if (content.Length == 0)
            {
                problems.Add($"{path}：正文为空，已跳过");
                return null;
            }
            if (description.Length == 0)
                description = FirstLine(content) ?? name;
            if (description.Length > MaxDescriptionLength)
                description = description[..MaxDescriptionLength] + "…";
            return new Style(name, description, content, path, false, project);
        }
        catch (Exception ex)
        {
            problems.Add($"{path}：读取失败（{ex.Message}），已跳过");
            return null;
        }
    }

    private static (List<string> FrontMatter, string Body) SplitFrontMatter(string text)
    {
        var lines = text.Split('\n');
        var frontMatter = new List<string>();
        if (lines.Length == 0 || !lines[0].Trim().Equals("---", StringComparison.Ordinal))
            return (frontMatter, text);

        var end = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (!lines[i].Trim().Equals("---", StringComparison.Ordinal))
                continue;
            end = i;
            break;
        }
        if (end < 0)
            return (frontMatter, text);

        for (var i = 1; i < end; i++)
            frontMatter.Add(lines[i]);
        return (frontMatter, string.Join('\n', lines[(end + 1)..]));
    }

    private static string? FirstLine(string description)
    {
        foreach (var line in description.Split('\n'))
        {
            var trimmed = line.Trim().TrimStart('#').Trim();
            if (trimmed.Length > 0)
                return trimmed;
        }
        return null;
    }
}
