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
    /// <summary>会话总时长（/stats 显示用；从进程启动计）。/clear 不重置——统计的是会话进程本身。</summary>
    private static readonly System.Diagnostics.Stopwatch SessionStopwatch = System.Diagnostics.Stopwatch.StartNew();

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

        // 流式输出即时刷新：dotnet run 等管道环境把 stdout 块缓冲，文本会等回车/退出才显示。
        // 用无 BOM 的 UTF-8：默认 UTF8Encoding 会把 BOM 前导写进终端（横幅开头出现不可见字符）
        try
        {
            Console.SetOut(new System.IO.StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true });
        }
        catch { /* 忽略 */ }

        string? configPath = null, provider = null, model = null, cwd = null, modeOverride = null;
        var init = false;
        var setup = false;
        var listModels = false;
        var continueLast = false;
        var noSession = false; // --no-session：本次运行不落盘会话日志
        var resumeIndex = 0; // --resume <编号>：按 /resume 列表编号恢复历史会话
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
                    case "--mode":
                        modeOverride = NextArg(args, ref i, "--mode");
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
                    case "--continue":
                        continueLast = true;
                        break;
                    case "--no-session":
                        // 本次运行不写会话日志（隐私敏感的一次性任务）：仅进程内生效，不写回配置文件
                        noSession = true;
                        break;

                    case "--resume":
                        resumeIndex = int.TryParse(NextArg(args, ref i, "--resume"), out var ri) && ri >= 1 ? ri : 0;
                        if (resumeIndex == 0)
                            throw new ArgumentException("--resume 需要一个 ≥1 的编号（/resume 查看列表）");
                        break;
                    case "-v" or "--version":
                        Console.WriteLine($"codeagent {InformationalVersion}");
                        return 0;
                    case "-h" or "--help":
                        PrintHelp();
                        return 0;
                    default:
                        // 未识别的 -flag 此前被静默拼进任务文本发给模型（--verbos 变成任务的一部分）；
                        // 明确报错让用户发现拼写错误。纯任务文本以 "-" 开头属罕见场景（用 -- 分隔或去掉横线）。
                        if (LooksLikeUnknownFlag(args[i]))
                            throw new ArgumentException($"未知参数: {args[i]}（-h 查看用法）");
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
                Console.WriteLine($"codeagent.json 已存在: {target}（未覆盖——误删配置防护；删除后重跑 --init 可重新生成）");
                return 0;
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

        // 配置非致命警告（未知配置项 / 枚举回退）：拼写错误此前被静默忽略，用户无从得知「配了不生效」
        foreach (var warning in config.Warnings)
        {
            SafeColor.Foreground(ConsoleColor.DarkYellow);
            Console.WriteLine($"⚠ 配置: {warning}");
            SafeColor.Reset();
        }

        // 交互式供应商配置向导：生成/更新 codeagent.json 后退出
        if (setup)
        {
            try
            {
                // 尊重 -c 指定的配置文件路径；未指定时写回实际加载的来源文件（可能是 ~/.codeagent/config.json）
                SetupWizard.Run(config, Console.In, Console.Out, ConfigSavePath(configPath, config), testConnection: true);
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
                // 只写入会话级字段：/model、/thinking、/access 等命令会保存整个 config，
                // 直接改 SystemPrompt 会把注入的 mod 上下文永久写进用户的 codeagent.json
                var injected = config.SystemPrompt + "\n\n" + AdofaiContext.ExtraSystemPrompt;
                // 知识库文件存在时给出明确路径，Agent 开发前先 read_file 阅读
                var knowledge = AdofaiContext.FindKnowledgeBase(Environment.CurrentDirectory);
                if (knowledge is not null)
                    injected += $"\n知识库文件: {knowledge}(开发前用 read_file 阅读)";
                config.SessionOnlySystemPrompt = injected;
            }
            foreach (var m in AdofaiContext.ExtraModes)
                if (!config.Modes.Any(x => x.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase)))
                    config.Modes.Add(m);
        }

        // 命令行/环境变量覆盖：provider 与 model。
        // 环境变量同时接受 CODEAGENT_* 与历史拼写 CODEGENT_*（后者像笔误，按自然名设置曾静默无效），
        // CODEAGENT_* 优先
        var envProvider = FirstEnvVar("CODEAGENT_PROVIDER", "CODEGENT_PROVIDER");
        var envModel = FirstEnvVar("CODEAGENT_MODEL", "CODEGENT_MODEL");
        // 记住持久层原始 provider：后续 /thinking 等命令保存配置时按它写回，
        // 环境变量/命令行的会话级覆盖不会固化成配置默认值（显式 /provider 切换除外）
        config.PersistedProvider = config.Provider;
        if (provider is not null)
            config.Provider = provider;
        else if (!string.IsNullOrWhiteSpace(envProvider))
            config.Provider = envProvider;

        // 拼错的 provider 名此前会静默落到空的 openai 配置，报错变成误导性的「缺 API Key」。
        // 在创建 Provider 前先校验名字，给出可用列表
        if (config.Providers.Count > 0 && !config.Providers.ContainsKey(config.Provider)) // 空配置走默认，不误伤
        {
            Console.Error.WriteLine($"未知 provider「{config.Provider}」（可用: {string.Join(", ", config.Providers.Keys)}）");
            return 2;
        }

        var opts = EnsureSelectedProvider(config);
        if (model is not null)
            opts.Model = model.Trim(); // 手滑带上的前后空白会原样进请求（模型名 404）
        else if (!string.IsNullOrWhiteSpace(envModel))
            opts.Model = envModel.Trim();

        // --no-session：仅进程内关闭会话落盘。放在 --setup 之后（向导保存配置时不会把
        // 该覆盖持久化成 SaveSessions=false），Agent 构造前生效即可完全不创建日志文件。
        if (noSession)
            config.SaveSessions = false;

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

        // --continue：恢复最近会话。必须在 SetMode 之前加载——SetMode 会把 messages[0]
        // 的旧 system 提示换成当前模式的提示词
        if (continueLast || resumeIndex > 0)
        {
            var logs = RecentSessionLogs(config, Math.Max(resumeIndex, 1));
            string? target = null;
            if (resumeIndex > 0)
            {
                if (resumeIndex <= logs.Count)
                    target = logs[resumeIndex - 1];
                else
                    Console.WriteLine($"⚠ --resume 编号超出范围（可用 1-{logs.Count}）。");
            }
            else if (logs.Count > 0)
                target = logs[0];
            if (target is null)
                Console.WriteLine("没有可恢复的会话记录（先正常对话过一次，或检查 saveSessions 配置）。");
            else if (agent.LoadSessionLog(target))
                Console.WriteLine($"↩ 已恢复会话: {Path.GetFileName(target)}");
            else
                Console.WriteLine("⚠ 会话日志无法恢复（文件可能损坏）。");
        }
        // 应用工作模式：--mode <名> 会话级覆盖（不落盘），否则用配置的 defaultMode（如 "debug"）
        var modeName = modeOverride ?? config.DefaultMode;
        var mode = Modes.Find(modeName, config);
        if (!string.Equals(mode.Name, modeName.Trim(), StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"⚠ 未知模式「{modeName}」，已回退到 {mode.Name}（/mode 查看可用模式）。");
        agent.SetMode(mode);

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
                var task = ComposeTaskWithStdin(string.Join(" ", positional),
                    Console.IsInputRedirected ? await Console.In.ReadToEndAsync() : "");
                // 空任务防御（codeagent ""）：空 user 消息对部分 API 是非法请求（Anthropic 侧
                // 会被归一化掉导致 messages 为空数组直接 400），与其等远端报错不如本地明确提示
                if (string.IsNullOrWhiteSpace(task))
                {
                    Console.Error.WriteLine("任务为空：请提供任务描述（codeagent \"任务\"），或直接运行 codeagent 进入交互模式。");
                    agent.Close();
                    return 2;
                }
                var result = await RunTurnAsync(t => agent.RunAsync(task, t));
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
        if (NeedsGitignoreHint(Environment.CurrentDirectory))
            Console.WriteLine("提示: .codeagent/ 未被 .gitignore 忽略——会话日志含代码内容，建议加入 .gitignore");
        PrintBanner(config, opts, agent);
        var modeTuples = Modes.Build(config).Select(m => (m.Name, m.Description)).ToList();

        // 后台探测一次模型上下文窗口（OpenRouter 风格 /models 元数据）：失败静默、不阻塞启动，
        // 完成后状态栏即可用（优先级：contextWindow 配置 > 内置模型表 > 此探测）
        var ctxProbe = new ContextProbeState
        {
            Model = opts.Model,
            Task = providerInst.GetContextWindowAsync(opts.Model, CancellationToken.None),
        };

        // 后台探测一次模型推理能力（/models 元数据 reasoning.effort 字段 > 内置模型名前缀表）：
        // 供 /thinking 与状态栏显示 auto 的实际生效值；失败静默、不阻塞启动
        var reasoningProbe = new ReasoningProbeState
        {
            Model = opts.Model,
            Task = providerInst.GetSupportedEffortsAsync(opts.Model, CancellationToken.None),
        };

        // 有效上下文窗口：0 = 未知（状态栏只显示 ctx 绝对值）
        int EffectiveContextWindow() => Program.EffectiveContextWindow(config, opts, ctxProbe);

        // 切换块原地覆盖按「提示符占一行」计算：窄终端 + 长模式/模型/目录名会让提示符折行，
        // 此时退回追加模式，避免错位覆盖（留 8 列余量吸收目录里的 CJK 双宽字符）
        bool PromptFitsOneRow()
        {
            try { return PromptFor(opts, agent).TrimStart('\n').Length < Console.WindowWidth - 8; }
            catch { return true; }
        }

        // 清掉上一会话遗留的输入缓冲（否则新会话一启动就被旧按键触发菜单/命令，显得"诡异"）
        try
        {
            while (Console.KeyAvailable)
                Console.ReadKey(intercept: true);
        }
        catch { /* 管道/重定向环境忽略 */ }

        var pendingDraft = (string?)null; // 取消回合后回填到输入框的草稿
        var skipStatusBar = false; // 模式/权限切换已有一行确认：下一轮跳过状态栏（防模式名重复三处）
        var switchBlockActive = false; // 提示符上方是「消息+空行」切换块：连续切换可原地覆盖，屏幕零增长
        string? inlinePrompt = null; // 原地重绘后 Read 用无前导换行的提示符（空行已由切换块写好）
        var ansiOk = config.TuiAnsi && !Console.IsOutputRedirected;
        while (true)
        {
            if (!skipStatusBar)
                PrintStatusBar(opts, agent, config.ThinkingEffort, EffectiveContextWindow(), reasoningProbe);
            skipStatusBar = false;
            var line = InputLine.Read(inlinePrompt ?? PromptFor(opts, agent), modeTuples, config.TuiAnsi, pendingDraft);
            inlinePrompt = null;
            var couldOverwriteBlock = switchBlockActive;
            switchBlockActive = false; // 默认失效：只有本轮再次切换才重新置位（中间任何输入都会改变块上方布局）
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
            // 全角斜杠归一化：CJK 输入法打出的 ／model 与 /model 同义（菜单过滤已兼容，这里补上执行路径）
            line = InputLine.NormalizeCommandFilter(line);
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
                    // 模式/权限切换（Tab/Shift+Tab 或 /mode //access）：连续切换时原地覆盖上一轮的
                    // 「消息+空行+提示符」三行块——光标此刻在旧提示符行的下一行行首，上移 3 行即块顶
                    var (peekCmd, peekRest) = SplitCommand(line);
                    var isSwitch = IsSwitchCommand(peekCmd, peekRest);
                    if (isSwitch && couldOverwriteBlock && ansiOk && PromptFitsOneRow())
                        Console.Write("\x1b[3A");
                    var suppress = HandleCommand(line, config, configPath, ref opts, agent, ref providerInst, tools, ctxProbe, reasoningProbe);
                    skipStatusBar = suppress;
                    if (suppress)
                    {
                        switchBlockActive = true;
                        // 空行并清整行：覆盖路径下新提示符可能比旧的短（[explain|…]→[doc|…]），清掉行尾残字符
                        Console.Write(ansiOk ? "\r\n\x1b[2K" : "\n");
                        inlinePrompt = PromptFor(opts, agent).TrimStart('\n');
                    }
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
                PrintTurnSummary(agent, sw.Elapsed, opts);
                // 上下文占用 ≥90%：提示可 /compact（状态栏百分比小、易被忽略）
                {
                    var win = EffectiveContextWindow();
                    if (win > 0 && agent.ContextTokens > 0)
                    {
                        var pct = TextUtil.PercentOf(agent.ContextTokens, win);
                        if (pct >= 90)
                            Console.WriteLine($"⚠ 上下文已用 {pct}%（{TextUtil.CompactTokenCount(agent.ContextTokens)}/{TextUtil.CompactTokenCount(win)}）：建议 /compact 压缩历史，否则即将自动裁剪最旧对话。");
                    }
                }
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

    /// <summary>最近的会话日志文件（.jsonl，最新在前，最多 max 个）；无日志返回空表。
    /// 跳过 0 字节文件：启动后未对话就退出会留下空日志，恢复它毫无意义（曾导致
    /// /resume 与 --continue 报「文件可能损坏」的误导错误）。</summary>
    internal static List<string> RecentSessionLogs(AgentConfig config, int max = 10)
    {
        try
        {
            var dir = Path.Combine(Environment.CurrentDirectory, config.SessionDir);
            if (!Directory.Exists(dir))
                return [];
            // 按最后写入时间排序（同秒滚动的 -2/-3 后缀文件名字典序不可靠）
            return Directory.GetFiles(dir, "*.jsonl")
                .Where(f => new FileInfo(f).Length > 0)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ThenByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>最近一次会话日志路径；无日志返回 null（--continue 用）。</summary>
    internal static string? LatestSessionLog(AgentConfig config) =>
        RecentSessionLogs(config, 1).FirstOrDefault();

    /// <summary>/resume 列表与 /export &lt;编号&gt; 共用的日志列表：排除当前会话自己的日志。
    /// 两处必须同源——/export 曾直接用未过滤的 RecentSessionLogs，当前会话日志挤占 1 号
    /// 时 /export 1 与 /resume 1 会指向不同文件。</summary>
    internal static List<string> ResumableLogs(AgentClass agent, AgentConfig config) =>
        RecentSessionLogs(config)
            .Where(p => !string.Equals(p, agent.SessionPath, StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static ProviderOptions EnsureSelectedProvider(AgentConfig config)
    {
        if (!config.Providers.TryGetValue(config.Provider, out var opts))
        {
            opts = new ProviderOptions();
            config.Providers[config.Provider] = opts;
        }
        return opts;
    }

    /// <summary>按顺序取第一个非空环境变量（别名兜底：CODEAGENT_* 优先，历史拼写 CODEGENT_* 兼容）。</summary>
    internal static string? FirstEnvVar(params string[] names)
    {
        foreach (var n in names)
        {
            var v = Environment.GetEnvironmentVariable(n);
            if (!string.IsNullOrWhiteSpace(v))
                return v;
        }
        return null;
    }

    internal static string NextArg(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"缺少 {flag} 的参数值");
        return args[++i];
    }

    /// <summary>已识别的 CLI 旗标（含值型旗标本身；其参数值不经过此判断）。</summary>
    private static readonly HashSet<string> KnownFlags = new(StringComparer.Ordinal)
    {
        "-c", "--config", "-p", "--provider", "-m", "--model", "--cwd", "--mode",
        "--init", "--setup", "--models", "--continue", "--resume", "--no-session",
        "-v", "--version", "-h", "--help",
    };

    /// <summary>判断是否为未识别的旗标（以 '-' 开头且不在已知列表）：拒绝而非当任务文本。</summary>
    internal static bool LooksLikeUnknownFlag(string arg) =>
        arg.Length > 1 && arg[0] == '-' && !KnownFlags.Contains(arg);

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
                    // 主线程在做 y/N 确认等控制台输入时让路：这里的 ReadKey 会吞按键
                    if (!CodeAgent.Tools.ShellRunner.ConsoleInputBusy && !stop && Console.KeyAvailable)
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
    private static void PrintConversation(AgentClass agent, int? last = null)
    {
        var msgs = agent.Messages.Where(m => m.Role != MessageRole.System).ToList();
        if (msgs.Count == 0)
        {
            Console.WriteLine("对话历史为空（还没有对话消息，直接输入内容开始对话）。");
            return;
        }
        // /history N 只看最近 N 条：长对话全量打印会刷屏
        if (last is { } n && n < msgs.Count)
        {
            msgs = msgs[^n..];
            Console.WriteLine($"对话历史（最近 {n} 条，共 {agent.Messages.Count(m => m.Role != MessageRole.System)} 条）:");
        }
        else
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
            if (m.Role == MessageRole.Assistant && m.ToolCalls is { Count: > 0 })
            {
                // 纯工具调用轮无文本：显示调用了哪些工具（否则 [助手] 后空白，像丢了消息）
                var calls = string.Join(", ", m.ToolCalls.Select(tc => tc.Name));
                content = (content.Length > 0 ? content + " " : "") + $"[调用 {calls}]";
            }
            if (content.Length > 300)
                content = TextUtil.TruncateLine(content, 300);
            // 多行内容折叠为一行（工具结果常带换行，否则会打乱逐条列表）
            content = content.Replace("\r", "").Replace("\n", " ⏎ ");
            Console.WriteLine(m.IsError ? $"  [{role}] ❗ {content}" : $"  [{role}] {content}"); // 工具错误加标记
        }
    }

    /// <summary>回合结束后打印摘要行（轮数/工具/时长/思考/tokens/缓存比例）——灰色弱化视觉噪音。
    /// token 显示本回合用量（与状态栏、spinner 定格行同口径）；会话累计见 /stats。
    /// 配置了单价时附本回合费用估算（≈$x.xx）。</summary>
    private static void PrintTurnSummary(AgentClass agent, TimeSpan elapsed, ProviderOptions opts)
    {
        var cache = agent.TurnInputTokens > 0 ? $" {TextUtil.PercentOf(agent.TurnCachedTokens, agent.TurnInputTokens)}% cached" : "";
        var think = agent.TurnThinkingSeconds > 0 ? $" 思考 {agent.TurnThinkingSeconds:F1}s" : "";
        // 单价优先取当前 provider 的配置，未配置回退全局（多 provider 切换时全局价曾算错费用）
        var cost = TextUtil.UsdCost(agent.TurnInputTokens, agent.TurnOutputTokens,
            opts.PricePerMillionInput > 0 ? opts.PricePerMillionInput : agent.Context.Config.PricePerMillionInput,
            opts.PricePerMillionOutput > 0 ? opts.PricePerMillionOutput : agent.Context.Config.PricePerMillionOutput);
        string costText = "";
        if (cost is { } c)
            costText = $" ≈${TextUtil.FormatCost(c)}";
        SafeColor.Foreground(ConsoleColor.DarkGray);
        Console.WriteLine(
            $"── ✓ 完成 {agent.TurnRounds} 轮 {agent.TurnToolCalls} 次工具调用 " +
            $"{TextUtil.FormatElapsed(elapsed)} {agent.TurnInputTokens:N0} in / {agent.TurnOutputTokens:N0} out tok{think}{cache}{costText} ──");
        SafeColor.Reset();
    }

    /// <summary>
    /// 配置写回路径：-c 显式路径 → 实际加载的来源文件（可能是 ~/.codeagent/config.json）→ 默认当前目录。
    /// 忽略来源文件时，从主目录配置启动的 /model 会把半份配置写进 cwd 的新 codeagent.json，配置被一分为二。
    /// </summary>
    internal static string ConfigSavePath(string? configPath, AgentConfig config) =>
        !string.IsNullOrWhiteSpace(configPath) ? configPath
        : !string.IsNullOrWhiteSpace(config.SourceFile) ? config.SourceFile
        : "codeagent.json";

    /// <summary>已保存的命名会话（新 → 旧，附相对时间）。/load 无参数列表用。</summary>
    internal static IReadOnlyList<(string Name, string Age)> SavedSessions(string sessionDir)
    {
        try
        {
            if (!Directory.Exists(sessionDir))
                return [];
            return Directory.GetFiles(sessionDir, "*.json")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Select(f => (Path.GetFileNameWithoutExtension(f),
                    TextUtil.RelativeTime(File.GetLastWriteTimeUtc(f), DateTime.UtcNow)))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>保存配置，但 provider 按启动时的持久值写回（见 AgentConfig.PersistedProvider）：
    /// /model、/thinking、/shell、/access 等命令的保存不把 CODEAGENT_PROVIDER 之类的
    /// 会话级覆盖固化成配置文件的默认 provider。显式 /provider 切换由调用方先更新
    /// PersistedProvider 再保存（用户明确选择应持久化）。</summary>
    internal static void SaveConfig(AgentConfig config, string path)
    {
        var session = config.Provider;
        if (config.PersistedProvider is not null)
            config.Provider = config.PersistedProvider;
        try { AgentConfig.Save(config, path); }
        finally { config.Provider = session; }
    }

    /// <summary>切换权限模式后写回配置文件（/access 与 Shift+Tab 用），使重启后保持该模式。
    /// 成功时静默（切换确认行已反馈状态，连写两行会破坏连续切换的原地覆盖行数）；失败才提示。</summary>
    private static void PersistFileAccess(AgentConfig config)
    {
        if (config.SourceFile is null)
            return; // 无配置文件：仅本次会话生效（/access 查看时的提示已覆盖此说明）
        try
        {
            SaveConfig(config, config.SourceFile);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠ 写入配置失败: {ex.Message}");
        }
    }

    /// <summary>放开沙箱（fileAccess=full）前的二次确认：工作区外可读写是高危操作。
    /// 已处于 full 时再次经过不重复询问。EOF/非 y 一律视为取消（安全默认）。</summary>
    internal static bool ConfirmFullAccess(TextReader input, TextWriter output)
    {
        output.Write("⚠ 即将完全放开文件沙箱（工作区外可读写，仅限信任场景）。确认? [y/N] ");
        var answer = input.ReadLine()?.Trim();
        if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
            return true;
        output.WriteLine($"已取消（保持当前模式）。");
        return false;
    }

    /// <summary>通用覆盖确认（/save 同名快照等）：只有明确 y 放行，EOF/其他输入取消（安全默认）。</summary>
    internal static bool ConfirmReplace(TextReader input, TextWriter output, string question)
    {
        output.Write($"⚠ {question} [y/N] ");
        return string.Equals(input.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>显示文件访问权限模式与说明（/access 与 Shift+Tab 用）——灰色 UI 层级。</summary>
    private static void PrintFileAccess(string mode, bool showHint = false)
    {
        var desc = mode.ToLowerInvariant() switch
        {
            "strict" => "仅工作区可读写",
            "whitelist" => "工作区读写 + 只读白名单目录",
            "full" => "所有文件可读可写（完全放开）",
            _ => mode,
        };
        SafeColor.Foreground(ConsoleColor.DarkGray);
        Console.WriteLine($"已切换权限: {mode}（{desc}）");
        if (showHint)
            Console.WriteLine("  Shift+Tab 或 /access next 循环切换; /access <strict|whitelist|full> 直接指定");
        SafeColor.Reset();
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

    /// <summary>深路径显示截断：超长时保留尾部（工作区名永远可见），前缀省略号。</summary>
    internal static string TruncatePathHead(string path, int max = 42) =>
        path.Length <= max ? path : "…" + path[^(max - 1)..];

    private static (string Cwd, string? Branch, DateTime At)? _branchCache;

    /// <summary>当前 git 分支（3 秒缓存：状态栏每轮提示符都刷新，直接解析 .git/HEAD 虽廉价也不必每次读盘）。
    /// 非 git 仓库返回 null（显示层整体省略该段）。</summary>
    internal static string? CachedBranch(string cwd)
    {
        var now = DateTime.UtcNow;
        if (_branchCache is { } c && c.Cwd == cwd && (now - c.At).TotalSeconds < 3)
            return c.Branch;
        var b = GitInfo.CurrentBranch(cwd);
        _branchCache = (cwd, b, now);
        return b;
    }

    /// <summary>状态栏：模式 · 模型 · 目录 · 本回合 token · 上下文规模（百分比）· 思考强度（每轮提示符前显示）——灰色。</summary>
    private static void PrintStatusBar(ProviderOptions opts, AgentClass agent, string thinkingEffort, int contextWindow, ReasoningProbeState? reasoningProbe)
    {
        // auto：探测完成后显示实际生效档位（最高可用档），探测中显示 auto
        string think;
        if (thinkingEffort == "auto")
        {
            var t = reasoningProbe?.Task;
            var done = t?.IsCompletedSuccessfully == true
                && string.Equals(opts.Model, reasoningProbe?.Model, StringComparison.OrdinalIgnoreCase);
            var efforts = t?.IsCompletedSuccessfully == true ? t.Result : null;
            // 探测完成且模型未变：显示实际生效档（无支持 → off，与 /thinking 的说明一致）；
            // 探测中或已换模型（结果作废）：仍只显示 auto
            think = done ? $" · think:auto→{(efforts is { Count: > 0 } ? efforts[^1] : "off")}" : " · think:auto";
        }
        else
        {
            think = thinkingEffort != "off" ? $" · think:{thinkingEffort}" : "";
        }
        var ctx = contextWindow > 0
            ? $"ctx {TextUtil.CompactTokenCount(agent.ContextTokens)}/{TextUtil.CompactTokenCount(contextWindow)} ({TextUtil.PercentOf(agent.ContextTokens, contextWindow)}%)"
            : $"ctx {TextUtil.CompactTokenCount(agent.ContextTokens)}";
        var shownCwd = TruncatePathHead(Environment.CurrentDirectory);
        // git 分支段（非仓库整体省略）：多仓库/多分支工作流下快速确认当前所在位置
        var branch = CachedBranch(Environment.CurrentDirectory);
        var branchText = branch is null ? "" : $" ({branch})";
        SafeColor.Foreground(ConsoleColor.DarkGray);
        Console.WriteLine(
            $"⏵ {agent.CurrentMode.Name} · {opts.Model} · {shownCwd}{branchText} · " +
            $"{TextUtil.CompactTokenCount(agent.TurnInputTokens)} in / {TextUtil.CompactTokenCount(agent.TurnOutputTokens)} out · {ctx}{think}");
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

    /// <summary>从模型列表中找与输入相近的候选（按输入首个家族段做包含匹配，忽略大小写；最多 max 个）。</summary>
    internal static IReadOnlyList<string> SuggestModels(IReadOnlyList<string> models, string input, int max = 3)
    {
        var family = input.Split('-', '.')[0];
        if (family.Length == 0)
            return [];
        return models
            .Where(m => m.Contains(family, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    /// <summary>带编号的模型展示行：编号始终按完整列表（/model &lt;编号&gt; 按完整列表解析），
    /// 过滤只影响显示哪些行——否则过滤后的编号与 /model 解析错位。</summary>
    internal static IReadOnlyList<(int Num, string Model)> NumberedModels(IReadOnlyList<string> models, string? filter)
    {
        var shown = FilterModels(models, filter);
        // HashSet 查找：过滤后逐项 Contains 是 O(n*m)，模型列表上百时拖慢 /models
        var shownSet = new HashSet<string>(shown, StringComparer.OrdinalIgnoreCase);
        var result = new List<(int, string)>(shown.Count);
        for (int i = 0; i < models.Count; i++)
            if (shownSet.Contains(models[i]))
                result.Add((i + 1, models[i]));
        return result;
    }

    /// <summary>按关键字过滤模型列表（忽略大小写子串）；null/空 = 不过滤。</summary>
    internal static IReadOnlyList<string> FilterModels(IReadOnlyList<string> models, string? filter) =>
        string.IsNullOrWhiteSpace(filter) ? models : models.Where(m => m.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>列出当前 Provider 的可用模型，并用 * 标记当前配置的模型。</summary>
    private static async Task PrintModelsAsync(IAgentProvider provider, string? currentModel, string? filter = null)
    {
        try
        {
            var models = await provider.ListModelsAsync(CancellationToken.None);
            var rows = NumberedModels(models, filter);
            Console.WriteLine($"可用模型（{provider.Name}，共 {models.Count} 个，显示 {rows.Count} 条{(filter is null ? "" : $"，过滤 “{filter.Trim()}”")}）:");
            if (models.Count == 0)
            {
                // 成功响应但空列表：常见于 baseUrl 指错端点或 Key 无列表权限——给出可行动的提示
                Console.WriteLine("  （服务未返回任何模型：检查 baseUrl 是否指向支持 /models 的端点、API Key 是否有列表权限；仍可直接 /model <名称> 使用）");
                return;
            }
            var marked = false;
            foreach (var (num, m) in rows)
            {
                var numText = $"{num}) ";
                if (string.Equals(m, currentModel, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  {numText}{m}  *");
                    marked = true;
                }
                else
                {
                    Console.WriteLine($"  {numText}{m}");
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

    /// <summary>把文本写入系统剪贴板：Windows 用 clip.exe、macOS 用 pbcopy、Linux 用 xclip。
    /// 找不到工具或写入失败返回 false（调用方给出手动复制提示）。</summary>
    private static async Task<bool> TryCopyToClipboardAsync(string text)
    {
        try
        {
            System.Diagnostics.ProcessStartInfo psi = OperatingSystem.IsWindows()
                ? new("clip.exe")
                : OperatingSystem.IsMacOS() ? new("pbcopy") : new("xclip", "-selection clipboard");
            psi.UseShellExecute = false;
            psi.RedirectStandardInput = true;
            psi.CreateNoWindow = true;
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
                return false;
            await p.StandardInput.WriteAsync(text);
            await p.StandardInput.FlushAsync();
            p.StandardInput.Close();
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>git 仓库中存在 .codeagent/ 但 .gitignore 未忽略它时返回 true：
    /// 会话日志/导出含代码内容与可能的 Key 片段，误提交有泄露风险。</summary>
    internal static bool NeedsGitignoreHint(string cwd)
    {
        try
        {
            var git = Path.Combine(cwd, ".git");
            if (!Directory.Exists(git) && !File.Exists(git))
                return false; // 非 git 仓库不管
            if (!Directory.Exists(Path.Combine(cwd, ".codeagent")))
                return false;
            var gi = Path.Combine(cwd, ".gitignore");
            if (!File.Exists(gi))
                return true;
            foreach (var line in File.ReadAllLines(gi).Where(l => !l.TrimStart().StartsWith('#'))) // 注释行不算忽略
                if (line.Trim().TrimEnd('/').EndsWith(".codeagent", StringComparison.OrdinalIgnoreCase))
                    return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void PrintBanner(AgentConfig config, ProviderOptions opts, AgentClass agent)
    {
        try
        {
            // 终端标签页标题：多标签时快速区分项目/模型（不支持标题的终端静默忽略）
            Console.Title = $"CodeAgent · {agent.CurrentMode.Name} · {opts.Model}";
        }
        catch { /* 平台不支持：忽略 */ }
        Console.WriteLine("── CodeAgent ─────────────────────────────────────────────");
        Console.WriteLine($"  Version  : {InformationalVersion}");
        Console.WriteLine($"  Provider : {config.Provider} ({opts.Type})");
        Console.WriteLine($"  Model    : {opts.Model}");
        Console.WriteLine($"  Mode     : {agent.CurrentMode.Name}");
        Console.WriteLine($"  Thinking : {config.ThinkingEffort}{(config.ThinkingEffort == "auto" ? "（自动探测模型推理档位，状态栏显示实际生效值）" : "")}");
        Console.WriteLine($"  BaseUrl  : {opts.BaseUrl}");
        Console.WriteLine($"  Workspace: {Environment.CurrentDirectory}");
        var bannerBranch = GitInfo.CurrentBranch(Environment.CurrentDirectory);
        if (bannerBranch is not null)
            Console.WriteLine($"  Git      : {bannerBranch}");
        if (agent.SessionPath is not null)
            Console.WriteLine($"  会话日志  : {agent.SessionPath}");
        if (config.SourceFile is not null)
            Console.WriteLine($"  配置文件  : {config.SourceFile}");
        Console.WriteLine("  输入 /help 查看命令；直接输入任务描述即可开始。");
        Console.WriteLine("──────────────────────────────────────────────────────────");
    }
    /// <summary>一次性任务 + 管道输入：type bug.log | codeagent "分析" 的 stdin 内容附在任务后。
    /// stdin 为空（未管道）原样返回任务；超长截断避免撑爆上下文。</summary>
    internal static string ComposeTaskWithStdin(string task, string stdin)
    {
        if (string.IsNullOrWhiteSpace(stdin))
            return task;
        return task + "\n\n[stdin 输入]\n" + TextUtil.Truncate(stdin.TrimEnd(), 100_000);
    }
    /// <summary>有效上下文窗口：contextWindow 配置 > 内置模型表 > /models 元数据探测（仅对探测时模型有效）。
    /// 0 = 未知（显示层退回绝对值）。REPL 状态栏与 /stats 共用。</summary>
    internal static int EffectiveContextWindow(AgentConfig config, ProviderOptions opts, ContextProbeState? probe)
    {
        if (config.ContextWindow > 0)
            return config.ContextWindow;
        if (KnownContextWindows.TryGet(opts.Model) is { } fromTable)
            return fromTable;
        var t = probe?.Task;
        if (t?.IsCompletedSuccessfully == true && t.Result is { } fromApi
            && string.Equals(opts.Model, probe!.Model, StringComparison.OrdinalIgnoreCase))
            return fromApi;
        return 0;
    }

    /// <summary>后台上下文窗口探测状态：/model 换模型后旧结果作废并重启探测。</summary>
    internal sealed class ContextProbeState
    {
        public string? Model;
        public Task<int?>? Task;

        public void Restart(string model, IAgentProvider provider) =>
            (Model, Task) = (model, provider.GetContextWindowAsync(model, CancellationToken.None));
    }
    /// <summary>后台推理能力探测状态：/model 换模型后旧结果作废并重启探测。</summary>
    internal sealed class ReasoningProbeState
    {
        public string? Model;
        public Task<IReadOnlyList<string>?>? Task;

        public void Restart(string model, IAgentProvider provider) =>
            (Model, Task) = (model, provider.GetSupportedEffortsAsync(model, CancellationToken.None));
    }
    /// <summary>处理 REPL 斜杠命令。返回 true = 该命令已展示过状态信息，本轮跳过状态栏
    /// （模式/权限切换只需一行灰色确认，避免「消息 + 状态栏 + 提示符」三处重复模式名）。</summary>
    private static bool HandleCommand(
        string line,
        AgentConfig config,
        string? configPath,
        ref ProviderOptions opts,
        AgentClass agent,
        ref IAgentProvider providerInst,
        ToolRegistry tools,
        ContextProbeState? ctxProbe = null,
        ReasoningProbeState? reasoningProbe = null)
    {
        var (cmd, rest) = SplitCommand(line);
        var suppressStatusBar = false;

        switch (cmd)
        {
            case "/exit" or "/quit":
                Console.WriteLine("再见！");
                agent.Close(); // Flush and close the session log before exiting
                Environment.Exit(0);
                break;

            case "/clear":
                agent.Reset();
                Console.WriteLine("已清空对话历史。");
                break;

            case "/compact":
                // 用户主动压缩上下文：把最早的一部分对话交给 LLM 压缩成摘要（/clear 是彻底清空，本命令保留语义）
                // 走 RunTurnAsync：摘要请求支持 ESC/Ctrl+C 取消（此前 CancellationToken.None 卡住只能干等）
                {
                    string result;
                    try
                    {
                        result = RunTurnAsync(async t =>
                            await agent.CompactAsync(t, string.IsNullOrWhiteSpace(rest) ? null : rest.Trim()) ? "COMPACTED" : "SHORT").GetAwaiter().GetResult(); // HandleCommand 同步上下文（rest = 压缩保留重点）
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"⚠ 压缩失败: {ex.Message}");
                        break;
                    }
                    if (IsCancelledTurn(result))
                        Console.WriteLine("⏹ 已取消压缩（历史未变动）。");
                    else if (result == "SHORT")
                        Console.WriteLine("⚠ 当前对话过短，无需压缩。");
                    else
                        Console.WriteLine("✔ 历史已压缩。");
                }
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
                    if (next == "full" && !ConfirmFullAccess(Console.In, Console.Out))
                        break; // 用户取消：保持当前模式
                    agent.SetFileAccess(next);
                    PrintFileAccess(next);
                    PersistFileAccess(config); // 写回配置文件，重启后保持
                    suppressStatusBar = true; // 同模式切换：一行确认，跳过状态栏
                }
                else if (!string.IsNullOrWhiteSpace(rest))
                {
                    var mode = rest.Trim().ToLowerInvariant();
                    if (mode is "strict" or "whitelist" or "full")
                    {
                        if (mode == "full" && !ConfirmFullAccess(Console.In, Console.Out))
                            break; // 用户取消：保持当前模式
                        agent.SetFileAccess(mode);
                        PrintFileAccess(mode);
                        PersistFileAccess(config);
                        suppressStatusBar = true;
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

            case "/provider":
                // 切换供应商（无需重启）：-p 只在启动时生效，会话内曾只能改配置文件
                if (string.IsNullOrWhiteSpace(rest))
                {
                    Console.WriteLine($"当前 Provider: {config.Provider}（/provider <名> 切换，/providers 查看全部）");
                }
                else
                {
                    var wanted = rest.Trim();
                    var hit = config.Providers.FirstOrDefault(kv =>
                        kv.Key.Equals(wanted, StringComparison.OrdinalIgnoreCase));
                    if (hit.Key is null)
                    {
                        Console.WriteLine($"⚠ 没有供应商「{wanted}」，可用: {string.Join(", ", config.Providers.Keys)}");
                        break;
                    }
                    try
                    {
                        config.Provider = hit.Key;
                        opts = hit.Value;
                        providerInst = ProviderFactory.Create(config);
                        agent.SetProvider(providerInst);
                        ctxProbe?.Restart(opts.Model, providerInst);
                        reasoningProbe?.Restart(opts.Model, providerInst);
                        config.PersistedProvider = hit.Key; // 显式切换：用户明确选择，应持久化
                        var savePath = ConfigSavePath(configPath, config);
                        AgentConfig.Save(config, savePath);
                        try { Console.Title = $"CodeAgent · {agent.CurrentMode.Name} · {opts.Model}"; } catch { }
                        Console.WriteLine($"已切换 Provider: {hit.Key}，模型 {opts.Model}，已保存到 {savePath}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"切换失败: {ex.Message}");
                    }
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
                    // 模型列表在编号解析与拼写检查间共用：/model 5 原先对刚取出的名字二次请求模型接口
                    IReadOnlyList<string>? knownModels = null;
                    // 数字参数：从 /models 列表按编号选择（如 /model 5）
                    if (int.TryParse(modelArg, out var idx) && idx >= 1)
                    {
                        try
                        {
                            knownModels = providerInst.ListModelsAsync(CancellationToken.None).GetAwaiter().GetResult();
                            if (idx <= knownModels.Count)
                            {
                                modelArg = knownModels[idx - 1];
                            }
                            else
                            {
                                Console.WriteLine($"无效编号（可选 1-{knownModels.Count}，/models 查看）");
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"获取模型列表失败: {ex.Message}");
                            break;
                        }
                    }
                    // 拼写检查：名称不在模型列表时提示相近候选（不阻断——代理服务可能隐藏列表）
                    if (!int.TryParse(modelArg, out _))
                        try
                        {
                            knownModels ??= providerInst.ListModelsAsync(CancellationToken.None).GetAwaiter().GetResult(); // 编号选择时已取回，直接复用
                            if (knownModels.Count > 0 && !knownModels.Contains(modelArg, StringComparer.OrdinalIgnoreCase))
                            {
                                var near = SuggestModels(knownModels, modelArg);
                                Console.WriteLine($"⚠ 模型列表中没有「{modelArg}」（共 {knownModels.Count} 个模型）");
                                if (near.Count > 0)
                                    Console.WriteLine($"  相近的模型: {string.Join("、", near)}");
                                Console.WriteLine("  仍将按输入保存；/models [关键字] 可查列表");
                            }
                        }
                        catch { /* 离线/接口不支持时跳过检查 */ }
                    opts.Model = modelArg;
                    try
                    {
                        providerInst = ProviderFactory.Create(config);
                        agent.SetProvider(providerInst);
                        // 重新后台探测新模型的上下文窗口与推理能力（启动时的探测只对旧模型有效）
                        ctxProbe?.Restart(opts.Model, providerInst);
                        reasoningProbe?.Restart(opts.Model, providerInst);
                        // 同步回配置并持久化，重启后仍然生效
                        if (config.Providers.TryGetValue(config.Provider, out var po))
                            po.Model = opts.Model;
                        var savePath = ConfigSavePath(configPath, config);
                        SaveConfig(config, savePath);
                        Console.WriteLine($"已切换模型: {opts.Model}，已保存到 {savePath}");
                        try { Console.Title = $"CodeAgent · {agent.CurrentMode.Name} · {opts.Model}"; } catch { }
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
                // 与状态栏/\/stats 同口径：配置 > 内置表 > 后台探测（探测完成后 /config 能显示 API 元数据值）
                var effWin = EffectiveContextWindow(config, opts, ctxProbe);
                var ctxDesc = effWin > 0
                    ? config.ContextWindow > 0 ? $"{effWin:N0}（配置）"
                    : KnownContextWindows.TryGet(opts.Model) is { } known && known == effWin ? $"{known:N0}（按模型名自动识别）"
                    : $"{effWin:N0}（/models 元数据探测）"
                    : "未知（可配置 contextWindow）";
                Console.WriteLine($"MaxIter  : {config.MaxToolIterations}  MaxHistoryChars: {config.MaxHistoryChars}  ContextWindow: {ctxDesc}");
                Console.WriteLine($"Commands : {(config.AllowCommands ? "on" : "off")}  确认: {(config.ConfirmCommands ? "on" : "off")}   Shell: {config.Shell}   超时: {config.CommandTimeoutSeconds}s");
                Console.WriteLine($"工具日志 : {(config.ShowToolCalls ? "on" : "off")}   流式输出: {(config.StreamOutput ? "on" : "off")}   会话日志: {(config.SaveSessions ? $"on（保留 {config.MaxSessionLogs}）" : "off")}");
                Console.WriteLine($"界面     : Markdown 渲染 {(config.RenderMarkdown ? "on" : "off")}   菜单 {(config.TuiAnsi ? "ANSI 原地" : "滚动式")}   默认模式 {config.DefaultMode}");
                Console.WriteLine($"目录     : 会话 {config.SessionDir}   导出 {config.ExportDir}");
                var roDirs = config.ReadOnlyDirs.Count == 0 ? "" : $"  只读白名单: {string.Join(", ", config.ReadOnlyDirs)}";
                Console.WriteLine($"Access   : {config.FileAccess}{roDirs}");
                break;

            case "/session":
                Console.WriteLine(agent.SessionPath ?? "会话日志未启用（config.SaveSessions=false）。");
                {
                    var logs = RecentSessionLogs(config, int.MaxValue);
                    if (logs.Count > 0)
                        Console.WriteLine($"目录内共 {logs.Count} 个会话日志（保留上限 maxSessionLogs={config.MaxSessionLogs}，滚动新日志时自动清理最旧的；0 = 不清理）。");
                }
                break;

            case "/setup":
                try
                {
                    // 尊重 -c 指定的配置文件路径；未指定时写回实际加载的来源文件（可能是 ~/.codeagent/config.json）
                    SetupWizard.Run(config, Console.In, Console.Out, ConfigSavePath(configPath, config), testConnection: true);
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
                if (int.TryParse(rest.Trim(), out var diffN) && diffN >= 1)
                {
                    // /diff <N>：只看倒数第 N 条改动（1 = 最近），多条改动时免翻全量
                    var nthDiff = agent.Context.Undo.DiffAt(diffN);
                    if (nthDiff is null)
                        Console.WriteLine($"没有第 {diffN} 条改动记录（/undo list 查看现有条数）。");
                    else
                        PrintColoredDiff(nthDiff);
                }
                else
                {
                    var diffText = agent.Context.Undo.AllDiffs();
                    if (diffText is null)
                        Console.WriteLine("没有可显示的改动（先让 agent 修改过文件）。");
                    else
                        PrintColoredDiff(diffText); // 着色输出：+/绿、-/红、@@/青、标题/亮白、文件头/灰
                }
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
                        // 同名快照已存在：覆盖前确认（误覆盖旧快照不可恢复）
                        if (agent.SessionExists(rest.Trim()) &&
                            !ConfirmReplace(Console.In, Console.Out, $"会话「{rest.Trim()}」已存在，覆盖？"))
                        {
                            Console.WriteLine("已取消保存。");
                            break;
                        }
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
                    // 无参数：列出已保存的命名会话（按保存时间新→旧，附相对时间）
                    var sessions = SavedSessions(Path.Combine(Environment.CurrentDirectory, config.SessionDir));
                    if (sessions.Count == 0)
                    {
                        Console.WriteLine("没有已保存的会话（用 /save <会话名> 保存当前对话）。");
                    }
                    else
                    {
                        Console.WriteLine($"已保存的会话（{sessions.Count} 个，新 → 旧）:");
                        foreach (var (name, age) in sessions)
                            Console.WriteLine($"  {name}（{age}）");
                    }
                }
                else
                {
                    try
                    {
                        agent.LoadSession(rest.Trim());
                        Console.WriteLine($"✔ 已恢复会话: {rest.Trim()}");
                        PrintConversation(agent, 20); // 显示恢复的最近 20 条（全量打印长会话会刷屏）
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"加载失败: {ex.Message}");
                    }
                }
                break;

            case "/resume":
                // 恢复历史会话日志（每条消息自动落盘）；--continue 启动时自动恢复最近一次。
                // 列表排除当前会话自己的日志（ResumableLogs）：当前会话占 1 号会把用户想恢复的
                // 「上一次对话」挤到后面（恢复自己还会把日志再滚动复制一份）
                {
                    var logs = ResumableLogs(agent, config);
                    if (logs.Count == 0)
                    {
                        Console.WriteLine("没有可恢复的会话记录（先正常对话过一次，或检查 saveSessions 配置）。");
                        break;
                    }
                    if (int.TryParse(rest.Trim(), out var ridx) && ridx >= 1 && ridx <= logs.Count)
                    {
                        if (agent.LoadSessionLog(logs[ridx - 1]))
                        {
                            Console.WriteLine($"↩ 已恢复会话: {Path.GetFileName(logs[ridx - 1])}");
                            PrintConversation(agent, 20);
                        }
                        else
                            Console.WriteLine("⚠ 会话日志无法恢复（文件可能损坏）。");
                    }
                    else
                    {
                        // 数字越界时明确指出范围（静默回退到列表曾让人以为编号生效了）
                        if (int.TryParse(rest.Trim(), out _))
                            Console.WriteLine($"⚠ 编号超出范围（可用 1-{logs.Count}）。最近的会话:");
                        else
                            Console.WriteLine("最近的会话（输入 /resume <编号> 恢复，--continue 启动时自动恢复最近一次）:");
                        for (int i = 0; i < logs.Count; i++)
                        {
                            // 文件名只是时间戳：附上相对时间、首条用户消息预览与条数，才能认出哪个会话是哪段对话
                            var (preview, count, capped) = AgentClass.SessionLogSummary(logs[i]);
                            var label = Path.GetFileNameWithoutExtension(logs[i]);
                            var age = TextUtil.RelativeTime(File.GetLastWriteTimeUtc(logs[i]), DateTime.UtcNow);
                            var countText = capped ? $"≥{count} 条" : $"{count} 条"; // 封顶后是下限，不是精确值
                            Console.WriteLine(preview is null
                                ? $"  {i + 1}) {label}（{age}，{countText}）"
                                : $"  {i + 1}) {label} · {age} · {TextUtil.TruncateLine(preview, 50)}（{countText}）");
                        }
                    }
                    break;
                }

            case "/history":
                PrintConversation(agent, int.TryParse(rest.Trim(), out var histN) && histN >= 1 ? histN : null);
                break;

            case "/find":
                // 跨历史会话日志搜索（/resume 的同类来源，最新在前）：找「之前那段对话」用
                {
                    var kw = rest.Trim();
                    if (kw.Length == 0)
                    {
                        Console.WriteLine("用法: /find <关键字> —— 在历史会话日志里搜索内容（与 /resume 同源，最新在前）");
                        break;
                    }
                    var logs = ResumableLogs(agent, config);
                    if (logs.Count == 0)
                    {
                        Console.WriteLine("没有可搜索的会话记录（先正常对话过一次，或检查 saveSessions 配置）。");
                        break;
                    }
                    var printed = 0;
                    var moreAvailable = false;
                    foreach (var log in logs)
                    {
                        if (printed >= 5)
                        {
                            moreAvailable = true; // 还有未展示的日志：提示缩小关键字
                            break;
                        }
                        var hits = AgentClass.SearchSessionLog(log, kw);
                        if (hits.Count == 0)
                            continue;
                        var label = Path.GetFileNameWithoutExtension(log);
                        var age = TextUtil.RelativeTime(File.GetLastWriteTimeUtc(log), DateTime.UtcNow);
                        Console.WriteLine($"{label} · {age}（/resume 可恢复）:");
                        foreach (var (role, snippet) in hits)
                            Console.WriteLine($"  [{role}] {TextUtil.TruncateLine(snippet, 110)}");
                        printed++;
                    }
                    if (printed == 0)
                        Console.WriteLine($"历史会话中没有匹配「{kw}」的内容。");
                    else if (moreAvailable)
                        Console.WriteLine("…（仅显示前 5 个命中文件，更精确的关键字可减少噪音）");

                    // 命名快照（/save 的 .json）也纳入搜索：快照是用户显式保存的，命中价值高
                    var snapshotDir = Path.Combine(Environment.CurrentDirectory, config.SessionDir);
                    var snapshotPrinted = 0;
                    foreach (var (name, age) in SavedSessions(snapshotDir))
                    {
                        if (snapshotPrinted >= 3)
                            break;
                        var hits = AgentClass.SearchSnapshot(Path.Combine(snapshotDir, name + ".json"), kw);
                        if (hits.Count == 0)
                            continue;
                        Console.WriteLine($"快照 {name} · {age}（/load {name} 恢复）:");
                        foreach (var (role, snippet) in hits)
                            Console.WriteLine($"  [{role}] {TextUtil.TruncateLine(snippet, 110)}");
                        snapshotPrinted++;
                    }
                }
                break;

            case "/export":
                // /export            导出当前对话
                // /export <名>       导出命名快照（/save 保存的）；与编号撞名时快照优先
                //                    （编号随日志滚动漂移，快照名是用户起的名，不该被劫持）
                // /export <编号>     导出 /resume 列表中的历史会话日志（编号与 /resume 一致）
                try
                {
                    var arg = rest.Trim();
                    string file;
                    if (arg.Length > 0 && agent.SessionExists(arg))
                        file = agent.ExportMarkdown(arg);
                    else if (int.TryParse(arg, out var eidx) && eidx >= 1)
                    {
                        var logs = ResumableLogs(agent, config);
                        if (logs.Count == 0)
                        {
                            Console.WriteLine("⚠ 没有历史会话日志可导出（先正常对话过一次，或 /save <名> 后 /export <名>）。");
                            break;
                        }
                        if (eidx > logs.Count)
                        {
                            Console.WriteLine($"⚠ 编号超出范围（可用 1-{logs.Count}，/resume 查看列表）。");
                            break;
                        }
                        file = agent.ExportSessionLogMarkdown(logs[eidx - 1]);
                    }
                    else
                        file = agent.ExportMarkdown(arg.Length == 0 ? null : arg);
                    Console.WriteLine($"✔ 已导出: {file}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"导出失败: {ex.Message}");
                }
                break;

            case "/stats":
                {
                    // 单价优先取当前 provider 的配置，未配置回退全局（多 provider 切换时全局价曾算错费用）
                    var cost = TextUtil.UsdCost(agent.TotalInputTokens, agent.TotalOutputTokens,
                        opts.PricePerMillionInput > 0 ? opts.PricePerMillionInput : config.PricePerMillionInput,
                        opts.PricePerMillionOutput > 0 ? opts.PricePerMillionOutput : config.PricePerMillionOutput);
                    var win = EffectiveContextWindow(config, opts, ctxProbe);
                    var ctxText = win > 0
                        ? $"ctx {TextUtil.CompactTokenCount(agent.ContextTokens)}/{TextUtil.CompactTokenCount(win)} ({TextUtil.PercentOf(agent.ContextTokens, win)}%)"
                        : $"ctx {TextUtil.CompactTokenCount(agent.ContextTokens)}";
                    var avg = agent.ProviderCalls > 0
                        ? (agent.TotalInputTokens + agent.TotalOutputTokens) / agent.ProviderCalls
                        : 0;
                    Console.WriteLine(
                        $"会话统计: 模型 {opts.Model}，请求 {agent.ProviderCalls} 次，" +
                        $"输入 {agent.TotalInputTokens:N0} tokens，输出 {agent.TotalOutputTokens:N0} tokens" +
                        (agent.ProviderCalls > 0 ? $"（平均 {avg:N0}/次）" : "") +
                        (agent.TotalCachedTokens > 0 ? $"（其中缓存命中 {agent.TotalCachedTokens:N0}）" : "") +
                        $"，当前上下文 {ctxText}，会话时长 {TextUtil.FormatSessionTime(SessionStopwatch.Elapsed)}" +
                        (cost is { } c ? $"，累计费用 ≈${TextUtil.FormatCost(c)}" : ""));
                }
                break;

            case "/tools":
                var modeTools = agent.ToolsForMode();
                Console.WriteLine($"可用工具（当前模式: {agent.CurrentMode.Name}，共 {modeTools.Count} 个）:");
                foreach (var t in modeTools.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
                    Console.WriteLine($"  {t.Name} — {t.Description}");
                break;

            case "/providers":
                Console.WriteLine($"已配置的 Provider（当前: {config.Provider}，-p <名> 或改 provider 切换）:");
                foreach (var kv in config.Providers)
                {
                    var cur = string.Equals(kv.Key, config.Provider, StringComparison.OrdinalIgnoreCase) ? " ←" : "";
                    var price = kv.Value.PricePerMillionInput > 0
                        ? $"  单价: ${kv.Value.PricePerMillionInput:F2}/${kv.Value.PricePerMillionOutput:F2} per M"
                        : "";
                    Console.WriteLine($"  {kv.Key} ({kv.Value.Type}) 模型: {kv.Value.Model}  baseUrl: {kv.Value.BaseUrl}{price}{cur}");
                }
                break;

            case "/mode":
                if (string.IsNullOrWhiteSpace(rest))
                {
                    Console.WriteLine($"当前模式: {agent.CurrentMode.Name}");
                    Console.WriteLine(ModeListText(config, agent.CurrentMode.Name));
                    Console.WriteLine("（提示: 按 Alt+M 弹出模式菜单，Shift+Tab 快速切换下一个模式）");
                }
                else if (rest.Equals("next", StringComparison.OrdinalIgnoreCase))
                {
                    // /mode next：循环切换到下一个模式（Shift+Tab 快捷键映射到这里）。
                    // 只打一行灰色确认并跳过状态栏：Tab 连续切换时曾产出
                    // 「消息 + 状态栏 + 提示符」4 行、模式名重复 3 次的刷屏
                    var modes = Modes.Build(config);
                    var idx = modes.FindIndex(m => m.Name.Equals(agent.CurrentMode.Name, StringComparison.OrdinalIgnoreCase));
                    if (idx < 0)
                        idx = 0;
                    var next = modes[(idx + 1) % modes.Count];
                    agent.SetMode(next);
                    PrintModeSwitched(next, opts.Model);
                    suppressStatusBar = true;
                }
                else
                {
                    // 拼错的模式名曾静默回退到 code 并打印「已切换」——用户以为生效了。
                    // 明确报错 + 列出相近候选（与 /model 的拼写提示一致）
                    var wanted = rest.Trim();
                    var modes = Modes.Build(config);
                    var mode = modes.FirstOrDefault(m => m.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
                    if (mode is null)
                    {
                        Console.WriteLine($"⚠ 没有模式「{wanted}」，可用: {string.Join(", ", modes.Select(m => m.Name))}");
                        var near = modes.Where(m => m.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                                                    || wanted.Contains(m.Name, StringComparison.OrdinalIgnoreCase))
                                        .Select(m => m.Name).Take(3).ToList();
                        if (near.Count > 0)
                            Console.WriteLine($"  相近的模式: {string.Join("、", near)}");
                    }
                    else
                    {
                        agent.SetMode(mode);
                        PrintModeSwitched(mode, opts.Model);
                        suppressStatusBar = true;
                    }
                }
                break;

            case "/copy":
                {
                    // 复制最近一条助手回复到剪贴板（贴 issue/聊天不用手动选中滚动输出）
                    var lastReply = agent.Messages.LastOrDefault(m =>
                        m.Role == MessageRole.Assistant && !string.IsNullOrWhiteSpace(m.Content));
                    if (lastReply is null)
                    {
                        Console.WriteLine("没有可复制的助手回复。");
                        break;
                    }
                    var ok = TryCopyToClipboardAsync(lastReply.Content!).GetAwaiter().GetResult(); // HandleCommand 同步上下文
                    Console.WriteLine(ok
                        ? $"已复制最近一条回复（{TextUtil.TruncateLine(lastReply.Content!, 40)}…）到剪贴板。"
                        : "⚠ 无法访问剪贴板（需要 clip.exe / pbcopy / xclip）。");
                }
                break;

            case "/prompt":
                {
                    // 查看当前生效的系统提示（模式提示 + ADOFAI 注入等叠加后的实际值）
                    var p = agent.CurrentSystemPrompt;
                    var source = agent.CurrentMode.Name.Equals("code", StringComparison.OrdinalIgnoreCase)
                        ? config.SessionOnlySystemPrompt is not null
                            ? "code（含会话级运行时注入，如 ADOFAI 上下文）"
                            : "code（配置的 systemPrompt）"
                        : $"模式 {agent.CurrentMode.Name}";
                    Console.WriteLine($"系统提示（来源: {source}；{p.Length:N0} 字符，截断显示 2000）:");
                    Console.WriteLine(TextUtil.Truncate(p, 2000));
                }
                break;

            case "/files":
                {
                    // 本次会话修改过的文件（撤销栈口径，去重）：审查 agent 改动面
                    var paths = agent.Context.Undo.AllPaths();
                    if (paths.Count == 0)
                        Console.WriteLine("本次会话还没有修改过文件。");
                    else
                    {
                        Console.WriteLine($"本次会话修改过的文件（{paths.Count} 个，最近优先）:");
                        foreach (var p in paths)
                            Console.WriteLine($"  {agent.Context.Workspace.ToRelative(p).Replace((char)92, '/')}");
                    }
                }
                break;

            case "/diag":
                // 终端环境诊断：定位输入卡顿 / 菜单渲染问题
                Console.WriteLine("终端诊断:");
                Console.WriteLine($"  IsInputRedirected : {Console.IsInputRedirected}");
                Console.WriteLine($"  OutputEncoding    : {Console.OutputEncoding.WebName} (CP{W(() => (int)Console.OutputEncoding.CodePage)})");
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
                Console.WriteLine($"  IsOutputRedirected: {Console.IsOutputRedirected}");
                var wt = Environment.GetEnvironmentVariable("WT_SESSION");
                var term = Environment.GetEnvironmentVariable("TERM_PROGRAM");
                Console.WriteLine($"  Terminal          : {(wt is not null ? "Windows Terminal" : term is not null ? term : "未知（conhost 或其他）")}");
                // 工作区环境补充：git 分支与 .codeagent 目录占用（排查磁盘/隐私时有用）
                Console.WriteLine($"  Git branch        : {GitInfo.CurrentBranch(Environment.CurrentDirectory) ?? "(非 git 仓库)"}");
                try
                {
                    var caDir = Path.Combine(Environment.CurrentDirectory, ".codeagent");
                    if (Directory.Exists(caDir))
                    {
                        var bytes = Directory.EnumerateFiles(caDir, "*", SearchOption.AllDirectories)
                            .Sum(f => new FileInfo(f).Length);
                        Console.WriteLine($"  .codeagent 大小   : {bytes / 1024.0:F0} KB（会话日志/导出/历史，可整目录删除）");
                    }
                }
                catch { /* 统计失败不影响诊断输出 */ }
                break;

            case "/models":
                PrintModelsAsync(providerInst, opts.Model, rest).GetAwaiter().GetResult();
                break;

            case "/thinking":
                if (string.IsNullOrWhiteSpace(rest))
                {
                    Console.WriteLine($"思考强度: {config.ThinkingEffort}（可选: off / low / medium / high / auto）");
                    Console.WriteLine("auto: 自动探测模型支持的档位并取最高可用（供应商声明支持 high 就用 high，只支持 low 就用 low）");
                    if (config.ThinkingEffort == "auto" && reasoningProbe is not null)
                    {
                        var t = reasoningProbe.Task;
                        if (t?.IsCompletedSuccessfully == true && string.Equals(opts.Model, reasoningProbe.Model, StringComparison.OrdinalIgnoreCase))
                        {
                            var efforts = t.Result;
                            Console.WriteLine(efforts is { Count: > 0 }
                                ? $"当前模型 {opts.Model}: 支持推理参数（可用档位: {string.Join(" / ", efforts)}）→ auto 生效为 {efforts[^1]}"
                                : $"当前模型 {opts.Model}: 不支持/无法判断推理参数 → auto 生效为 off（不发送）");
                        }
                        else
                        {
                            Console.WriteLine($"当前模型 {opts.Model}: 探测中…（稍后重新运行 /thinking 查看结果）");
                        }
                    }
                }
                else
                {
                    var v = rest.Trim().ToLowerInvariant();
                    if (v is "off" or "low" or "medium" or "high" or "auto")
                    {
                        config.ThinkingEffort = v;
                        // 持久化到配置文件，重启后仍然生效
                        try
                        {
                            var savePath = ConfigSavePath(configPath, config);
                            SaveConfig(config, savePath);
                            Console.WriteLine($"思考强度已设为: {v}，已保存到 {savePath}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"思考强度已设为: {v}（保存配置失败: {ex.Message}）");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"无效值: {rest}（可选: off / low / medium / high / auto）");
                    }
                }
                break;


            case "/shell":
                // 命令 shell 运行时切换：run_command 工具每次执行时读 config.Shell（留空自动检测），改完下一跳命令即生效
                if (string.IsNullOrWhiteSpace(rest))
                {
                    var auto = ShellRunner.AutoShell();
                    var current = string.IsNullOrWhiteSpace(config.Shell)
                        ? $"auto（当前生效: {(auto.Length == 0 ? "bash" : auto)}）"
                        : config.Shell;
                    Console.WriteLine($"命令 shell: {current}（可选: cmd / powershell / pwsh / bash / sh / auto）");
                }
                else
                {
                    var v = rest.Trim().ToLowerInvariant();
                    if (v is "cmd" or "powershell" or "pwsh" or "bash" or "sh" or "auto")
                    {
                        config.Shell = v == "auto" ? "" : v;
                        // 持久化到配置文件，重启后仍然生效
                        try
                        {
                            var savePath = ConfigSavePath(configPath, config);
                            SaveConfig(config, savePath);
                            Console.WriteLine($"命令 shell 已设为: {v}，已保存到 {savePath}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"命令 shell 已设为: {v}（保存配置失败: {ex.Message}）");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"无效值: {rest}（可选: cmd / powershell / pwsh / bash / sh / auto）");
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
        return suppressStatusBar;
    }

    /// <summary>模式切换的灰色单行确认（Tab / /mode 用；状态栏本轮跳过，避免模式名重复三处）。</summary>
    private static void PrintModeSwitched(AgentMode mode, string model)
    {
        try { Console.Title = $"CodeAgent · {mode.Name} · {model}"; } catch { /* 部分终端不支持标题 */ }
        SafeColor.Foreground(ConsoleColor.DarkGray);
        Console.WriteLine($"已切换模式: {mode.Name} — {mode.Description}");
        SafeColor.Reset();
    }
    internal static (string cmd, string rest) SplitCommand(string line)
    {
        // 分隔符取最早出现者：空格、Tab、全角空格（CJK 输入法常打全角空格，曾导致 /model　gpt 无法识别）
        var idx = new[] { line.IndexOf(' '), line.IndexOf('\t'), line.IndexOf('　') }
            .Where(i => i >= 0)
            .DefaultIfEmpty(-1)
            .Min();
        // 命令名统一小写：/HELP、/Model 曾因大小写不匹配落入 default 分支被当成聊天消息发给模型；
        // rest 保持原样（模型名/会话名区分大小写）
        return idx < 0 ? (line.ToLowerInvariant(), "") : (line[..idx].ToLowerInvariant(), line[(idx + 1)..]);
    }

    /// <summary>模式列表文本（/mode 无参数用）：当前模式标 ←。</summary>
    internal static string ModeListText(AgentConfig config, string currentMode) =>
        string.Join("\n", Modes.Build(config).Select(m =>
            $"  {m.Name} — {m.Description}{(m.Name.Equals(currentMode, StringComparison.OrdinalIgnoreCase) ? "  ←" : "")}"));
    /// <summary>命令是否为模式/权限切换。必须与 HandleCommand 的切换分支保持一致
    /// （切换命令恰好输出一行确认并跳过状态栏，原地覆盖按「消息+空行+提示符」三行计算）。</summary>
    internal static bool IsSwitchCommand(string cmd, string rest) =>
        rest.Equals("next", StringComparison.OrdinalIgnoreCase) && cmd is "/mode" or "/access"
        || cmd == "/mode" && !string.IsNullOrWhiteSpace(rest)
        || cmd == "/access" && rest.Trim().ToLowerInvariant() is "strict" or "whitelist" or "full";

    private static void PrintReplHelp()
    {
        Console.WriteLine("""
            命令:
              /help            显示本帮助
              /clear           清空对话历史
              /compact [重点]   压缩对话历史为摘要（重点并入摘要指令；/clear 是彻底清空）
              /cls             清空屏幕（或按 Ctrl+L）
              /model [名称|编号] 查看或切换模型（编号按完整列表）
              /provider [名]   查看或切换供应商（无需重启）
              /copy            复制最近一条助手回复到剪贴板
              /prompt          查看当前生效的系统提示
              /files           列出本次会话修改过的文件
              /config          显示当前配置
              /session         显示会话日志路径
              /setup           运行交互式供应商配置向导
              /undo            撤销最近一次文件修改（write/edit）
              /diff [N]        显示最近一次修改的 diff（N = 倒数第 N 条）
              /save <名>       保存当前会话（命名快照）
              /load <名>       恢复已保存的会话
              /export [名/编号] 导出会话为 Markdown（同名快照优先；编号为 /resume 列表中的历史会话）
              /stats           显示 token 用量统计
              /retry           重新执行上一条请求
              /tools           列出可用工具
              /providers       显示已配置的 Provider
              /models [关键字]  列出/过滤模型（过滤时编号不变）
              /diag            显示终端环境诊断
              /history [N]      显示对话历史（N = 最近 N 条）
              /resume [编号]   恢复历史会话（--continue 启动时自动恢复最近一次）
              /find <关键字>    在历史会话日志中搜索内容
              /thinking        查看或设置思考强度（off/low/medium/high/auto）
              /shell [名称]     查看或切换命令 shell（cmd/powershell/pwsh/bash/sh，auto=自动检测）
              /mode [名称]     查看或切换工作模式（内置 8 种 + 自定义）
              /access [模式]   查看或切换文件访问权限（strict/whitelist/full，next 循环切换）
              /exit, /quit     退出
            用法:
              codeagent "帮我给项目写一个 README"  一次性任务（管道输入会附加到任务后：`type bug.log | codeagent "分析"`）
              codeagent                           进入交互模式
            参数:
              -c, --config <路径>  指定配置文件
              -p, --provider <名>  切换 Provider（配置中的键）
              -m, --model <模型>   覆盖模型名
              --cwd <目录>         切换工作目录
              --init               生成示例配置 codeagent.json
              --setup              交互式配置供应商并生成 codeagent.json
              --models             列出当前 Provider 的可用模型
              --continue           恢复本项目最近一次会话（会话自动落盘到 .codeagent/sessions）
              -v, --version        显示版本号
            快捷键:
              Esc                   撤回最近一轮对话（空输入时；连按逐轮回退）
              Tab                    切换下一个工作模式（/mode next）
              Shift+Tab              切换文件访问权限模式（strict→whitelist→full）
              Alt+M / Ctrl+Shift+M   模式切换菜单
              Alt+U / Ctrl+Shift+U   撤销最近一次文件修改（/undo）
              Alt+D / Ctrl+Shift+D   查看最近修改的 diff（/diff）
              Alt+N / Ctrl+Shift+N   新建会话（/clear）
              Ctrl+R                 反向搜索命令历史（再按跳更早命中）
              Ctrl+L                 清屏
            """);
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
            codeagent {InformationalVersion} — LLM 驱动的编码助手 CLI

            用法:
              codeagent [选项] ["任务描述"]     交互式 REPL（默认）；带任务则一次性执行后退出
              codeagent --continue ["接续任务"] 恢复本项目最近一次会话
              codeagent --resume <编号> ["任务"] 按编号恢复历史会话（编号见 /resume 列表）

            选项:
              -c, --config <路径>    指定配置文件
              -p, --provider <名称>  使用配置中的指定供应商
              -m, --model <模型>     覆盖本次使用的模型
              --cwd <目录>           切换工作目录（先于 --init/--setup 生效）
              --mode <名>            以指定工作模式启动（会话级覆盖 defaultMode，不写回配置；/mode 查看）
              --init                 在当前目录生成示例配置 codeagent.json
              --setup                交互式供应商配置向导（保存前自动测试连接）
              --models               列出当前供应商的可用模型
              --continue             恢复最近一次会话
              --no-session           本次运行不写会话日志（隐私敏感任务；不写回配置文件）
              -v, --version          显示版本
              -h, --help             显示本帮助

            """);
        PrintReplHelp();
    }
}
