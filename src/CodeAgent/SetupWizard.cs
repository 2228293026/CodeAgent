namespace CodeAgent;

/// <summary>
/// 交互式供应商配置向导（codeagent --setup / REPL 内 /setup）。
/// 终端里选择供应商、模型、API Key 提供方式，自动生成/更新 codeagent.json。
/// </summary>
public static class SetupWizard
{
    internal sealed record Preset(string Name, string Label, string Type, string BaseUrl, string Model, string Env);

    /// <summary>供应商预设表（--setup 向导用；internal 以便测试数据完整性）。</summary>
    internal static readonly Preset[] Presets =
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
    public static void Run(AgentConfig config) => Run(config, Console.In, Console.Out, null, testConnection: true);

    /// <summary>
    /// 运行向导（可注入输入输出以便测试）：就地更新 config。
    /// savePath 为保存路径；null 时保存到当前目录的 codeagent.json。
    /// </summary>
    internal static void Run(AgentConfig config, TextReader input, TextWriter output, string? savePath, bool testConnection = false)
    {
        var path = savePath ?? Path.Combine(Environment.CurrentDirectory, "codeagent.json");

        // 配置中已存在、但不在预设表里的 provider（如手工编辑 codeagent.json 加的自定义项），
        // 一并列出，选中后直接沿用原设置而不重新询问。
        var presetNames = Presets.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extras = config.Providers.Keys
            .Where(k => !presetNames.Contains(k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        output.WriteLine("── CodeAgent 供应商配置向导 ──────────────");
        output.WriteLine($"将更新配置文件: {path}\n");
        output.WriteLine("请选择供应商:");
        for (int i = 0; i < Presets.Length; i++)
            output.WriteLine($"  {i + 1}) {Presets[i].Label}");
        for (int i = 0; i < extras.Count; i++)
            output.WriteLine($"  {Presets.Length + i + 1}) {extras[i]}（已配置）");
        output.WriteLine();

        var idx = AskChoice(input, output, "选择", Presets.Length + extras.Count, 1);
        output.WriteLine();

        // 选中已配置的自定义 provider：沿用原设置（模型/地址/Key 都不再询问）
        if (idx > Presets.Length)
        {
            var name = extras[idx - Presets.Length - 1];
            var existing = config.Providers[name];
            config.Provider = name;
            output.WriteLine($"沿用已有配置: {name}");

            if (testConnection)
                TestConnection(name, existing, output);

            AgentConfig.Save(config, path);
            output.WriteLine($"\n✔ 配置已保存: {path}");
            output.WriteLine($"当前供应商: {name}   模型: {existing.Model}");
            output.WriteLine("运行 codeagent 即可开始使用。");
            return;
        }

        var p = Presets[idx - 1];
        var opts = new ProviderOptions { Type = p.Type };

        // 模型名
        string model = p.Model;
        if (p.Name == "custom")
        {
            model = AskRequired(input, output, "模型名（必填）");
        }
        else
        {
            model = Ask(input, output, "模型名", p.Model) ?? p.Model;
        }

        // API 地址
        string baseUrl = p.BaseUrl;
        if (p.Name == "custom")
        {
            baseUrl = AskRequired(input, output, "API 地址（必填，一般以 /v1 结尾）");
        }
        else
        {
            baseUrl = Ask(input, output, "API 地址", p.BaseUrl) ?? p.BaseUrl;
        }

        opts.Model = model;
        opts.BaseUrl = baseUrl;

        // 高级参数（custom 才问，回车用默认值）：自定义服务的上限/采样常需按供应商调整
        if (p.Name == "custom")
        {
            var mtText = Ask(input, output, "maxTokens（单次回复 token 上限）", opts.MaxTokens.ToString());
            if (int.TryParse(mtText, out var mt) && mt > 0)
                opts.MaxTokens = Math.Min(mt, 1_000_000); // 防手滑天文数字
            var tempText = Ask(input, output, "temperature（采样温度 0-2）", opts.Temperature.ToString("0.#"));
            if (double.TryParse(tempText, System.Globalization.CultureInfo.InvariantCulture, out var t) && t is >= 0 and <= 2)
                opts.Temperature = t;
        }

        // API Key
        if (p.Name is "ollama" or "hitmargin")
        {
            opts.ApiKey = p.Name == "ollama" ? "ollama" : "dummy"; // 免费服务不校验鉴权，占位即可
        }
        else
        {
            output.WriteLine("\nAPI Key 提供方式:");
            output.WriteLine("  1) 使用环境变量（推荐）");
            output.WriteLine("  2) 直接输入 Key（明文存入配置文件）");
            output.WriteLine("  3) 暂不设置（启动前自行配置）");
            var k = AskChoice(input, output, "选择", 3, 1);

            if (k == 1)
            {
                var env = Ask(input, output, "环境变量名", p.Env) ?? p.Env;
                opts.ApiKeyEnv = env;
            }
            else if (k == 2)
            {
                output.Write("请输入 API Key: ");
                var key = input.ReadLine()?.Trim() ?? "";
                opts.ApiKey = key.Length == 0 ? null : key;
                opts.ApiKeyEnv = null;
            }
            else
            {
                opts.ApiKeyEnv = p.Env;
                output.WriteLine($"提示: 启动前请先设置环境变量 {p.Env}。");
            }
        }

        config.Provider = p.Name;
        config.Providers[p.Name] = opts;

        // 连接测试：保存前用 /models 验证 baseUrl/key 可用，拼错的地址或坏 Key 立刻暴露，
        // 而不是等到第一次对话才报错。失败不阻断保存（可能是临时网络问题），只提示。
        if (testConnection)
            TestConnection(p.Name, opts, output);

        AgentConfig.Save(config, path);
        output.WriteLine($"\n✔ 配置已保存: {path}");
        output.WriteLine($"当前供应商: {p.Name}   模型: {opts.Model}");
        output.WriteLine("运行 codeagent 即可开始使用。");
    }

    /// <summary>连接测试：列出 Provider 模型验证 baseUrl/Key；失败或无 Key 只提示，不阻断保存。</summary>
    internal static void TestConnection(string providerName, ProviderOptions opts, TextWriter output)
    {
        // Key 未落配置且环境变量也未设置：无法测试，给出明确提示而不是含糊的 401
        var hasKey = !string.IsNullOrWhiteSpace(opts.ApiKey)
                     || (opts.ApiKeyEnv is { Length: > 0 } && Environment.GetEnvironmentVariable(opts.ApiKeyEnv) is { Length: > 0 });
        if (!hasKey && providerName is not ("ollama" or "hitmargin"))
        {
            output.WriteLine($"\n⏭ 跳过连接测试（{opts.ApiKeyEnv ?? "API Key"} 未设置）");
            return;
        }

        output.Write("\n⏳ 测试连接…");
        try
        {
            var probe = new AgentConfig { Provider = providerName };
            probe.Providers[providerName] = opts;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var models = ProviderFactory.Create(probe).ListModelsAsync(cts.Token).GetAwaiter().GetResult();
            output.WriteLine();
            if (models.Count == 0)
            {
                output.WriteLine("⚠ 服务未返回任何模型，请检查 API 地址。");
            }
            else if (!models.Contains(opts.Model ?? "", StringComparer.OrdinalIgnoreCase))
            {
                output.WriteLine($"⚠ 可连接，但模型列表中没有「{opts.Model}」（共 {models.Count} 个模型，可能拼写有误或无权限）。");
                // 给出相近候选（与 REPL /model 的拼写提示同款逻辑），少走一趟 /models
                var family = (opts.Model ?? "").Split('-', '.')[0];
                var near = string.IsNullOrEmpty(family)
                    ? []
                    : models.Where(m => m.Contains(family, StringComparison.OrdinalIgnoreCase))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Take(3)
                            .ToList();
                if (near.Count > 0)
                    output.WriteLine($"  相近的模型: {string.Join("、", near)}");
            }
            else
            {
                output.WriteLine($"✔ 连接成功，模型 {opts.Model} 可用（服务共 {models.Count} 个模型）。");
            }
        }
        catch (Exception ex)
        {
            output.WriteLine();
            output.WriteLine($"⚠ 连接失败: {ex.Message}");
            output.WriteLine("  配置仍会保存；请检查地址/Key，或稍后用 /models 复查。");
        }
    }

    /// <summary>带默认值的文本输入：回车使用默认值；输入被中断（EOF）时返回 null。</summary>
    private static string? Ask(TextReader input, TextWriter output, string prompt, string? defaultValue = null)
    {
        var def = defaultValue is null ? "" : $" [{defaultValue}]";
        output.Write($"{prompt}{def}: ");
        var line = input.ReadLine();
        if (line is null)
            return null;
        var text = line.Trim();
        return text.Length > 0 ? text : defaultValue;
    }

    /// <summary>必填输入：空输入继续询问，输入被中断（EOF）时取消整个向导。
    /// 不复用 Ask：Ask 把空输入映射回默认值（默认值为 null 时与 EOF 同为 null，无法区分）——
    /// 曾导致必填项按回车被当成中断，整个向导被取消。</summary>
    private static string AskRequired(TextReader input, TextWriter output, string prompt)
    {
        while (true)
        {
            output.Write($"{prompt}: ");
            var line = input.ReadLine();
            if (line is null)
            {
                output.WriteLine("\n⚠ 输入已中断，配置向导取消，未保存任何更改。");
                throw new OperationCanceledException();
            }
            var value = line.Trim();
            if (value.Length > 0)
                return value;
            output.WriteLine("  该项必填，请输入内容。");
        }
    }

    /// <summary>带默认值的序号选择，非法输入会重新询问；EOF 时取消向导。</summary>
    private static int AskChoice(TextReader input, TextWriter output, string prompt, int max, int def)
    {
        while (true)
        {
            var value = Ask(input, output, prompt, def.ToString());
            if (value is null)
                throw new OperationCanceledException();
            if (int.TryParse(value, out var n) && n >= 1 && n <= max)
                return n;
            output.WriteLine($"  请输入 1-{max} 之间的数字。");
        }
    }
}
