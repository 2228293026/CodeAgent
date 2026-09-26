using System;
using System.IO;
using System.Threading;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 输入行 <c>@文件</c> 引用（学自 Claude Code）。
/// </summary>
public sealed class FileMentionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-mention-" + Guid.NewGuid().ToString("N"));

    public FileMentionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void WriteFiles(params string[] rels)
    {
        foreach (var rel in rels)
        {
            var full = Path.Combine(_dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "// x");
        }
    }

    // —— 解析 ——

    [Fact]
    public void Mention_ActivatesAtLineStart()
    {
        var (active, prefix, start) = InputLine.ParseMention("@src", 4);
        Assert.True(active);
        Assert.Equal("src", prefix);
        Assert.Equal(0, start);
    }

    [Fact]
    public void Mention_ActivatesAfterSpace()
    {
        // "看这个 " 是 4 个字符（CJK 各占 1 字符），@ 落在索引 4
        var (active, prefix, start) = InputLine.ParseMention("看这个 @src/Prog", 17);
        Assert.True(active);
        Assert.Equal("src/Prog", prefix);
        Assert.Equal(4, start);
    }

    [Fact]
    public void Mention_BareAtIsActiveWithEmptyPrefix()
    {
        var (active, prefix, start) = InputLine.ParseMention("@", 1);
        Assert.True(active);
        Assert.Equal(string.Empty, prefix);
        Assert.Equal(0, start);
    }

    [Fact]
    public void Mention_IgnoresEmailLikeText()
    {
        // foo@bar 里有 @，但前面不是空白——不能弹文件菜单
        Assert.False(InputLine.ParseMention("foo@bar", 7).Active);
        Assert.False(InputLine.ParseMention("git@github.com:me/repo.git", 24).Active);
    }

    [Fact]
    public void Mention_IgnoresTextWithoutAt()
    {
        Assert.False(InputLine.ParseMention("普通输入", 4).Active);
        Assert.False(InputLine.ParseMention("", 0).Active);
    }

    [Fact]
    public void Mention_StopsAtWhitespace()
    {
        // 光标在第二个词上，第一个词里的 @ 不该生效
        Assert.False(InputLine.ParseMention("a@b c", 5).Active);
    }

    [Fact]
    public void Mention_OnlyLooksBeforeTheCursor()
    {
        // 光标不在 @ 之后 → 不激活
        Assert.False(InputLine.ParseMention("@abc", 0).Active);
        // 光标在 @ 之后但 token 还没开始写 → 激活，前缀为空
        var (active, prefix, _) = InputLine.ParseMention("@", 1);
        Assert.True(active);
        Assert.Equal(string.Empty, prefix);
    }

    [Fact]
    public void Mention_IgnoresTextAfterTheCursor()
    {
        // 光标在 @src/Program.cs 末尾（索引 15）之后是空白 → token 已结束，不激活
        Assert.False(InputLine.ParseMention("@src/Program.cs 后面还有", 16).Active);
        // 光标正好停在 token 末尾 → 激活，前缀只取到光标为止
        var (active, prefix, _) = InputLine.ParseMention("@src/Program.cs 后面还有", 15);
        Assert.True(active);
        Assert.Equal("src/Program.cs", prefix);
    }

    // —— 应用 ——

    [Fact]
    public void Apply_ReplacesTheWholeToken()
    {
        // 选中后要把整个 @src/Prog 换掉，不是只补上选中项
        Assert.Equal("看这个 src/CodeAgent/Program.cs 谢谢",
            InputLine.ApplyMention("看这个 @src/Prog 谢谢", 4, "src/CodeAgent/Program.cs"));
    }

    [Fact]
    public void Apply_ReplacesAtEndOfLine()
    {
        Assert.Equal("@a/b", InputLine.ApplyMention("@a/x", 0, "@a/b"));
    }

    [Fact]
    public void Apply_KeepsSurroundingText()
    {
        Assert.Equal("前 @x 后", InputLine.ApplyMention("前 @old 后", 2, "@x"));
    }

    // —— 文件列表 ——

    [Fact]
    public void Items_ListsWorkspaceFilesWithForwardSlashes()
    {
        WriteFiles("src/CodeAgent/Program.cs", "README.md");
        var items = InputLine.MentionItems(_dir, "", 50, CancellationToken.None);
        Assert.Contains(items, i => i.Name == "src/CodeAgent/Program.cs");
        Assert.Contains(items, i => i.Name == "README.md");
        // 路径分隔符统一成 /，跨平台显示一致
        Assert.DoesNotContain(items, i => i.Name.Contains('\\'));
    }

    [Fact]
    public void Items_MatchesPathSuffixNotJustFileName()
    {
        // 用户只打了 "Prog"，就该能命中 src/CodeAgent/Program.cs——
        // 按文件名匹配的话 @ 的价值（少打长路径）就没了
        WriteFiles("src/CodeAgent/Program.cs", "other/ProgramTests.cs");
        var items = InputLine.MentionItems(_dir, "Prog", 50, CancellationToken.None);
        Assert.Contains(items, i => i.Name == "src/CodeAgent/Program.cs");
    }

    [Fact]
    public void Items_RanksDeeperMatchesFirst()
    {
        WriteFiles("aaa/Prog.cs", "zzz/Program.cs");
        var items = InputLine.MentionItems(_dir, "Prog", 50, CancellationToken.None);
        // "aaa/Prog.cs" 命中更靠前
        Assert.Equal("aaa/Prog.cs", items[0].Name);
    }

    [Fact]
    public void Items_RespectsLimit()
    {
        WriteFiles("a1.cs", "a2.cs", "a3.cs", "a4.cs", "a5.cs");
        var items = InputLine.MentionItems(_dir, "a", 3, CancellationToken.None);
        Assert.True(items.Count <= 3);
    }

    [Fact]
    public void Items_NoMatchReturnsEmpty()
    {
        WriteFiles("a.cs");
        Assert.Empty(InputLine.MentionItems(_dir, "zzzz", 50, CancellationToken.None));
    }

    [Fact]
    public void Items_MissingRootReturnsEmptyInsteadOfThrowing()
    {
        // 目录没了不该让整个输入行崩掉
        Assert.Empty(InputLine.MentionItems(Path.Combine(_dir, "no-such-dir"), "", 50, CancellationToken.None));
        Assert.Empty(InputLine.MentionItems("", "", 50, CancellationToken.None));
    }

    [Fact]
    public void Items_RespectsCancellation()
    {
        WriteFiles("a.cs");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => InputLine.MentionItems(_dir, "", 50, cts.Token));
    }

    [Fact]
    public void Items_SkipsIgnoredDirectories()
    {
        WriteFiles("keep.cs", "bin/generated.cs", "obj/generated.cs");
        var items = InputLine.MentionItems(_dir, "", 50, CancellationToken.None);
        Assert.Contains(items, i => i.Name == "keep.cs");
        Assert.DoesNotContain(items, i => i.Name.StartsWith("bin/"));
        Assert.DoesNotContain(items, i => i.Name.StartsWith("obj/"));
    }
}
