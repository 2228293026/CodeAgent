using System.Reflection;
using CodeAgent.Providers;
using CodeAgent.Tools;
using AgentClass = CodeAgent.Agent.Agent;

namespace CodeAgent;

/// <summary>CLI 入口：支持一次请求（codeagent "任务"）与交互式 REPL。</summary>
internal static class Program
{
    /// <summary>当前正在运行的一轮请求（供 Ctrl+C / ESC 取消）。</summary>
    private static CancellationTokenSource? _activeTurn;

    private static string InformationalVersion =>
        Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?.Split('+')[0] ?? "0.0.0";

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // Ctrl+C：运行中取消本轮；空闲时保持默认退出行为
        Console.CancelKeyPress += (_, e) =>
        {
            if (_activeTurn is not null)
            {
                e.Cancel = true;
                _activeTurn.Cancel();
            }
        };

        string? configPath = null, provider = null, model = null, cwd = null;
        var init = false;
        var setup = false;
        var positional = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-c" or "--config":
                    configPath = NextArg(args, ref i, "--config");
                    break;
                case "-p" or "--provider":
                    provider = NextArg(args, ref i, "--provider");
                    break;
                case "-m" or "--model":
                    model = NextArg(args, ref i, "--model");
                    break;
                case "--cwd":
                    cwd = NextArg(args, ref i, "--cwd");
                    break;
                case "--init":
                    init = true;
                    break;
                case "--setup":
                    setup = true;
                    break;
                case "-v" or "--version":
                    Console.WriteLine($"codeagent {InformationalVersion}");
                    return 0;
                case "-h" or "--help":
                    PrintHelp();
                    return 0;
                default:
                    positional.Add(args[i]);
                    break;
            }
        }

        if (init)
        {
            var target = Path.Combine(Environment.CurrentDirectory, "codeagent.json");
            if (File.Exists(target))
            {
                Console.WriteLine($"codeagent.json 已存在: {target}");
                return 1;
            }
            AgentConfig.WriteExample(target);
            Console.WriteLine($"已生成示例配置: {target}");
            Console.WriteLine("提示: 也可运行 codeagent --setup 用向导快速配置供应商。");
            Console.WriteLine("请填入 API Key（或设置对应环境变量），然后运行 codeagent。");
            return 0;
        }

        if (cwd is not null)
        {
            if (!Directory.Exists(cwd))
            {
                Console.Error.WriteLine($"目录不存在: {cwd}");
                return 2;
            }
            Environment.CurrentDirectory = Path.GetFullPath(cwd);
        }

        AgentConfig config;
        try
        {
            config = AgentConfig.Load(configPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"配置加载失败: {ex.Message}");
            return 2;
        }

        // 交互式供应商配置向导：生成/更新 codeagent.json 后退出
        if (setup)
        {
            try
            {
                SetupWizard.Run(config);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("已取消配置向导。");
            }
            return 0;
        }

        // 命令行/环境变量覆盖：provider 与 model
        var envProvider = Environment.GetEnvironmentVariable("CODEGENT_PROVIDER");
        var envModel = Environment.GetEnvironmentVariable("CODEGENT_MODEL");
        if (provider is not null)
            config.Provider = provider;
        else if (!string.IsNullOrWhiteSpace(envProvider))
            config.Provider = envProvider;

        var opts = EnsureSelectedProvider(config);
        if (model is not null)
            opts.Model = model;
        else if (!string.IsNullOrWhiteSpace(envModel))
            opts.Model = envModel;

        var tools = ToolRegistry.CreateDefault();

        IAgentProvider providerInst;
        try
        {
            providerInst = ProviderFactory.Create(config);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Provider 初始化失败: {ex.Message}");
            return 2;
        }

        var agent = new AgentClass(config, providerInst, tools);

        // 一次请求模式
        if (positional.Count > 0)
        {
            try
            {
                var result = await RunTurnAsync(t => agent.RunAsync(string.Join(" ", positional), t));
                PrintResult(result, agent.StreamedLastRun, prefixNewline: false);
                agent.Close();
                return 0;
            }
            catch (ProviderException ex)
            {
                Console.Error.WriteLine($"⚠ {ex.Message}");
                agent.Close();
                return 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"⚠ 发生错误: {ex.GetType().Name}: {ex.Message}");
                agent.Close();
                return 1;
            }
        }

        // 交互式 REPL
        PrintBanner(config, opts, agent);
        while (true)
        {
            Console.Write("\ncodeagent> ");
            var line = Console.ReadLine();
            if (line is null)
                break; // EOF (Ctrl+Z / Ctrl+D)
            line = line.Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith('/'))
            {
                if (line.Equals("/retry", StringComparison.OrdinalIgnoreCase))
                {
                    if (agent.LastPrompt is null)
                    {
                        Console.WriteLine("没有可重试的请求。");
                        continue;
                    }
                    line = agent.LastPrompt; // 作为普通请求重新执行
                }
                else
                {
                    HandleCommand(line, config, ref opts, agent, ref providerInst, tools);
                    continue;
                }
            }

            try
            {
                var result = await RunTurnAsync(t => agent.RunAsync(line, t));
                PrintResult(result, agent.StreamedLastRun, prefixNewline: true);
            }
            catch (ProviderException ex)
            {
                Console.WriteLine($"\n⚠ {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n⚠ 发生错误: {ex.GetType().Name}: {ex.Message}");
            }
        }

        agent.Close();
        return 0;
    }

    private static ProviderOptions EnsureSelectedProvider(AgentConfig config)
    {
        if (!config.Providers.TryGetValue(config.Provider, out var opts))
        {
            opts = new ProviderOptions();
            config.Providers[config.Provider] = opts;
        }
        return opts;
    }

    private static string NextArg(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"缺少 {flag} 的参数值");
        return args[++i];
    }

    /// <summary>
    /// 执行一轮请求：支持 Ctrl+C 与 ESC 优雅取消。
    /// 运行中取消返回提示文本而不是崩溃；空闲时 Ctrl+C 仍为默认退出。
    /// </summary>
    private static async Task<string> RunTurnAsync(Func<CancellationToken, Task<string>> action)
    {
        using var cts = new CancellationTokenSource();
        _activeTurn = cts;

        // ESC 监听：运行期间按 ESC 取消本轮（stdin 被重定向时 KeyAvailable 不可用，自动降级）
        var watcher = Task.Run(() =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(intercept: true);
                        if (key.Key == ConsoleKey.Escape)
                            cts.Cancel();
                    }
                    Thread.Sleep(40);
                }
            }
            catch
            {
                // 非交互终端（管道/重定向）忽略
            }
        });

        try
        {
            return await action(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return "\n⏹ 已取消。";
        }
        finally
        {
            _activeTurn = null;
            cts.Cancel(); // 停止 ESC 监听任务
        }
    }

    /// <summary>输出最终答复：若已流式打印过则只补换行，否则整体打印。</summary>
    private static void PrintResult(string result, bool streamed, bool prefixNewline)
    {
        if (streamed)
        {
            if (result.Length > 0)
                Console.WriteLine();
        }
        else
        {
            Console.WriteLine((prefixNewline ? "\n" : "") + result);
        }
    }

    private static void PrintBanner(AgentConfig config, ProviderOptions opts, AgentClass agent)
    {
        Console.WriteLine("── CodeAgent ─────────────────────────────────────────────");
        Console.WriteLine($"  Provider : {config.Provider} ({opts.Type})");
        Console.WriteLine($"  Model    : {opts.Model}");
        Console.WriteLine($"  Mode     : {agent.CurrentMode.Name}");
        Console.WriteLine($"  BaseUrl  : {opts.BaseUrl}");
        Console.WriteLine($"  Workspace: {Environment.CurrentDirectory}");
        if (agent.SessionPath is not null)
            Console.WriteLine($"  会话日志  : {agent.SessionPath}");
        if (config.SourceFile is not null)
            Console.WriteLine($"  配置文件  : {config.SourceFile}");
        Console.WriteLine("  输入 /help 查看命令；直接输入任务描述即可开始。");
        Console.WriteLine("──────────────────────────────────────────────────────────");
    }

    private static void HandleCommand(
        string line,
        AgentConfig config,
        ref ProviderOptions opts,
        AgentClass agent,
        ref IAgentProvider providerInst,
        ToolRegistry tools)
    {
        var (cmd, rest) = SplitCommand(line);

        switch (cmd)
        {
            case "/exit" or "/quit":
                Console.WriteLine("再见！");
                Environment.Exit(0);
                break;

            case "/clear":
                agent.Reset();
                Console.WriteLine("已清空对话历史。");
                break;

            case "/model":
                if (string.IsNullOrWhiteSpace(rest))
                {
                    Console.WriteLine($"当前模型: {opts.Model}");
                }
                else
                {
                    opts.Model = rest.Trim();
                    try
                    {
                        providerInst = ProviderFactory.Create(config);
                        agent.SetProvider(providerInst);
                        Console.WriteLine($"已切换模型: {opts.Model}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"切换失败: {ex.Message}");
                    }
                }
                break;

            case "/config":
                Console.WriteLine($"Provider : {config.Provider} ({opts.Type})");
                Console.WriteLine($"Model    : {opts.Model}");
                Console.WriteLine($"BaseUrl  : {opts.BaseUrl}");
                Console.WriteLine($"ApiKey   : {(string.IsNullOrEmpty(opts.ApiKey) ? $"env[{opts.ApiKeyEnv ?? "?"}]" : "****")}");
                Console.WriteLine($"MaxIter  : {config.MaxToolIterations}  MaxHistoryChars: {config.MaxHistoryChars}");
                Console.WriteLine($"Commands : {(config.AllowCommands ? "on" : "off")}  确认: {(config.ConfirmCommands ? "on" : "off")}   Shell: {config.Shell}");
                Console.WriteLine($"工具日志 : {(config.ShowToolCalls ? "on" : "off")}   流式输出: {(config.StreamOutput ? "on" : "off")}");
                break;

            case "/session":
                Console.WriteLine(agent.SessionPath ?? "会话日志未启用（config.SaveSessions=false）。");
                break;

            case "/setup":
                SetupWizard.Run(config);
                opts = EnsureSelectedProvider(config);
                try
                {
                    providerInst = ProviderFactory.Create(config);
                    agent.SetProvider(providerInst);
                    Console.WriteLine($"已应用新配置: {config.Provider} / {opts.Model}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"配置已保存，但 Provider 初始化失败（请检查 API Key）: {ex.Message}");
                }
                break;

            case "/undo":
                Console.WriteLine(agent.Context.Undo.TryUndo() ?? "没有可撤销的操作。");
                break;

            case "/diff":
                Console.WriteLine(agent.Context.Undo.LastDiff() ?? "没有可显示的改动（先让 agent 修改过文件）。");
                break;

            case "/save":
                if (string.IsNullOrWhiteSpace(rest))
                {
                    Console.WriteLine("用法: /save <会话名>");
                }
                else
                {
                    try
                    {
                        agent.SaveSession(rest.Trim());
                        Console.WriteLine($"✔ 已保存会话: {rest.Trim()}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"保存失败: {ex.Message}");
                    }
                }
                break;

            case "/load":
                if (string.IsNullOrWhiteSpace(rest))
                {
                    Console.WriteLine("用法: /load <会话名>");
                }
                else
                {
                    try
                    {
                        agent.LoadSession(rest.Trim());
                        Console.WriteLine($"✔ 已恢复会话: {rest.Trim()}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"加载失败: {ex.Message}");
                    }
                }
                break;

            case "/export":
                try
                {
                    var file = agent.ExportMarkdown(string.IsNullOrWhiteSpace(rest) ? null : rest.Trim());
                    Console.WriteLine($"✔ 已导出: {file}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"导出失败: {ex.Message}");
                }
                break;

            case "/stats":
                Console.WriteLine(
                    $"会话统计: 请求 {agent.ProviderCalls} 次，" +
                    $"输入 {agent.TotalInputTokens:N0} tokens，输出 {agent.TotalOutputTokens:N0} tokens");
                break;

            case "/tools":
                Console.WriteLine($"可用工具（当前模式: {agent.CurrentMode.Name}）:");
                foreach (var t in agent.ToolsForMode())
                    Console.WriteLine($"  {t.Name} — {t.Description}");
                break;

            case "/providers":
                Console.WriteLine("已配置的 Provider:");
                foreach (var kv in config.Providers)
                    Console.WriteLine($"  {kv.Key} ({kv.Value.Type}) 模型: {kv.Value.Model}  baseUrl: {kv.Value.BaseUrl}");
                break;

            case "/mode":
                if (string.IsNullOrWhiteSpace(rest))
                {
                    Console.WriteLine($"当前模式: {agent.CurrentMode.Name}");
                    Console.WriteLine(Modes.ListText(config));
                }
                else
                {
                    var mode = Modes.Find(rest, config);
                    agent.SetMode(mode);
                    Console.WriteLine($"已切换模式: {mode.Name} — {mode.Description}");
                }
                break;

            case "/help":
                PrintReplHelp();
                break;

            default:
                Console.WriteLine($"未知命令: {cmd}（输入 /help 查看）");
                break;
        }
    }

    private static (string cmd, string rest) SplitCommand(string line)
    {
        var idx = line.IndexOf(' ');
        return idx < 0 ? (line, "") : (line[..idx], line[(idx + 1)..]);
    }

    private static void PrintReplHelp()
    {
        Console.WriteLine("""
            命令:
              /help            显示本帮助
              /clear           清空对话历史
              /model [名称]    查看或切换模型
              /config          显示当前配置
              /session         显示会话日志路径
              /setup           运行交互式供应商配置向导
              /undo            撤销最近一次文件修改（write/edit）
              /diff            显示最近一次修改的 diff
              /save <名>       保存当前会话（命名快照）
              /load <名>       恢复已保存的会话
              /export [名]     导出会话为 Markdown
              /stats           显示 token 用量统计
              /retry           重新执行上一条请求
              /tools           列出可用工具
              /providers       显示已配置的 Provider
              /mode [名称]     查看或切换工作模式（内置 7 种 + 自定义）
              /exit, /quit     退出
            用法:
              codeagent "帮我给项目写个 README"    一次性任务
              codeagent                           进入交互模式
            参数:
              -c, --config <路径>  指定配置文件
              -p, --provider <名>  切换 Provider（配置中的键）
              -m, --model <模型>   覆盖模型名
              --cwd <目录>         切换工作目录
              --init               生成示例配置 codeagent.json
              --setup              交互式配置供应商并生成 codeagent.json
              -v, --version        显示版本号
            """);
    }

    private static void PrintHelp() => PrintReplHelp();
}
