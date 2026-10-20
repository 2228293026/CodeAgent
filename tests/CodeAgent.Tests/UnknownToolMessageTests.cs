using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

/// <summary>
/// 「未知工具」这条错误该怎么写。
///
/// 此前它会把**全部 231 个工具名**拼上去，约 4600 字符。模型编造一个工具名，
/// 就收到一整段占满上下文的回复——而那串名字里没有任何一个是它要找的：
/// 它需要的是「我想的那个叫什么」，不是「总共有哪些」。
///
/// 这条错误的读者是模型，不是人。人扫一眼长列表还能自己找，
/// 模型的注意力会被 231 个等价候选摊平，最后往往还是随便挑一个再错一次。
/// </summary>
public class UnknownToolMessageTests
{
    private static ToolRegistry Registry() => ToolRegistry.CreateDefault();

    [Fact]
    public async Task NearMissSuggestsTheClosestName()
    {
        var reg = Registry();
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            reg.ExecuteAsync("read_fil", "{}", MinimalContext(), CancellationToken.None));
        Assert.Contains("相近的", ex.Message);
        // 拼错一个字母，正确名字必须出现在候选里
        Assert.Contains("read_file", ex.Message);
    }

    [Fact]
    public async Task TheMessageStaysShort()
    {
        // 关键指标：不能把整张工具表塞进上下文。
        var reg = Registry();
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            reg.ExecuteAsync("totally_made_up_tool", "{}", MinimalContext(), CancellationToken.None));
        Assert.True(ex.Message.Length < 300,
            $"未知工具的错误消息 {ex.Message.Length} 字符，太长了：{ex.Message}");
    }

    [Fact]
    public async Task NoSuggestionStillPointsSomewhere()
    {
        // 与任何工具都不相近时，至少要告诉模型去哪里看全表。
        // 只写「未知工具: xxx」等于让它猜第二遍。
        var reg = Registry();
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            reg.ExecuteAsync("zzzzzz", "{}", MinimalContext(), CancellationToken.None));
        Assert.True(ex.Message.Contains("/tools") || ex.Message.Contains("相近的"),
            $"既没候选也没出路：{ex.Message}");
    }

    [Fact]
    public async Task TheToolNameItselfIsStillIncluded()
    {
        // 模型得知道自己调用的是什么名字，否则它无法对照修改
        var reg = Registry();
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            reg.ExecuteAsync("read_fil", "{}", MinimalContext(), CancellationToken.None));
        Assert.Contains("read_fil", ex.Message);
    }

    private static AgentContext MinimalContext()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ca-unknown-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(dir);
        return new AgentContext
        {
            Workspace = new Workspace(dir),
            Config = new AgentConfig { SaveSessions = false },
        };
    }
}
