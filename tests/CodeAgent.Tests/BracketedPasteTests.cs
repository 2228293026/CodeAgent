using Xunit;

namespace CodeAgent.Tests;

/// <summary>括号粘贴标记探测状态机（ESC[200~ / ESC[201~）。</summary>
public class BracketedPasteTests
{
    private static PasteMarkerResult FeedAll(string chars)
    {
        var d = new PasteMarkerDetector();
        foreach (var c in chars)
            d.Feed(c);
        return d.Result;
    }

    [Fact]
    public void FullStartMarker_Detected() =>
        Assert.Equal(PasteMarkerResult.Start, FeedAll("\x1b[200~"));

    [Fact]
    public void FullEndMarker_Detected() =>
        Assert.Equal(PasteMarkerResult.End, FeedAll("\x1b[201~"));

    [Fact]
    public void LoneEsc_InProgressNotFailed()
    {
        // 用户单按 ESC：序列未完成（InProgress），非 Failed——调用方稍等无后续键后放回，走正常 ESC 处理
        var d = new PasteMarkerDetector();
        d.Feed('\x1b');
        Assert.True(d.InProgress);
        Assert.False(d.Failed);
        Assert.Equal(PasteMarkerResult.None, d.Result);
    }

    [Theory]
    [InlineData("\x1b[20x~")]  // 中途错字符
    [InlineData("\x1bX200~")]  // 第二键不是 [
    [InlineData("a[200~")]     // 首键不是 ESC
    [InlineData("\x1b[200")]   // 缺尾 ~（喂到这里虽 InProgress…见下）
    public void DivergentSequence_NotStartOrEnd(string chars)
    {
        var r = FeedAll(chars);
        Assert.NotEqual(PasteMarkerResult.Start, r);
        Assert.NotEqual(PasteMarkerResult.End, r);
    }

    [Fact]
    public void DivergentSequence_MarkedFailed()
    {
        var d = new PasteMarkerDetector();
        foreach (var c in "\x1b[20x~")
            d.Feed(c);
        Assert.True(d.Failed); // 中断：已消费键必须放回，不能吞
    }

    [Fact]
    public void VariantCharacters_NotMarker()
    {
        // 标记全小写：错大小写/错符号的近似序列都不是标记
        Assert.Equal(PasteMarkerResult.None, FeedAll("\x1b{200~")); // { 代替 [
        Assert.Equal(PasteMarkerResult.None, FeedAll("\x1b[200`")); // ` 代替 ~
    }

    [Fact]
    public void InProgress_True_UntilTerminal()
    {
        var d = new PasteMarkerDetector();
        Assert.True(d.InProgress); // 初始态可喂
        d.Feed('\x1b');
        Assert.True(d.InProgress);
        d.Feed('[');
        Assert.True(d.InProgress);
        d.Feed('2');
        d.Feed('0');
        d.Feed('1');
        Assert.True(d.InProgress); // 还差尾 ~
        d.Feed('~');
        Assert.False(d.InProgress); // 终态
        Assert.Equal(PasteMarkerResult.End, d.Result);
    }
}
