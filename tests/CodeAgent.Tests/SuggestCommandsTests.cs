using System;
using System.Linq;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 拼错命令时的最接近候选。
///
/// 此前只说「未知命令: /statuss（输入 /help 查看命令）」——而用户敲
/// <c>/statuss</c> 时要的就是 <c>/status</c>，命令列表就在 <c>/help</c> 里躺着，
/// 让他自己找一遍纯属白费工夫。
/// </summary>
public sealed class SuggestCommandsTests
{
    private static readonly string[] All = Program.ReplCommands.Select(c => c.Command).ToArray();

    [Theory]
    [InlineData("/statuss", "/status")]
    [InlineData("/task", "/tasks")]
    [InlineData("/undoo", "/undo")]
    [InlineData("/stat", "/status")]
    public void Typos_GetTheIntendedCommand(string input, string expected)
    {
        Assert.Contains(expected, Program.SuggestCommands(input, All));
    }

    [Fact]
    public void Suggestions_KeepTheLeadingSlash()
    {
        // 距离用归一化形式算（//exit 与 /exit 同义），但**显示**必须带斜杠——
        // 用户要敲的是 /status，不是 status
        foreach (var s in Program.SuggestCommands("/statuss", All))
            Assert.StartsWith("/", s);
    }

    [Fact]
    public void PartialCommand_IsTreatedAsAPrefixMatch()
    {
        // /stat → /status 是**命令打到一半**，不是拼错：编辑距离是 2，
        // 按距离阈值会被判成"差得远"而丢掉——那正好是最该提示的场景。
        var s = Program.SuggestCommands("/stat", All);
        Assert.Contains("/status", s);
        Assert.Contains("/stats", s);
    }

    [Fact]
    public void BundledEntry_OffersEachNameSeparately()
    {
        // 帮助里写的是「/exit, /quit」一条；不拆开的话 /exi 找不到 /exit
        Assert.Contains("/exit", Program.SuggestCommands("/exi", All));
        Assert.Contains("/quit", Program.SuggestCommands("/qui", All));
    }

    [Fact]
    public void UnrelatedInput_GetsNoSuggestion()
    {
        // 「/zzzz 是不是想 /undo？」是纯粹的噪音——宁可不说
        Assert.Empty(Program.SuggestCommands("/zzzzzz", All));
    }

    [Fact]
    public void EmptyInput_GetsNoSuggestion()
    {
        Assert.Empty(Program.SuggestCommands("", All));
        Assert.Empty(Program.SuggestCommands("/", All));
    }

    [Fact]
    public void DoubleSlashIsTreatedTheSame()
    {
        // 全角/双斜杠归一化后与单斜杠同义
        Assert.Contains("/exit", Program.SuggestCommands("//exit", All));
    }

    [Fact]
    public void ExactMatch_DistanceZeroAndFirst()
    {
        var s = Program.SuggestCommands("/stats", All);
        Assert.NotEmpty(s);
        Assert.Equal("/stats", s[0]);
    }

    [Fact]
    public void ResultsAreDeterministic()
    {
        // 同距离按字典序——否则每次提示顺序不同，像在随机
        for (var i = 0; i < 5; i++)
            Assert.Equal(Program.SuggestCommands("/statuss", All), Program.SuggestCommands("/statuss", All));
    }

    [Fact]
    public void TiesAreListedInStableOrder()
    {
        // /statuss 同时接近 /status 与 /stats：两个都该给，顺序固定
        var s = Program.SuggestCommands("/statuss", All);
        Assert.Contains("/status", s);
        Assert.Contains("/stats", s);
    }

    [Fact]
    public void SuggestionsAreCapped()
    {
        Assert.True(Program.SuggestCommands("/statuss", All, maxSuggestions: 1).Length <= 1);
        Assert.True(Program.SuggestCommands("/statuss", All, maxSuggestions: 99).Length <= 99);
    }

    [Fact]
    public void DuplicateCommandNames_AreNotListedTwice()
    {
        var dupes = new[] { "/a", "/a", "/ab" };
        var s = Program.SuggestCommands("/a", dupes);
        Assert.Equal(s.Distinct(StringComparer.OrdinalIgnoreCase).Count(), s.Length);
    }

    [Fact]
    public void EmptyCommandList_ProducesNothing()
    {
        Assert.Empty(Program.SuggestCommands("/status", []));
    }

    [Fact]
    public void UnknownCommandMessage_UsesTheSuggestion()
    {
        var src = System.IO.File.ReadAllText(Find());
        Assert.Contains("你是不是想输入", src);
        // 没有候选时必须回落到原来的「/help + Tab」提示，不能变成一句空问句
        Assert.Contains("（输入 /help 查看命令，Tab 可补全）", src);
    }

    [Fact]
    public void SuggestionSeparator_DoesNotCollideWithTheSlash()
    {
        // 候选项自带 / 前缀，用 " / " 分隔会读成 "/status / /stats"
        var src = System.IO.File.ReadAllText(Find());
        Assert.DoesNotContain("""你是不是想输入 {string.Join(" / ", near)}""", src);
    }

    private static string Find()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = start;
            for (var i = 0; i < 10 && dir.Length > 1; i++)
            {
                var f = System.IO.Path.Combine(dir, "src", "CodeAgent", "Program.cs");
                if (System.IO.File.Exists(f) && new System.IO.FileInfo(f).Length > 1000)
                    return f;
                var parent = System.IO.Path.GetDirectoryName(dir.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(parent) || parent == dir)
                    break;
                dir = parent;
            }
        }
        throw new System.IO.FileNotFoundException("找不到 src/CodeAgent/Program.cs");
    }
}
