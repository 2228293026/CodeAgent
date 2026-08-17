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

    /// <summary>回合被用户取消的哨兵返回（区别于模型回复文本：模型内容可能含"已取消"字样）。</summary>
    private const string CancelledTurnMarker = "\u001bCANCELLED_TURN";

    /// <summary>判断结果是否为「用户取消」哨兵（精确匹配，防止模型文本含"已取消"被误判）。</summary>
    internal static bool IsCancelledTurn(string? result) => result == CancelledTurnMarker;

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

        // 流式输出即时刷新：dotnet run 等管道环境把 stdout 块缓冲，文本会等回车/退出才显示
        try
        {
            Console.SetOut(new System.IO.StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding) { AutoFlush = true });
        }
        catch { /* 忽略 */ }

        string? configPath = null, provider = null, model = null, cwd = null;
        var init = false;
        var setup = false;
        var listModels = false;
        var positional = new List<string>();

        try
        {
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
                    case "--models":
                        listModels = true;
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
        }
        catch (ArgumentException ex)
        {
            // 参数缺值（如 codeagent -c）应友好提示而非抛堆栈
            Console.Error.WriteLine($"参数错误: {ex.Message}");
            PrintHelp();
            return 2;
        }

        // --cwd 先于 --init/--setup 生效：--init 生成的示例配置应写入目标目录
        if (cwd is not null)
        {
            if (!Directory.Exists(cwd))
            {
                Console.Error.WriteLine($"目录不存在: {cwd}");
                return 2;
            }
            Environment.CurrentDirectory = Path.GetFullPath(cwd);
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
                // 尊重 -c 指定的配置文件路径；未指定时向导保存到当前目录 codeagent.json
                SetupWizard.Run(config, Console.In, Console.Out, configPath);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("已取消配置向导。");
            }
            return 0;
        }

        // ADOFAI mod 项目自动适配：检测到项目特征（Info.json 入口声明或 Assembly-CSharp.dll 引用）时，
        // 注入专属开发上下文（系统提示）与 moddev/harmony/assetbundle 工作模式。
        // 仅当用户未自定义 systemPrompt 时追加知识；用户自定义的 modes 同名项优先保留。
        if (AdofaiContext.Detect(Environment.CurrentDirectory))
        {
            if (config.SystemPrompt == AgentConfig.DefaultSystemPrompt)
            {
                config.SystemPrompt += "\n\n" + AdofaiContext.ExtraSystemPrompt;
                // 知识库文件存在时给出明确路径，Agent 开发前先 read_file 阅读
                var knowledge = AdofaiContext.FindKnowledgeBase(Environment.CurrentDirectory);
                if (knowledge is not null)
                    config.SystemPrompt += $"\n知识库文件: {knowledge}(开发前用 read_file 阅读)";
            }
            foreach (var m in AdofaiContext.ExtraModes)
                if (!config.Modes.Any(x => x.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase)))
                    config.Modes.Add(m);
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

        // 应用配置的默认工作模式（如 "defaultMode": "debug"）
        agent.SetMode(Modes.Find(config.DefaultMode, config));

        // 列出可用模型模式
        if (listModels)
        {
            await PrintModelsAsync(providerInst, opts.Model);
            agent.Close();
            return 0;
        }

        // 一次请求模式
        if (positional.Count > 0)
        {
            try
            {
                var result = await RunTurnAsync(t => agent.RunAsync(string.Join(" ", positional), t));
                if (IsCancelledTurn(result))
                    result = "\n⏹ 已取消。"; // 哨兵映射回显示文本（一次性模式没有草稿回填）
                PrintResult(result, agent.StreamedLastRun, prefixNewline: false);
                agent.Close();
                return agent.LastTurnFailed ? 1 : 0; // 空回复视为失败，非零退出码供脚本判断
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
        var modeTuples = Modes.Build(config).Select(m => (m.Name, m.Description)).ToList();

        // 清掉上一会话遗留的输入缓冲（否则新会话一启动就被旧按键触发菜单/命令，显得"诡异"）
        try
        {
            while (Console.KeyAvailable)
                Console.ReadKey(intercept: true);
        }
        catch { /* 管道/重定向环境忽略 */ }

        var pendingDraft = (string?)null; // 取消回合后回填到输入框的草稿
        while (true)
        {
            PrintStatusBar(opts, agent, config.ThinkingEffort);
            var line = InputLine.Read(PromptFor(opts, agent), modeTuples, config.TuiAnsi, pendingDraft);
            pendingDraft = null;
            if (line is null)
                break; // EOF (Ctrl+Z / Ctrl+D)
            if (line == InputLine.RecallMarker)
            {
                // ESC：撤回最后一条已发送的消息
                Console.WriteLine(agent.UndoLastTurn() ?? "没有可撤回的轮次。");
                continue;
            }
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
                    HandleCommand(line, config, configPath, ref opts, agent, ref providerInst, tools);
                    continue;
                }
            }

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = await RunTurnAsync(t => agent.RunAsync(line, t));
                sw.Stop();
                if (IsCancelledTurn(result))
                {
                    // ESC/Ctrl+C 取消本轮：撤回未完成的消息，输入恢复到输入框（可修改重发）
                    agent.UndoLastTurn();
                    pendingDraft = line;
                }
                if (agent.LastTurnFailed)
                {
                    // 空回复：红色 ⚠ 明确提示失败
                    SafeColor.Foreground(ConsoleColor.Red);
                    Console.WriteLine("⚠ " + result);
                    SafeColor.Reset();
                }
                else
                {
                    PrintResult(result, agent.StreamedLastRun, prefixNewline: true);
                }
                PrintTurnSummary(agent, sw.Elapsed);
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

    internal static string NextArg(string[] args, ref int i, string flag)
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

        // ESC 监听：运行期间按 ESC 停止模型思考/取消本轮。
        // 注意：ReadKey 会消费回合期间的按键（typeahead 会被吞），但用户主动按 ESC 停思考是刻意操作；
        // stop 标志 + 等待退出，避免回合结束后吞掉输入行第一个键。
        var stop = false;
        var watcher = Task.Run(() =>
        {
            try
            {
                while (!stop && !cts.IsCancellationRequested)
                {
                    if (!stop && Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(intercept: true);
                        if (!stop && key.Key == ConsoleKey.Escape)
                            cts.Cancel();
                    }
                    Thread.Sleep(20);
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
            // 返回哨兵而非文本：REPL 用 IsCancelledTurn 精确判断，避免模型回复含"已取消"字样被误判
            return CancelledTurnMarker;
        }
        finally
        {
            _activeTurn = null;
            cts.Cancel();
            stop = true; // 让监听立即退出，避免吞掉接下来的用户输入
            try { watcher.Wait(TimeSpan.FromMilliseconds(300)); } catch { /* 忽略 */ }
        }
    }

    /// <summary>显示当前对话历史（系统提示不计入条数，内容截断）。</summary>
    private static void PrintConversation(AgentClass agent)
    {
        var msgs = agent.Messages.Where(m => m.Role != MessageRole.System).ToList();
        if (msgs.Count == 0)
        {
            Console.WriteLine("对话历史为空（还没有对话消息，直接输入内容开始对话）。");
            return;
        }
        Console.WriteLine($"对话历史（{msgs.Count} 条）:");
        foreach (var m in msgs)
        {
            var role = m.Role switch
            {
                MessageRole.User => "用户",
                MessageRole.Assistant => "助手",
                MessageRole.Tool => $"工具{m.ToolName}",
                _ => m.Role.ToString(),
            };
            var content = m.Content ?? "";
            if (content.Length > 300)
                content = content[..300] + "…";
            Console.WriteLine($"  [{role}] {content}");
        }
    }

    /// <summary>回合结束后打印摘要行（轮数/工具/时长/思考/tokens/缓存比例）——灰色弱化视觉噪音。
    /// token 显示会话累计（Total*），与状态栏/定格行口径一致。</summary>
    private static void PrintTurnSummary(AgentClass agent, TimeSpan elapsed)
    {
        var cache = agent.TurnInputTokens > 0 ? $" {TextUtil.PercentOf(agent.TurnCachedTokens, agent.TurnInputTokens)}% cached" : "";
        var think = agent.TurnThinkingSeconds > 0 ? $" 思考 {agent.TurnThinkingSeconds:F1}s" : "";
        SafeColor.Foreground(ConsoleColor.DarkGray);
        Console.WriteLine(
            $"── ✓ 完成 {agent.TurnRounds} 轮 {agent.TurnToolCalls} 次工具调用 " +
            $"{TextUtil.FormatElapsed(elapsed)} {agent.TotalInputTokens:N0} in / {agent.TotalOutputTokens:N0} out tok{think}{cache} ──");
        SafeColor.Reset();
    }

    /// <summary>切换权限模式后写回配置文件（/access 与 Shift+Tab 用），使重启后保持该模式。</summary>
    private static void PersistFileAccess(AgentConfig config)
    {
        if (config.SourceFile is null)
        {
            Console.WriteLine("（无配置文件，切换仅本次会话生效；用 --init 生成 codeagent.json 后可持久化）");
            return;
        }
        try
        {
            AgentConfig.Save(config, config.SourceFile);
            Console.WriteLine($"已写入配置文件: {config.SourceFile}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"写入配置失败: {ex.Message}");
        }
    }

    /// <summary>显示文件访问权限模式与说明（/access 与 Shift+Tab 用）。</summary>
    private static void PrintFileAccess(string mode, bool showHint = false)
    {
        var desc = mode.ToLowerInvariant() switch
        {
            "strict" => "仅工作区可读写",
            "whitelist" => "工作区读写 + 只读白名单目录",
            "full" => "所有文件可读可写（完全放开）",
            _ => mode,
        };
        Console.WriteLine($"权限模式: {mode}（{desc}）");
        if (showHint)
            Console.WriteLine("  Shift+Tab 或 /access next 循环切换; /access <strict|whitelist|full> 直接指定");
    }

    /// <summary>按 diff 行首标记着色输出：+ 绿 / - 红 / @@ 青 / == 标题亮白 / ---+++ 文件头灰。</summary>
    private static void PrintColoredDiff(string diff)
    {
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("== ", StringComparison.Ordinal))
                SafeColor.Foreground(ConsoleColor.White);       // 文件标题
            else if (line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("+++", StringComparison.Ordinal))
                SafeColor.Foreground(ConsoleColor.DarkGray);    // 文件头
            else if (line.StartsWith("@@", StringComparison.Ordinal))
                SafeColor.Foreground(ConsoleColor.Cyan);        // hunk 头
            else if (line.StartsWith('+'))
                SafeColor.Foreground(ConsoleColor.Green);       // 新增
            else if (line.StartsWith('-'))
                SafeColor.Foreground(ConsoleColor.Red);         // 删除
            Console.WriteLine(line);
            SafeColor.Reset();
        }
    }

    /// <summary>状态栏：模式 · 模型 · 目录 · 上下文总量 · 思考强度（每轮提示符前显示）——灰色。</summary>
    private static void PrintStatusBar(ProviderOptions opts, AgentClass agent, string thinkingEffort)
    {
        var think = thinkingEffort != "off" ? $" · think:{thinkingEffort}" : "";
        SafeColor.Foreground(ConsoleColor.DarkGray);
        // 状态栏显示单独一次对话（上一回合）的 token 用量，与回合摘要行 in/out 格式一致；
        // 会话累计消耗见摘要行与 /stats
        Console.WriteLine(
            $"⏵ {agent.CurrentMode.Name} · {opts.Model} · {Environment.CurrentDirectory} · " +
            $"{TextUtil.CompactTokenCount(agent.TurnInputTokens)} in / {TextUtil.CompactTokenCount(agent.TurnOutputTokens)} out tok{think}");
        SafeColor.Reset();
    }

    /// <summary>构建提示符：[模式|模型短名] 目录名> </summary>
    private static string PromptFor(ProviderOptions opts, AgentClass agent)
    {
        var model = TextUtil.ShortModelName(opts.Model);
        var dir = new DirectoryInfo(Environment.CurrentDirectory).Name;
        return $"\n[{agent.CurrentMode.Name}|{model}] {dir}> ";
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

    /// <summary>列出当前 Provider 的可用模型，并用 * 标记当前配置的模型。</summary>
    private static async Task PrintModelsAsync(IAgentProvider provider, string? currentModel)
    {
        try
        {
            var models = await provider.ListModelsAsync(CancellationToken.None);
            Console.WriteLine($"可用模型（{provider.Name}，共 {models.Count} 个）:");
            var marked = false;
            for (int i = 0; i < models.Count; i++)
            {
                var m = models[i];
                var num = $"{i + 1}) ";
                if (string.Equals(m, currentModel, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  {num}{m}  *");
                    marked = true;
                }
                else
                {
                    Console.WriteLine($"  {num}{m}");
                }
            }
            Console.WriteLine("  提示: /model <编号> 可直接切换");
            if (marked)
                Console.WriteLine("  * = 当前配置的模型");
            else if (!string.IsNullOrWhiteSpace(currentModel))
                Console.WriteLine($"  （当前配置的模型不在列表中: {currentModel}）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠ 无法获取模型列表: {ex.Message}");
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
        string? configPath,
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

            case "/cls":
                try
                {
                    Console.Clear();
                    PrintBanner(config, opts, agent);
                }
                catch
                {
                    // 重定向终端不支持清屏
                }
                break;

            case "/access":
                // 文件访问权限模式：Shift+Tab 触发 /access next 循环切换，或 /access <strict|whitelist|full> 直接指定
                if (rest.Equals("next", StringComparison.OrdinalIgnoreCase))
                {
                    var next = config.FileAccess.ToLowerInvariant() switch
                    {
                        "strict" => "whitelist",
                        "whitelist" => "full",
                        _ => "strict",
                    };
                    agent.SetFileAccess(next);
                    PrintFileAccess(next);
                    PersistFileAccess(config); // 写回配置文件，重启后保持
                }
                else if (!string.IsNullOrWhiteSpace(rest))
                {
                    var mode = rest.Trim().ToLowerInvariant();
                    if (mode is "strict" or "whitelist" or "full")
                    {
                        agent.SetFileAccess(mode);
                        PrintFileAccess(mode);
                        PersistFileAccess(config);
                    }
                    else
                    {
                        Console.WriteLine($"无效权限模式: {rest}(可选 strict | whitelist | full)");
                    }
                }
                else
                {
                    PrintFileAccess(config.FileAccess, showHint: true);
                }
                break;

            case "/model":
                if (string.IsNullOrWhiteSpace(rest))
                {
                    Console.WriteLine($"当前模型: {opts.Model}");
                }
                else
                {
                    var modelArg = rest.Trim();
                    // 数字参数：从 /models 列表按编号选择（如 /model 5）
                    if (int.TryParse(modelArg, out var idx) && idx >= 1)
                    {
                        try
                        {
                            var models = providerInst.ListModelsAsync(CancellationToken.None).GetAwaiter().GetResult();
                            if (idx <= models.Count)
                            {
                                modelArg = models[idx - 1];
                            }
                            else
                            {
                                Console.WriteLine($"无效编号（可选 1-{models.Count}，/models 查看）");
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"获取模型列表失败: {ex.Message}");
                            break;
                        }
                    }
                    opts.Model = modelArg;
                    try
                    {
                        providerInst = ProviderFactory.Create(config);
                        agent.SetProvider(providerInst);
                        // 同步回配置并持久化，重启后仍然生效
                        if (config.Providers.TryGetValue(config.Provider, out var po))
                            po.Model = opts.Model;
                        var savePath = string.IsNullOrWhiteSpace(configPath) ? "codeagent.json" : configPath;
                        AgentConfig.Save(config, savePath);
                        Console.WriteLine($"已切换模型: {opts.Model}，已保存到 {savePath}");
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
                try
                {
                    // 尊重 -c 指定的配置文件路径；未指定时向导保存到当前目录 codeagent.json
                    SetupWizard.Run(config, Console.In, Console.Out, configPath);
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("已取消配置向导。");
                    break;
                }
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
                // 用法: /undo 撤销最近一次; /undo N 撤销最近 N 次; /undo list 列出历史; 多条时可交互选择
                if (int.TryParse(rest.Trim(), out var undoN) && undoN >= 1)
                {
                    Console.WriteLine(agent.Context.Undo.TryUndo(undoN) ?? "没有可撤销的操作。");
                }
                else if (rest.Equals("list", StringComparison.OrdinalIgnoreCase))
                {
                    var list = agent.Context.Undo.ListEntries();
                    Console.WriteLine(list.Length == 0 ? "没有可撤销的操作。" : $"可撤销操作（编号 1 = 最近）:\n{list}");
                }
                else if (string.IsNullOrWhiteSpace(rest))
                {
                    var list = agent.Context.Undo.ListEntries();
                    if (list.Length == 0)
                    {
                        Console.WriteLine("没有可撤销的操作。");
                    }
                    else if (agent.Context.Undo.Count == 1)
                    {
                        Console.WriteLine(agent.Context.Undo.TryUndo());
                    }
                    else
                    {
                        Console.WriteLine($"可撤销操作（编号 1 = 最近;输入编号撤销到该步,回车撤销最近一次,其他取消）:\n{list}");
                        var pick = Console.ReadLine()?.Trim();
                        if (int.TryParse(pick, out var sel) && sel >= 1)
                            Console.WriteLine(agent.Context.Undo.TryUndo(sel));
                        else if (string.IsNullOrEmpty(pick))
                            Console.WriteLine(agent.Context.Undo.TryUndo());
                        else
                            Console.WriteLine("已取消。");
                    }
                }
                else
                {
                    Console.WriteLine("用法: /undo [N|list] —— N = 撤销最近 N 次, list = 列出历史");
                }
                break;

            case "/diff":
                var diffText = agent.Context.Undo.AllDiffs();
                if (diffText is null)
                    Console.WriteLine("没有可显示的改动（先让 agent 修改过文件）。");
                else
                    PrintColoredDiff(diffText); // 着色输出：+/绿、-/红、@@/青、标题/亮白、文件头/灰
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
                    // 无参数：列出已保存的命名会话
                    var dir = Path.Combine(Environment.CurrentDirectory, config.SessionDir);
                    var files = Directory.Exists(dir)
                        ? Directory.GetFiles(dir, "*.json").Select(Path.GetFileNameWithoutExtension).ToList()
                        : [];
                    if (files.Count == 0)
                    {
                        Console.WriteLine("没有已保存的会话（用 /save <会话名> 保存当前对话）。");
                    }
                    else
                    {
                        Console.WriteLine($"已保存的会话（{files.Count} 个）:");
                        foreach (var f in files)
                            Console.WriteLine($"  {f}");
                    }
                }
                else
                {
                    try
                    {
                        agent.LoadSession(rest.Trim());
                        Console.WriteLine($"✔ 已恢复会话: {rest.Trim()}");
                        PrintConversation(agent); // 显示恢复的消息，避免"看不到之前的消息"
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"加载失败: {ex.Message}");
                    }
                }
                break;

            case "/history":
                PrintConversation(agent);
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
                    Console.WriteLine("（提示: 按 Alt+M 弹出模式菜单，Shift+Tab 快速切换下一个模式）");
                }
                else if (rest.Equals("next", StringComparison.OrdinalIgnoreCase))
                {
                    // /mode next：循环切换到下一个模式（Shift+Tab 快捷键映射到这里）
                    var modes = Modes.Build(config);
                    var idx = modes.FindIndex(m => m.Name.Equals(agent.CurrentMode.Name, StringComparison.OrdinalIgnoreCase));
                    if (idx < 0)
                        idx = 0;
                    var next = modes[(idx + 1) % modes.Count];
                    agent.SetMode(next);
                    Console.WriteLine($"已切换模式: {next.Name} — {next.Description}");
                }
                else
                {
                    var mode = Modes.Find(rest, config);
                    agent.SetMode(mode);
                    Console.WriteLine($"已切换模式: {mode.Name} — {mode.Description}");
                }
                break;

            case "/diag":
                // 终端环境诊断：定位输入卡顿 / 菜单渲染问题
                Console.WriteLine("终端诊断:");
                Console.WriteLine($"  IsInputRedirected : {Console.IsInputRedirected}");
                static string W(Func<int> f)
                {
                    try { return f().ToString(); }
                    catch (Exception e) { return $"读取失败: {e.Message}"; }
                }
                Console.WriteLine($"  WindowWidth       : {W(() => Console.WindowWidth)}");
                Console.WriteLine($"  WindowHeight      : {W(() => Console.WindowHeight)}");
                Console.WriteLine($"  BufferWidth       : {W(() => Console.BufferWidth)}");
                Console.WriteLine($"  CursorLeft/Top    : {W(() => Console.CursorLeft)}/{W(() => Console.CursorTop)}");
                Console.WriteLine($"  TuiAnsi           : {config.TuiAnsi}");
                Console.WriteLine($"  OutputEncoding    : {Console.OutputEncoding.WebName}");
                break;

            case "/models":
                PrintModelsAsync(providerInst, opts.Model).GetAwaiter().GetResult();
                break;

            case "/thinking":
                if (string.IsNullOrWhiteSpace(rest))
                {
                    Console.WriteLine($"思考强度: {config.ThinkingEffort}（可选: off / low / medium / high）");
                    Console.WriteLine("提示: 仅对支持推理参数的模型生效（OpenAI o 系列 / OpenRouter reasoning，Anthropic thinking）");
                }
                else
                {
                    var v = rest.Trim().ToLowerInvariant();
                    if (v is "off" or "low" or "medium" or "high")
                    {
                        config.ThinkingEffort = v;
                        // 持久化到配置文件，重启后仍然生效
                        try
                        {
                            var savePath = string.IsNullOrWhiteSpace(configPath) ? "codeagent.json" : configPath;
                            AgentConfig.Save(config, savePath);
                            Console.WriteLine($"思考强度已设为: {v}，已保存到 {savePath}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"思考强度已设为: {v}（保存配置失败: {ex.Message}）");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"无效值: {rest}（可选: off / low / medium / high）");
                    }
                }
                break;

            case "/help":
                PrintReplHelp();
                break;

            case "/":
                PrintReplHelp();
                break;

            default:
                Console.WriteLine($"未知命令: {cmd}（输入 /help 查看命令，Tab 可补全）");
                break;
        }
    }

    internal static (string cmd, string rest) SplitCommand(string line)
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
              /cls             清空屏幕（或按 Ctrl+L）
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
              /models          列出当前 Provider 的可用模型
              /history         显示当前对话历史
              /thinking        查看或设置思考强度（off/low/medium/high）
              /mode [名称]     查看或切换工作模式（内置 8 种 + 自定义）
              /access [模式]   查看或切换文件访问权限（strict/whitelist/full，next 循环切换）
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
              --models             列出当前 Provider 的可用模型
              -v, --version        显示版本号
            快捷键:
              Esc                   撤回最后一条已发送的消息（空输入时）
              Tab                    切换下一个工作模式（/mode next）
              Shift+Tab              切换文件访问权限模式（strict→whitelist→full）
              Alt+M / Ctrl+Shift+M   模式切换菜单
              Alt+U / Ctrl+Shift+U   撤销最近一次文件修改（/undo）
              Alt+D / Ctrl+Shift+D   查看最近修改的 diff（/diff）
              Alt+N / Ctrl+Shift+N   新建会话（/clear）
              Ctrl+L                 清屏
            """);
    }

    private static void PrintHelp() => PrintReplHelp();
}
