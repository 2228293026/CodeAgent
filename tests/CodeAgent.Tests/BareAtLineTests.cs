using System;
using System.IO;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 光敲一个 <c>@</c> 的处理。
///
/// 与裸叹号同源，但**后果更糟**：实测模型收到一条只有 <c>@</c> 的消息时，
/// 不会说"你的消息是空的"，它会**替用户猜一个任务并作答**——
/// 「告诉我目标，我来先探索代码再动手」外加一串建议。手滑敲出一个 @，
/// 换回一段看着像真答案的输出，那比"消息是空的"有害得多：
/// 用户可能以为它理解了某个没说出口的需求。
/// </summary>
public sealed class BareAtLineTests
{
    [Theory]
    [InlineData("@")]
    [InlineData("@   ")]
    [InlineData("  @  ")]
    public void BareAt_IsRecognized(string line)
    {
        Assert.True(Program.IsBareAtLine(line));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("你好")]
    [InlineData("email@example.com")]
    [InlineData("a@b")]
    [InlineData("@src/Program.cs")]
    [InlineData("看看 @src/Program.cs 里的逻辑")]
    public void OtherLines_AreNotBareAt(string line)
    {
        Assert.False(Program.IsBareAtLine(line));
    }

    [Fact]
    public void EmailLikeTextIsNotMistakenForAMention()
    {
        // 整行就是一个邮箱时不能弹提示——那会把正常输入判成手滑
        Assert.False(Program.IsBareAtLine("someone@example.com"));
    }

    [Fact]
    public void AtMention_StillWorks()
    {
        // 前提没被破坏：真正的 @ 引用必须照常发给模型展开
        Assert.False(Program.IsBareAtLine("@README.md"));
        Assert.False(Program.IsBareAtLine("修一下 @Program.cs 的 bug"));
    }

    [Fact]
    public void Hint_ShowsAConcreteExample()
    {
        var hint = Program.FormatBareAtHint();
        Assert.Contains("@src/Program.cs", hint);
    }

    [Fact]
    public void Hint_MentionsTabCompletion()
    {
        Assert.Contains("Tab", Program.FormatBareAtHint());
    }

    [Fact]
    public void Hint_IsBoundedWhenGivenAWidth()
    {
        for (var w = 8; w <= 120; w += 4)
            Assert.True(TextUtil.DisplayWidth(Program.FormatBareAtHint(w)) <= w);
    }

    [Fact]
    public void Hint_UnknownWidthIsUntrimmed()
    {
        Assert.Equal(Program.FormatBareAtHint(0), Program.FormatBareAtHint());
    }

    [Fact]
    public void BangAndAtAreMutuallyExclusive()
    {
        // 两者都以"后面是空白"为特征，实现稍有偏差就会互相误判
        foreach (var line in new[] { "!", "@", "!  ", "@  ", "  ! ", "  @ " })
        {
            var bang = Program.IsBareBangLine(line);
            var at = Program.IsBareAtLine(line);
            Assert.True(bang ^ at, $"「{line}」同时/都不命中：bang={bang} at={at}");
        }
    }

    [Fact]
    public void ReplHandlesItLocally()
    {
        var src = File.ReadAllText(Find());
        Assert.Contains("if (Program.IsBareAtLine(line))", src);
        var at = src.IndexOf("if (Program.IsBareAtLine(line))", StringComparison.Ordinal);
        var window = src.Substring(at, Math.Min(300, src.Length - at));
        Assert.Contains("continue", window);
    }

    [Fact]
    public void AtCheckRunsAfterTheBangCheck()
    {
        var src = File.ReadAllText(Find());
        var bangAt = src.IndexOf("if (Program.IsBareBangLine(line))", StringComparison.Ordinal);
        var atAt = src.IndexOf("if (Program.IsBareAtLine(line))", StringComparison.Ordinal);
        Assert.True(bangAt > 0 && atAt > bangAt);
    }

    private static string Find()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = Path.Combine(dir, "src", "CodeAgent", "Program.cs");
                if (File.Exists(f) && new FileInfo(f).Length > 1000)
                    return f;
                var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new FileNotFoundException("找不到 src/CodeAgent/Program.cs");
    }
}
