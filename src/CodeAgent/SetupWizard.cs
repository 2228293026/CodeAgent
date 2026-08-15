namespace CodeAgent;

/// <summary>
/// 交互式供应商配置向导（codeagent --setup / REPL 内 /setup）。
/// 终端里选择供应商、模型、API Key 提供方式，自动生成/更新 codeagent.json。
/// </summary>
public static class SetupWizard
{
    private sealed record Preset(string Name, string Label, string Type, string BaseUrl, string Model, string Env);

    private static readonly Preset[] Presets =
    [
        new("openai", "OpenAI 官方", "openai", "https://api.openai.com/v1", "gpt-4o", "OPENAI_API_KEY"),
        new("deepseek", "DeepSeek（OpenAI 兼容）", "openai", "https://api.deepseek.com/v1", "deepseek-chat", "DEEPSEEK_API_KEY"),
        new("qwen", "通义千问 DashScope（OpenAI 兼容）", "openai", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen3-coder-plus", "DASHSCOPE_API_KEY"),
        new("ollama", "本地 Ollama（免费，无需 Key）", "openai", "http://localhost:11434/v1", "qwen2.5-coder:7b", ""),
        new("anthropic", "Anthropic Claude", "anthropic", "https://api.anthropic.com", "claude-sonnet-4-5", "ANTHROPIC_API_KEY"),
        new("hitmargin", "hitmargin 免费模型代理（Key 随意）", "openai", "https://api.hitmargin.workers.dev/v1", "poolside/laguna-s-2.1:free", ""),
        new("custom", "自定义 OpenAI 兼容服务", "openai", "", "", ""),
    ];

    /// <summary>运行向导：就地更新 config 并保存到当前目录的 codeagent.json。</summary>
    public static void Run(AgentConfig config)
    {
        var path = Path.Combine(Environment.CurrentDirectory, "codeagent.json");

        Console.WriteLine("── CodeAgent 供应商配置向导 ──────────────");
        Console.WriteLine($"将更新配置文件: {path}\n");
        Console.WriteLine("请选择供应商:");
        for (int i = 0; i < Presets.Length; i++)
            Console.WriteLine($"  {i + 1}) {Presets[i].Label}");
        Console.WriteLine();

        var idx = AskChoice("选择", Presets.Length, 1);
        var p = Presets[idx - 1];

        Console.WriteLine();
        var opts = new ProviderOptions { Type = p.Type };

        // 模型名
        string model = p.Model;
        if (p.Name == "custom")
        {
            model = AskRequired("模型名（必填）");
        }
        else
        {
            model = Ask("模型名", p.Model) ?? p.Model;
        }

        // API 地址
        string baseUrl = p.BaseUrl;
        if (p.Name == "custom")
        {
            baseUrl = AskRequired("API 地址（必填，一般以 /v1 结尾）");
        }
        else
        {
            baseUrl = Ask("API 地址", p.BaseUrl) ?? p.BaseUrl;
        }

        opts.Model = model;
        opts.BaseUrl = baseUrl;

        // API Key
        if (p.Name is "ollama" or "hitmargin")
        {
            opts.ApiKey = p.Name == "ollama" ? "ollama" : "dummy"; // 免费服务不校验鉴权，占位即可
        }
        else
        {
            Console.WriteLine("\nAPI Key 提供方式:");
            Console.WriteLine("  1) 使用环境变量（推荐）");
            Console.WriteLine("  2) 直接输入 Key（明文存入配置文件）");
            Console.WriteLine("  3) 暂不设置（启动前自行配置）");
            var k = AskChoice("选择", 3, 1);

            if (k == 1)
            {
                var env = Ask("环境变量名", p.Env) ?? p.Env;
                opts.ApiKeyEnv = env;
            }
            else if (k == 2)
            {
                Console.Write("请输入 API Key: ");
                var key = Console.ReadLine()?.Trim() ?? "";
                opts.ApiKey = key.Length == 0 ? null : key;
                opts.ApiKeyEnv = null;
            }
            else
            {
                opts.ApiKeyEnv = p.Env;
                Console.WriteLine($"提示: 启动前请先设置环境变量 {p.Env}。");
            }
        }

        config.Provider = p.Name;
        config.Providers[p.Name] = opts;

        AgentConfig.Save(config, path);
        Console.WriteLine($"\n✔ 配置已保存: {path}");
        Console.WriteLine($"当前供应商: {p.Name}   模型: {opts.Model}");
        Console.WriteLine("运行 codeagent 即可开始使用。");
    }

    /// <summary>带默认值的文本输入：回车使用默认值；输入被中断（EOF）时返回 null。</summary>
    private static string? Ask(string prompt, string? defaultValue = null)
    {
        var def = defaultValue is null ? "" : $" [{defaultValue}]";
        Console.Write($"{prompt}{def}: ");
        var line = Console.ReadLine();
        if (line is null)
            return null;
        var input = line.Trim();
        return input.Length > 0 ? input : defaultValue;
    }

    /// <summary>必填输入：空输入继续询问，输入被中断（EOF）时取消整个向导。</summary>
    private static string AskRequired(string prompt)
    {
        while (true)
        {
            var input = Ask(prompt);
            if (input is null)
            {
                Console.WriteLine("\n⚠ 输入已中断，配置向导取消，未保存任何更改。");
                throw new OperationCanceledException();
            }
            if (input.Length > 0)
                return input;
        }
    }

    /// <summary>带默认值的序号选择，非法输入会重新询问；EOF 时取消向导。</summary>
    private static int AskChoice(string prompt, int max, int def)
    {
        while (true)
        {
            var input = Ask(prompt, def.ToString());
            if (input is null)
                throw new OperationCanceledException();
            if (int.TryParse(input, out var n) && n >= 1 && n <= max)
                return n;
            Console.WriteLine($"  请输入 1-{max} 之间的数字。");
        }
    }
}
