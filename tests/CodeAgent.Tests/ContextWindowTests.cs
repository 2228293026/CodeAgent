using System.Threading.Tasks;
using CodeAgent;
using CodeAgent.Providers;
using Xunit;

namespace CodeAgent.Tests;

public class ContextWindowTests
{
    private static Program.ContextProbeState DoneProbe(string model, int? value) => new()
    {
        Model = model,
        Task = Task.FromResult(value),
    };

    [Fact]
    public void EffectiveContextWindow_PriorityConfigOverTableOverProbe()
    {
        var config = new AgentConfig { ContextWindow = 111_111 };
        var opts = new ProviderOptions { Model = "gpt-4o" };
        // 配置优先于一切（含探测值）
        Assert.Equal(111_111, Program.EffectiveContextWindow(config, opts, DoneProbe("gpt-4o", 999_999)));

        // 无配置：内置表命中（gpt-4o = 128k），探测值不参与
        config.ContextWindow = 0;
        Assert.Equal(128_000, Program.EffectiveContextWindow(config, opts, DoneProbe("gpt-4o", 999_999)));

        // deepseek-v4 系列已入表：128k
        opts.Model = "deepseek-v4";
        Assert.Equal(128_000, Program.EffectiveContextWindow(config, opts, DoneProbe("deepseek-v4", 555_555)));

        // 表未命中（my-private-model 不在表）：探测值生效——但仅当探测是同一模型
        opts.Model = "my-private-model";
        Assert.Equal(555_555, Program.EffectiveContextWindow(config, opts, DoneProbe("my-private-model", 555_555)));
        Assert.Equal(0, Program.EffectiveContextWindow(config, opts, DoneProbe("other-model", 555_555)));

        // 表未命中且无探测：0（未知）
        Assert.Equal(0, Program.EffectiveContextWindow(config, opts, null));
    }

    [Fact]
    public void EffectiveContextWindow_ProbeNotCompleted_IsIgnored()
    {
        var config = new AgentConfig();
        var opts = new ProviderOptions { Model = "my-private-model" }; // 不在表：探测未完成时必须回落 0
        var pending = new Program.ContextProbeState { Model = "my-private-model", Task = NeverCompletes() };
        Assert.Equal(0, Program.EffectiveContextWindow(config, opts, pending));
        return;
        static Task<int?> NeverCompletes() => new Task<int?>(() => 0);
    }
}
