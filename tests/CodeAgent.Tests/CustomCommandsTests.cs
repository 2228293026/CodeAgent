using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 自定义斜杠命令：把 <c>.codeagent/commands/**/*.md</c> 变成可复用提示词。
///
/// 它补的断层：此前 <c>/xxx</c> 全是内置的，用户手里那句反复敲的长指令
/// 只能每次重打，或者塞进 <c>AGENTS.md</c> 污染每一轮系统提示。
///
/// 这里守的几条都是「静默失效」的高发点：
///   · 文件被跳过却没人知道（用户会一直改文件名）；
///   · 命令名与内置命令撞车后悄悄顶掉 <c>/help</c>；
///   · 展开时把用户参数当模板再展开一次（<c>$1</c> 里再出现 <c>$2</c>）；
///   · 模板忘了占位符，用户敲的参数整个丢掉。
///
/// 所有 <c>Load</c> 调用都显式传 <c>userRoot</c>：默认值会读真实
/// <c>~/.codeagent/commands</c>，那会让测试结果取决于开发机上有没有随手放的模板
/// ——这正是那种「我这里过、CI 红」的来源。
/// </summary>
public class CustomCommandsTests : IDisposable
{
    private readonly string _root;
    private readonly string _user;

    public CustomCommandsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ca_cmd_" + Guid.NewGuid().ToString("N"));
        _user = Path.Combine(_root, "home");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_user);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }

    /// <summary>在项目级命令目录里放一个文件（<paramref name="name"/> 可带子目录）。</summary>
    private string ProjectFile(string name, string content)
    {
        var path = Path.Combine(CustomCommands.ProjectDirectory(_root), name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>在用户级命令目录里放一个文件。</summary>
    private string UserFile(string name, string content)
    {
        var path = Path.Combine(CustomCommands.UserDirectory(_user), name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private CustomCommands.Result Load(IEnumerable<string>? reserved = null) =>
        CustomCommands.Load(_root, _user, reserved);

    // ── 加载 ───────────────────────────────────────────────────────────

    [Fact]
    public void MissingDirectoriesAreSilent()
    {
        // 「没建命令目录」是常态，不是错误：不该每次启动都刷一条提示。
        var loaded = Load();

        Assert.Empty(loaded.Commands);
        Assert.Empty(loaded.Problems);
    }

    [Fact]
    public void ReadsFrontMatterAndBody()
    {
        ProjectFile("hello.md", "---\ndescription: 打个招呼\nargument-hint: <名字>\n---\n你好，$ARGUMENTS！\n");

        var command = Assert.Single(Load().Commands);

        Assert.Equal("hello", command.Name);
        Assert.Equal("打个招呼", command.Description);
        Assert.Equal("<名字>", command.ArgumentHint);
        Assert.Equal("你好，$ARGUMENTS！", command.Body);
        Assert.True(command.IsProject);
    }

    [Fact]
    public void UnknownFrontMatterKeysAreIgnored()
    {
        // front matter 是借来的格式，认不出的键不该变成错误。
        ProjectFile("x.md", "---\nmodel: opus\ndescription: 说明\nallowed-tools: Bash\n---\n正文\n");

        var loaded = Load();

        Assert.Empty(loaded.Problems);
        Assert.Equal("说明", Assert.Single(loaded.Commands).Description);
    }

    [Fact]
    public void DescriptionFallsBackToFirstContentLineWithoutHash()
    {
        ProjectFile("no-front-matter.md", "\n\n# 生成迁移脚本\n\n正文\n");

        var command = Assert.Single(Load().Commands);

        Assert.Equal("生成迁移脚本", command.Description);
        Assert.Equal(string.Empty, command.ArgumentHint);
    }

    [Fact]
    public void SubdirectoryBecomesColonName()
    {
        // 用 ':' 而不是 '/'：/db/migrate 会在命令分词那一步被拆成「命令 db、参数 migrate」。
        ProjectFile(Path.Combine("db", "Migrate.md"), "正文\n");

        var command = Assert.Single(Load().Commands);

        Assert.Equal("db:migrate", command.Name);
    }

    [Fact]
    public void NonMarkdownFilesAreIgnored()
    {
        ProjectFile("notes.txt", "正文\n");
        ProjectFile("script.ps1", "正文\n");

        Assert.Empty(Load().Commands);
    }

    // ── 被跳过的文件必须说出来 ──────────────────────────────────────────

    [Fact]
    public void ReservedNameIsSkippedWithExplanation()
    {
        // 一个 Markdown 文件悄悄顶掉 /help 是「查半天查不出原因」的故障。
        ProjectFile("help.md", "我想劫持帮助\n");

        var loaded = Load();

        Assert.Empty(loaded.Commands);
        var problem = Assert.Single(loaded.Problems);
        Assert.Contains("内置命令重名", problem);
        Assert.Contains("/help", problem);
    }

    [Fact]
    public void SameLevelDuplicateIsSkippedButReportsBothPaths()
    {
        // 同级同名只可能来自大小写差异（dup.md / DUP.md）。
        // 但大小写敏感是**文件系统属性**：Windows 与默认 macOS 上这两个名字是同一个文件，
        // 写第二遍只会覆盖第一遍，压根造不出重复。所以按能力分派，
        // 两边都断言真实行为——不 Skip（基线要求 0 跳过），也不假装测过。
        var first = ProjectFile("dup.md", "甲\n");
        var second = ProjectFile("DUP.md", "乙\n");
        var caseSensitive = !string.Equals(first, second, StringComparison.OrdinalIgnoreCase)
                            && File.ReadAllText(first) == "甲\n"
                            && File.ReadAllText(second) == "乙\n";

        var loaded = Load();

        if (caseSensitive)
        {
            Assert.Single(loaded.Commands);
            var problem = Assert.Single(loaded.Problems);
            Assert.Contains("重复", problem);
            Assert.Contains(first, problem);
            Assert.Contains(second, problem);
        }
        else
        {
            // 一个文件就该只产出一条命令，而且不许误报「重复」。
            // 这条断言守的是相反方向的 bug：去重判断写得过宽，会把正常文件也拦掉。
            Assert.Single(loaded.Commands);
            Assert.Empty(loaded.Problems);
        }
    }

    [Fact]
    public void WhitespaceInNameIsSkippedWithExplanation()
    {
        ProjectFile("my cmd.md", "正文\n");

        var loaded = Load();

        Assert.Empty(loaded.Commands);
        Assert.Contains("含空白字符", Assert.Single(loaded.Problems));
    }

    [Fact]
    public void EmptyBodyIsSkippedWithExplanation()
    {
        // 只有 front matter、没有正文 → 展开出来是空提示词，等于什么都没发。
        ProjectFile("empty.md", "---\ndescription: 只有说明\n---\n");

        var loaded = Load();

        Assert.Empty(loaded.Commands);
        Assert.Contains("正文为空", Assert.Single(loaded.Problems));
    }

    [Fact]
    public void OversizedFileIsSkippedWithExplanation()
    {
        // 模板是提示词不是数据文件；256 KB 的「命令」只可能是误放。
        ProjectFile("huge.md", new string('x', 300 * 1024));

        var loaded = Load();

        Assert.Empty(loaded.Commands);
        Assert.Contains("256 KB", Assert.Single(loaded.Problems));
    }

    [Fact]
    public void LongDescriptionIsTruncated()
    {
        ProjectFile("long.md", $"---\ndescription: {new string('说', 200)}\n---\n正文\n");

        var command = Assert.Single(Load().Commands);

        Assert.Equal(121, command.Description.Length); // 120 + 省略号
        Assert.EndsWith("…", command.Description);
    }

    // ── 优先级 ─────────────────────────────────────────────────────────

    [Fact]
    public void ProjectCommandOverridesUserCommand()
    {
        // 仓库自己的约定比全局习惯更贴近现场。
        UserFile("db.md", "用户级正文\n");
        ProjectFile("db.md", "项目级正文\n");

        var command = Assert.Single(Load().Commands);

        Assert.Equal("项目级正文", command.Body);
        Assert.True(command.IsProject);
        Assert.Empty(Load().Problems); // 跨级同名是特性，不是问题
    }

    [Fact]
    public void CommandsAreSortedByName()
    {
        ProjectFile("zeta.md", "z\n");
        ProjectFile("alpha.md", "a\n");
        ProjectFile(Path.Combine("mid", "w.md"), "m\n");

        var names = Load().Commands.Select(c => c.Name).ToList();

        Assert.Equal(["alpha", "mid:w", "zeta"], names);
    }

    // ── 查表与菜单 ─────────────────────────────────────────────────────

    [Fact]
    public void FindIsCaseInsensitive()
    {
        ProjectFile("hello.md", "正文\n");
        var loaded = Load();

        Assert.NotNull(CustomCommands.Find(loaded, "HELLO"));
        Assert.Null(CustomCommands.Find(loaded, "nope"));
    }

    [Fact]
    public void MenuEntriesCarrySlashAndArgumentHint()
    {
        ProjectFile("deploy.md", "---\ndescription: 部署\nargument-hint: <环境>\n---\n正文\n");

        var entries = CustomCommands.MenuEntries(Load().Commands);

        var entry = Assert.Single(entries);
        Assert.Equal("/deploy", entry.Name);
        Assert.Equal("部署（<环境>）", entry.Desc);
    }

    // ── 展开 ───────────────────────────────────────────────────────────

    private static CustomCommands.Command Cmd(string body) =>
        new("t", "说明", string.Empty, body, "t.md", true);

    [Fact]
    public void ExpandReplacesArgumentsPlaceholder()
    {
        Assert.Equal("改 src/a.cs 里的空指针", CustomCommands.Expand(Cmd("改 $ARGUMENTS"), "src/a.cs 里的空指针"));
    }

    [Fact]
    public void ExpandReplacesNumberedPlaceholders()
    {
        Assert.Equal("从 a 到 b", CustomCommands.Expand(Cmd("从 $1 到 $2"), "a b"));
    }

    [Fact]
    public void ExpandReplacesMissingNumberedPlaceholderWithEmpty()
    {
        // 缺参数替成空串，于是 "到 " 后面那个空格留下来——纯文本替换的固有结果。
        // 不在这里额外做空白规整：那会让 "价格 $1 元" 这类模板的排版也一起被改掉。
        Assert.Equal("从 a 到 ", CustomCommands.Expand(Cmd("从 $1 到 $2"), "a"));
    }

    [Fact]
    public void ExpandKeepsDollarEscape()
    {
        // 提示词里出现美元符号（价格、Shell 变量）很常见，$ 不能一律当占位符。
        Assert.Equal("价格 $5", CustomCommands.Expand(Cmd("价格 $$5"), string.Empty));
    }

    [Fact]
    public void ExpandLeavesUnknownDollarVerbatim()
    {
        Assert.Equal("${HOME} 与 $x", CustomCommands.Expand(Cmd("${HOME} 与 $x"), string.Empty));
    }

    [Fact]
    public void ExpandIsSinglePass()
    {
        // 参数本身带 $2 时不能再展开一次——否则「参数里有参数」会递归成灾难。
        Assert.Equal("$2", CustomCommands.Expand(Cmd("$1"), "$2 x"));
    }

    [Fact]
    public void ExpandAppendsArgumentsWhenTemplateHasNoPlaceholder()
    {
        // 模板忘写占位符时，丢掉用户输入比附在末尾更糟。
        Assert.Equal("审查这段代码\n这个函数", CustomCommands.Expand(Cmd("审查这段代码"), "这个函数"));
    }

    [Fact]
    public void ExpandDoesNotAppendWhenNoArgumentsGiven()
    {
        Assert.Equal("审查这段代码", CustomCommands.Expand(Cmd("审查这段代码"), ""));
        Assert.Equal("审查这段代码", CustomCommands.Expand(Cmd("审查这段代码"), "   "));
    }

    // ── 参数切分 ───────────────────────────────────────────────────────

    [Fact]
    public void SplitArgumentsHonorsQuotes()
    {
        Assert.Equal(["a b", "c"], CustomCommands.SplitArguments("\"a b\" c"));
        Assert.Equal(["a b", "c"], CustomCommands.SplitArguments("'a b' c"));
    }

    [Fact]
    public void SplitArgumentsRunsUnclosedQuoteToEnd()
    {
        // 报错比按字面用掉更烦人；用户下一句自己就能看出引号没闭合。
        Assert.Equal(["a b"], CustomCommands.SplitArguments("\"a b"));
    }

    [Fact]
    public void SplitArgumentsDistinguishesEmptyFromBlank()
    {
        Assert.Empty(CustomCommands.SplitArguments(""));
        Assert.Empty(CustomCommands.SplitArguments("   "));
        Assert.Equal([string.Empty], CustomCommands.SplitArguments("\"\""));
    }

    // ── REPL 接线 ──────────────────────────────────────────────────────

    private static CustomCommands.Result SetOf(params CustomCommands.Command[] commands) => new(commands, []);

    [Fact]
    public void TryExpandRewritesTheLineForACustomCommand()
    {
        var set = SetOf(new CustomCommands.Command("hello", "说明", string.Empty, "你好，$ARGUMENTS", "hello.md", true));

        Assert.True(Program.TryExpandCustomCommand("/hello 世界", set, out var expanded));
        Assert.Equal("你好，世界", expanded);
    }

    [Fact]
    public void TryExpandPassesBareCommandThroughUnchangedBody()
    {
        var set = SetOf(new CustomCommands.Command("hello", "说明", string.Empty, "固定正文", "hello.md", true));

        Assert.True(Program.TryExpandCustomCommand("/hello", set, out var expanded));
        Assert.Equal("固定正文", expanded);
    }

    [Fact]
    public void TryExpandIgnoresBuiltInAndUnknownCommands()
    {
        var set = SetOf(new CustomCommands.Command("hello", "说明", string.Empty, "正文", "hello.md", true));

        Assert.False(Program.TryExpandCustomCommand("/help", set, out var builtin));
        Assert.Equal("/help", builtin);

        Assert.False(Program.TryExpandCustomCommand("/nope", set, out var unknown));
        Assert.Equal("/nope", unknown);

        // 裸斜杠不该被当成命令名，否则按一下 / 就试图展开。
        Assert.False(Program.TryExpandCustomCommand("/", set, out _));
        // 非斜杠输入走普通请求路径。
        Assert.False(Program.TryExpandCustomCommand("hello", set, out _));
    }

    // ── 输入行的动态槽位 ───────────────────────────────────────────────

    [Fact]
    public void ExtraCommandsAreAppendedAfterBuiltIns()
    {
        try
        {
            var builtInCount = InputLine.Commands.Length;
            InputLine.SetExtraCommands([("/hello", "打招呼")]);

            // 内置命令排在前面：敲 / 时用户先看到内置的那批。
            Assert.Equal(builtInCount + 1, InputLine.AllCommands.Length);
            Assert.Equal(InputLine.Commands[0], InputLine.AllCommands[0]);
            Assert.Equal(("/hello", "打招呼"), InputLine.AllCommands[^1]);

            // 内置清单本身不被污染：CommandListSyncTests 以它为基准。
            Assert.Equal(builtInCount, InputLine.Commands.Length);
            Assert.Single(InputLine.ExtraCommands);
        }
        finally
        {
            InputLine.SetExtraCommands(null);
        }
    }

    [Fact]
    public void ClearingExtraCommandsRestoresTheBuiltInTable()
    {
        try
        {
            InputLine.SetExtraCommands([("/hello", "打招呼")]);
            InputLine.SetExtraCommands(null);

            // 重载后文件被删掉的情形必须真的消失，而不是留着上一批。
            Assert.Empty(InputLine.ExtraCommands);
            Assert.Same(InputLine.Commands, InputLine.AllCommands);
        }
        finally
        {
            InputLine.SetExtraCommands(null);
        }
    }
}
