using System;
using System.Collections.Generic;
using CodeAgent;
using CodeAgent.Providers;
using Xunit;

namespace CodeAgent.Tests;

public class ProviderFactoryTests
{
    [Fact]
    public void Create_OpenAiType_FillsDefaults()
    {
        var config = new AgentConfig
        {
            Provider = "myopenai",
            Providers =
            {
                ["myopenai"] = new ProviderOptions { Type = "openai", ApiKey = "test-key" }, // 无 baseUrl/model → 用默认
            },
        };

        var provider = ProviderFactory.Create(config);

        Assert.IsType<OpenAiProvider>(provider);
        var opts = config.Providers["myopenai"];
        Assert.Equal(OpenAiProvider.DefaultBaseUrl, opts.BaseUrl);
        Assert.Equal(OpenAiProvider.DefaultModel, opts.Model);
    }

    [Fact]
    public void Create_AnthropicType_FillsDefaults()
    {
        var config = new AgentConfig
        {
            Provider = "claude",
            Providers =
            {
                ["claude"] = new ProviderOptions { Type = "anthropic", ApiKey = "test-key" },
            },
        };

        var provider = ProviderFactory.Create(config);

        Assert.IsType<AnthropicProvider>(provider);
        var opts = config.Providers["claude"];
        Assert.Equal(AnthropicProvider.DefaultBaseUrl, opts.BaseUrl);
        Assert.Equal(AnthropicProvider.DefaultModel, opts.Model);
    }

    [Fact]
    public void Create_NullProviderCollection_InitializesDefaults()
    {
        var config = new AgentConfig { Provider = "custom", Providers = null! };
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        try
        {
            var provider = ProviderFactory.Create(config);

            Assert.IsType<OpenAiProvider>(provider);
            Assert.True(config.Providers.ContainsKey("custom"));
            Assert.Equal(OpenAiProvider.DefaultBaseUrl, config.Providers["custom"].BaseUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        }
    }

    [Fact]
    public void Create_NullProviderEntry_ReplacesWithDefaults()
    {
        var config = new AgentConfig
        {
            Provider = "custom",
            Providers = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["custom"] = null!,
            },
        };
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        try
        {
            var provider = ProviderFactory.Create(config);

            Assert.IsType<OpenAiProvider>(provider);
            Assert.Equal(OpenAiProvider.DefaultBaseUrl, config.Providers["custom"].BaseUrl);
            Assert.Equal(OpenAiProvider.DefaultModel, config.Providers["custom"].Model);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        }
    }

    [Fact]
    public void Create_UnknownProvider_GetsAddedWithDefaults()
    {
        // 配置里没写 providers 键时：工厂应补一个默认 openai 项而不是抛异常
        var config = new AgentConfig { Provider = "does-not-exist" };
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-key");
        try
        {
            var provider = ProviderFactory.Create(config);
            Assert.IsType<OpenAiProvider>(provider);
            Assert.True(config.Providers.ContainsKey("does-not-exist"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        }
    }

    [Fact]
    public void Create_TrimsDirectProviderOptions()
    {
        var config = new AgentConfig
        {
            Provider = "custom",
            Providers =
            {
                ["custom"] = new ProviderOptions
                {
                    Type = "  anthropic  ",
                    BaseUrl = "  https://api.example  ",
                    Model = "  model-x  ",
                    ApiKeyEnv = "  CUSTOM_KEY  ",
                    ApiKey = "test-key",
                },
            },
        };

        ProviderFactory.Create(config);
        var opts = config.Providers["custom"];

        Assert.Equal("anthropic", opts.Type);
        Assert.Equal("https://api.example", opts.BaseUrl);
        Assert.Equal("model-x", opts.Model);
        Assert.Equal("CUSTOM_KEY", opts.ApiKeyEnv);
    }

    [Fact]
    public void Create_FindsProviderKeyCaseInsensitively()
    {
        var config = new AgentConfig
        {
            Provider = "CUSTOM",
            Providers = new Dictionary<string, ProviderOptions>
            {
                ["custom"] = new ProviderOptions { Type = "openai", ApiKey = "test-key", Model = "existing" },
            },
        };

        ProviderFactory.Create(config);

        Assert.Equal("existing", config.Providers["custom"].Model);
        Assert.Equal("custom", config.Provider);
        Assert.DoesNotContain("CUSTOM", config.Providers.Keys);
    }

    [Fact]
    public void Create_KeepsExplicitValues()
    {
        var config = new AgentConfig
        {
            Provider = "custom",
            Providers =
            {
                ["custom"] = new ProviderOptions { Type = "openai", ApiKey = "test-key", BaseUrl = "https://x.example/v1", Model = "m1" },
            },
        };

        ProviderFactory.Create(config);

        var opts = config.Providers["custom"];
        Assert.Equal("https://x.example/v1", opts.BaseUrl);
        Assert.Equal("m1", opts.Model); // 显式值不应被默认覆盖
    }

    [Fact]
    public void Create_NullType_FallsBackToOpenAi()
    {
        // 回归：JSON 显式 "type": null 会覆盖默认值，曾导致 opts.Type.Trim() 抛 NRE；
        // 现在 null/空白 type 兜底为 openai
        var config = new AgentConfig
        {
            Provider = "weird",
            Providers =
            {
                ["weird"] = new ProviderOptions { Type = null!, ApiKey = "test-key" },
            },
        };

        var provider = ProviderFactory.Create(config);

        Assert.IsType<OpenAiProvider>(provider);
        Assert.Equal("openai", config.Providers["weird"].Type); // 已收敛
    }
}
