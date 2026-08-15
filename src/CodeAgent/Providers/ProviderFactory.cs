using CodeAgent.Providers;

namespace CodeAgent;

/// <summary>根据配置创建 Provider 实例，并填充类型默认值。</summary>
public static class ProviderFactory
{
    public static IAgentProvider Create(AgentConfig config)
    {
        var name = string.IsNullOrWhiteSpace(config.Provider) ? "openai" : config.Provider.Trim();
        if (!config.Providers.TryGetValue(name, out var opts))
        {
            opts = new ProviderOptions();
            config.Providers[name] = opts;
        }

        var type = opts.Type.Trim().ToLowerInvariant();
        if (type is "anthropic" or "claude")
        {
            type = "anthropic";
            opts.Type = type;
            if (string.IsNullOrWhiteSpace(opts.BaseUrl))
                opts.BaseUrl = AnthropicProvider.DefaultBaseUrl;
            if (string.IsNullOrWhiteSpace(opts.Model))
                opts.Model = AnthropicProvider.DefaultModel;
            return new AnthropicProvider(opts);
        }

        opts.Type = "openai";
        if (string.IsNullOrWhiteSpace(opts.BaseUrl))
            opts.BaseUrl = OpenAiProvider.DefaultBaseUrl;
        if (string.IsNullOrWhiteSpace(opts.Model))
            opts.Model = OpenAiProvider.DefaultModel;
        return new OpenAiProvider(opts);
    }
}
