using System;
using System.IO;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

public class PathDisplayTests
{
    [Theory]
    [InlineData("/short/path")]
    [InlineData("")]
    [InlineData("D:/Projects/CodeAgent")]
    public void TruncatePathHead_ShortPaths_Unchanged(string path) =>
        Assert.Equal(path, Program.TruncatePathHead(path));

    [Fact]
    public void TruncatePathHead_LongPath_KeepsTailWithEllipsis()
    {
        // 深路径显示：保留尾部（工作区目录名永远可见），长度封顶
        var longPath = @"C:\Users\someone\Deeply\Nested\Project\Structure\CodeAgent";
        var shown = Program.TruncatePathHead(longPath);
        Assert.StartsWith("…", shown);
        Assert.EndsWith("CodeAgent", shown);
        Assert.True(shown.Length <= 42, $"截断后 {shown.Length} 仍超上限");
        // 恰好等于上限：原样返回
        Assert.Equal(longPath, Program.TruncatePathHead(longPath, longPath.Length));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void FilterModels_BlankFilter_ReturnsAll(string? filter)
    {
        var models = new[] { "gpt-4o", "deepseek-chat", "claude-sonnet-4-5" };
        Assert.Equal(3, Program.FilterModels(models, filter).Count);
    }

    [Fact]
    public void FilterModels_SubstringCaseInsensitive()
    {
        var models = new[] { "gpt-4o", "GPT-4.1-mini", "deepseek-chat", "openai/gpt-5" };
        var hit = Program.FilterModels(models, "gpt");
        Assert.Equal(3, hit.Count); // openai/gpt-5 也命中（子串）
        Assert.DoesNotContain("deepseek-chat", hit);
    }

    [Fact]
    public void NumberedModels_Filtered_KeepsFullListIndices()
    {
        // 回归：/models <过滤> 曾把过滤后的列表重编号为 1..N，
        // 而 /model <编号> 按完整列表解析——编号错位。编号必须保留完整列表下标。
        var models = new[] { "gpt-4o", "deepseek-chat", "gpt-4.1", "deepseek-reasoner", "claude-sonnet-4-5" };
        var rows = Program.NumberedModels(models, "deepseek");
        Assert.Equal([(2, "deepseek-chat"), (4, "deepseek-reasoner")], rows);
    }

    [Fact]
    public void NumberedModels_NoFilter_NumbersSequentially()
    {
        var models = new[] { "gpt-4o", "deepseek-chat", "claude-sonnet-4-5" };
        var rows = Program.NumberedModels(models, null);
        Assert.Equal([(1, "gpt-4o"), (2, "deepseek-chat"), (3, "claude-sonnet-4-5")], rows);
    }

    [Fact]
    public void NumberedModels_NoMatches_ReturnsEmpty()
    {
        var rows = Program.NumberedModels(["gpt-4o"], "gemini");
        Assert.Empty(rows);
    }

    [Fact]
    public void SuggestModels_FamilyPrefix_FindsCandidates()
    {
        var models = new[] { "gpt-4o", "gpt-4o-mini", "gpt-4.1", "deepseek-chat", "deepseek-reasoner" };
        // 输入 gpt4o（拼错的家族名）：按首段 gpt4o 匹配 → 无；用 gpt 匹配的调用方语义
        Assert.Empty(Program.SuggestModels(models, "gpt4o"));
        // 正确家族段：gpt → 前 3 个 gpt 系
        var gpt = Program.SuggestModels(models, "gpt-4o-min");
        Assert.Equal(3, gpt.Count);
        Assert.All(gpt, m => Assert.StartsWith("gpt", m));
        // deepseek 家族
        var ds = Program.SuggestModels(models, "deepseek-cht"); // 拼错 chat → cht
        Assert.Equal(2, ds.Count);
    }

    [Fact]
    public void SuggestModels_EmptyFamily_ReturnsEmpty()
    {
        Assert.Empty(Program.SuggestModels(["a", "b"], "-x"));
        Assert.Empty(Program.SuggestModels(["a"], ".y"));
    }
    [Fact]
    public void ComposeTaskWithStdin_AppendsWhenPiped()
    {
        // type bug.log | codeagent "分析"：stdin 内容附在任务后；空 stdin 原样返回
        var composed = Program.ComposeTaskWithStdin("分析日志", "ERROR at line 3");
        Assert.StartsWith("分析日志", composed);
        Assert.Contains("[stdin 输入]", composed);
        Assert.Contains("ERROR at line 3", composed);

        Assert.Equal("分析日志", Program.ComposeTaskWithStdin("分析日志", ""));
        Assert.Equal("分析日志", Program.ComposeTaskWithStdin("分析日志", "  \n"));
    }


    [Fact]
    public void FirstEnvVar_PrefersFirstNonEmpty_AndFallsThrough()
    {
        // 别名兜底：CODEAGENT_* 优先于历史拼写 CODEGENT_*；全空返回 null；空白视为未设置
        var a = "codeagent-test-a-" + Guid.NewGuid().ToString("N");
        var b = "codeagent-test-b-" + Guid.NewGuid().ToString("N");
        try
        {
            Environment.SetEnvironmentVariable(a, "first");
            Environment.SetEnvironmentVariable(b, "second");
            Assert.Equal("first", Program.FirstEnvVar(a, b));
            Assert.Equal("second", Program.FirstEnvVar("codeagent-missing-" + a, b));
            Assert.Null(Program.FirstEnvVar("codeagent-missing-" + a));

            Environment.SetEnvironmentVariable(a, "   ");
            Assert.Equal("second", Program.FirstEnvVar(a, b)); // 空白值跳过
        }
        finally
        {
            Environment.SetEnvironmentVariable(a, null);
            Environment.SetEnvironmentVariable(b, null);
        }
    }

    [Theory]
    [InlineData("--verbos")]   // 拼写错误
    [InlineData("--contnue")]
    [InlineData("-x")]
    [InlineData("--cwdx")]
    public void LooksLikeUnknownFlag_Typos_AreRejected(string arg) =>
        Assert.True(Program.LooksLikeUnknownFlag(arg));

    [Theory]
    [InlineData("-c")]
    [InlineData("--config")]
    [InlineData("--continue")]
    [InlineData("--resume")]
    [InlineData("--no-session")]
    [InlineData("-h")]
    [InlineData("--help")]
    public void LooksLikeUnknownFlag_KnownFlags_Accepted(string arg) =>
        Assert.False(Program.LooksLikeUnknownFlag(arg));

    [Theory]
    [InlineData("任务描述")]   // 普通位置参数不是旗标
    [InlineData("-")]          // 单个 '-'（常见 stdin 惯例）不当旗标拒绝
    [InlineData("")]
    public void LooksLikeUnknownFlag_PositionalText_NotFlag(string arg) =>
        Assert.False(Program.LooksLikeUnknownFlag(arg));

    [Fact]
    public void SaveConfig_SessionProviderOverride_NotPersisted()
    {
        // 回归：CODEAGENT_PROVIDER=deepseek 启动后，/thinking 等命令保存配置
        // 曾把 deepseek 固化成配置文件的默认 provider
        var dir = Path.Combine(Path.GetTempPath(), "codeagent-savecfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "codeagent.json");
            AgentConfig.Save(new AgentConfig { Provider = "openai" }, path);

            var config = AgentConfig.Load(path);
            config.PersistedProvider = config.Provider; // Program 启动时记录
            config.Provider = "deepseek";                // 会话级覆盖（env/-p）

            Program.SaveConfig(config, path);

            Assert.Equal("deepseek", config.Provider); // 内存保持会话值
            var reloaded = AgentConfig.Load(path);
            Assert.Equal("openai", reloaded.Provider); // 持久层保持原值
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void SaveConfig_NoPersistedRecord_SavesCurrentProvider()
    {
        // 未记录持久值（新配置/测试直造）：按当前值保存，行为与旧版一致
        var dir = Path.Combine(Path.GetTempPath(), "codeagent-savecfg2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "codeagent.json");
            var config = new AgentConfig { Provider = "deepseek" };
            Program.SaveConfig(config, path);
            Assert.Equal("deepseek", AgentConfig.Load(path).Provider);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
