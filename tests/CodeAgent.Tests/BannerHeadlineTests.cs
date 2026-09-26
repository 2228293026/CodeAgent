using System;
using System.IO;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 启动横幅的块状首行 + 规则文件提示行（Claude Code 风格）。
///
/// 回归点：此前首行只是一条 <c>── CodeAgent ────</c> 规则线，右侧什么都没挂，
/// 启动第一屏完全看不出「现在连的是哪个模型、哪个供应商」——那两行藏在下面的
/// 键值表里，而键值表第二行是 Version，扫读时最容易看到的反而是最没用的信息。
/// </summary>
/// <remarks>
/// 必须在 <c>ConsoleOutput</c> 集合里：横幅的字形取自 <c>SafeColor.Glyphs</c>，
/// 而它读的是**进程级**可替换的 <c>SafeColor.ReadEnv</c>。不串行执行时，
/// 另一个类正在跑的 ASCII 作用域会把这里读成纯 ASCII——单跑全过、
/// 全量并行跑挂，这类失败最难查。
/// </remarks>
[Collection("ConsoleOutput")]
public class BannerHeadlineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-banner-" + Guid.NewGuid().ToString("N"));

    public BannerHeadlineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void FullFormCarriesTitleAndSubtitleWithCaps()
    {
        var line = Program.BuildBannerHeadline("CodeAgent 0.4.0", "model · custom (openai)", 100);
        Assert.StartsWith("▐▛███▛█ CodeAgent 0.4.0 ▝▜██████▀", line);
        Assert.EndsWith("▝▜██████▀", line);
        Assert.Contains("model · custom (openai)", line);
        Assert.True(TextUtil.DisplayWidth(line) <= 100, $"首行超宽: {TextUtil.DisplayWidth(line)}");
    }

    [Fact]
    public void CapsAreMeasuredNotCounted()
    {
        // 关键回归：块状封口是 **7 列和 9 列**（▐▛███▛█ 有 7 个字符），不是"看起来 6 列"。
        // ASCII 封口则是 5 列和 5 列——同一段代码两种模式宽度差 2 列，
        // 按 Length 算在两种模式下会分别画短/画长，线根本对不齐。
        Assert.Equal(7, TextUtil.DisplayWidth(SafeColor.Glyphs.BannerCapLeft));
        Assert.Equal(9, TextUtil.DisplayWidth(SafeColor.Glyphs.BannerCapRight));
        var saved = SafeColor.ReadEnv;
        try
        {
            SafeColor.ReadEnv = n => n == "CODEAGENT_ASCII" ? "1" : null;
            Assert.Equal(5, TextUtil.DisplayWidth(SafeColor.Glyphs.BannerCapLeft));
            Assert.Equal(5, TextUtil.DisplayWidth(SafeColor.Glyphs.BannerCapRight));
            var line = Program.BuildBannerHeadline("CodeAgent", "model", 100);
            Assert.StartsWith("+==== CodeAgent ====+", line);
            Assert.EndsWith("====+", line);
        }
        finally { SafeColor.ReadEnv = saved; }
    }

    [Fact]
    public void NarrowWidthDropsSubtitleBeforeTitle()
    {
        const string title = "CodeAgent 0.4.0";
        const string sub = "model · custom (openai)";
        // 完整形态需要 7+1+14+1+9+1+21+1+9 = 64 列
        var full = Program.BuildBannerHeadline(title, sub, 100);
        Assert.Contains(sub, full);
        // 40 列：装得下产品名 + 两侧封口（18），装不下副标题
        var narrow = Program.BuildBannerHeadline(title, sub, 40);
        Assert.Contains(title, narrow);
        Assert.DoesNotContain("custom (openai)", narrow);
        Assert.True(TextUtil.DisplayWidth(narrow) <= 40, $"窄屏超宽: {TextUtil.DisplayWidth(narrow)}");
        Assert.True(TextUtil.DisplayWidth(full) > TextUtil.DisplayWidth(narrow));
    }

    [Fact]
    public void SubtitleIsAllOrNothing()
    {
        // 副标题要么完整出现要么完全不出现：`custom (opena` 这种半截
        // 供应商名看起来像一个完整但错误的名字，比不显示更糟。
        const string title = "CodeAgent 0.4.0";
        const string sub = "model · custom (openai)";
        var full = Program.BuildBannerHeadline(title, sub, 200);
        Assert.Contains(sub, full);
        // 完整形态的实测门槛就是完整形态自己的宽度
        var threshold = TextUtil.DisplayWidth(full);
        for (var w = 18; w < threshold; w++)
        {
            var line = Program.BuildBannerHeadline(title, sub, w);
            Assert.DoesNotContain("custom (openai)", line);
            Assert.DoesNotContain("openai", line);
            Assert.True(TextUtil.DisplayWidth(line) <= w, $"宽度 {w} 溢出: {TextUtil.DisplayWidth(line)}");
        }
    }

    [Fact]
    public void TinyWidthStillKeepsTheTitleAndNeverOverflows()
    {
        // 标题至少要有 4 列才值得夹在封口中间（7 + 1 + 4 + 1 + 9 = 22）。
        // 再窄就丢掉封口只留产品名——`▐▛███▛█ C ▝▜██████▀` 那种只有装饰没有信息的首行比没有更糟。
        Assert.Equal("▐▛███▛█ Cod… ▝▜██████▀", Program.BuildBannerHeadline("CodeAgent 0.4.0", null, 22));
        // 9 列的 CodeAgent 刚好放得下：7 + 1 + 9 + 1 + 9 = 27
        Assert.Equal("▐▛███▛█ CodeAgent ▝▜██████▀", Program.BuildBannerHeadline("CodeAgent", null, 27));
        foreach (var w in new[] { 1, 2, 5, 10, 14, 18, 21, 22, 30 })
        {
            var line = Program.BuildBannerHeadline("CodeAgent 0.4.0", "model", w);
            Assert.True(TextUtil.DisplayWidth(line) <= w, $"宽度 {w} 溢出: {TextUtil.DisplayWidth(line)}");
            // 任何宽度下都至少要留下点内容（哪怕只有一个字符）
            Assert.False(string.IsNullOrEmpty(line), $"宽度 {w} 输出为空");
        }
        // 宽度够时产品名必须完整可读
        Assert.Contains("CodeAgent 0.4.0", Program.BuildBannerHeadline("CodeAgent 0.4.0", "model", 60));
    }

    [Fact]
    public void UnknownWidthOmitsTheCapsEntirely()
    {
        // 宽度未知时不猜：少画封口好过画一条必然越界的线
        Assert.Equal("CodeAgent", Program.BuildBannerHeadline("CodeAgent", "model", 0));
    }

    [Fact]
    public void NoticeLineUsesTheDotAndStaysInBudget()
    {
        var line = Program.BuildBannerNotice("已加载 AGENTS.md: D:\\P\\AGENTS.md", 40);
        Assert.StartsWith("● 已加载", line);
        Assert.True(TextUtil.DisplayWidth(line) <= 40);
    }

    [Fact]
    public void RuleFilePrefersAgentsMd()
    {
        File.WriteAllText(Path.Combine(_dir, "CLAUDE.md"), "x");
        Assert.Equal(Path.Combine(_dir, "CLAUDE.md"), Program.RuleFile(_dir));
        File.WriteAllText(Path.Combine(_dir, "AGENTS.md"), "x");
        Assert.Equal(Path.Combine(_dir, "AGENTS.md"), Program.RuleFile(_dir));
    }

    [Fact]
    public void RuleFileIsNullWhenAbsent()
    {
        Assert.Null(Program.RuleFile(_dir));
    }
}
