using System.Reflection;
using System.Text;
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

    /// <summary>把控制台输出编码设为 UTF-8，让 `── ✓ ⚠ ⏵ …` 这些 UI 字符正确显示。
    ///
    /// 这一步**绝不能抛**：它发生在 Main 的第一行，抛出去就是「启动即崩」，
    /// 屏幕上什么都看不到、也没有任何提示。而设置编码在若干环境下确实会抛：
    /// 输出被重定向到已关闭的句柄、精简控制台宿主、部分 CI 的管道。
    /// 项目里其余的平台接触都有兜底——颜色（SafeColor）、窗口标题（try/catch）、
    /// 读取终端宽度（ResolveColumns 沿用上一次好值）——唯独这里此前是裸赋值。
    /// 设不上就用控制台默认编码继续跑：字形可能不对，但用户至少看得见界面。</summary>
    internal static void ApplyOutputEncoding()
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; }
        catch { /* 用控制台默认编码继续：字形可能不对，但必须看得见 */ }
    }

    private static async Task<int> Main(string[] args)
    {
        ApplyOutputEncoding();

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
                    case "--":
                        // End of flags marker: all remaining args are positional
                        for (i++; i < args.Length; i++)
                            positional.Add(args[i]);
                        break;
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
            WriteStartupNotice($"参数错误: {ex.Message}");
            PrintHelp();
            return 2;
        }

        // --cwd 先于 --init/--setup 生效：--init 生成的示例配置应写入目标目录
        if (cwd is not null)
        {
            if (!Directory.Exists(cwd))
            {
                WriteStartupNotice($"目录不存在: {cwd}");
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
            WriteStartupNotice($"配置加载失败: {ex.Message}");
            return 2;
        }

        // 配置非致命警告（未知配置项 / 枚举回退）：拼写错误此前被静默忽略，用户无从得知「配了不生效」
        foreach (var warning in config.Warnings)
        {
            using var scope = SafeColor.Scope(SafeColor.Warning);
            Console.WriteLine(FormatNoticeLine($"配置: {warning}", ConsoleColumns()));
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
                Console.WriteLine(FormatCancelLine("已取消配置向导。", ConsoleColumns()));
            }
            catch (Exception ex)
            {
                WriteStartupNotice($"配置向导失败: {ex.Message}");
                return 2;
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
            config.Provider = provider.Trim();
        else if (!string.IsNullOrWhiteSpace(envProvider))
            config.Provider = envProvider.Trim();

        // 拼错的 provider 名此前会静默落到空的 openai 配置，报错变成误导性的「缺 API Key」。
        // 在创建 Provider 前先校验名字，给出可用列表
        if (config.Providers.Count > 0 && !config.Providers.ContainsKey(config.Provider)) // 空配置走默认，不误伤
        {
            WriteStartupNotice($"未知 provider「{config.Provider}」（可用: {string.Join(", ", config.Providers.Keys)}）");
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
            WriteStartupNotice($"Provider 初始化失败: {ex.Message}");
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
                    Console.WriteLine($"{SafeColor.Glyphs.Warn} --resume 编号超出范围（可用 1-{logs.Count}）。");
            }
            else if (logs.Count > 0)
                target = logs[0];
            if (target is null)
                Console.WriteLine("没有可恢复的会话记录（先正常对话过一次，或检查 saveSessions 配置）。");
            else if (agent.LoadSessionLog(target))
                Console.WriteLine(FormatConfirmLine($"已恢复会话: {Path.GetFileName(target)}", ConsoleColumns(), "↩"));
            else
                Console.WriteLine($"{SafeColor.Glyphs.Warn} 会话日志无法恢复（文件可能损坏）。");
        }
        // 应用工作模式：--mode <名> 会话级覆盖（不落盘），否则用配置的 defaultMode（如 "debug"）
        var modeName = modeOverride ?? config.DefaultMode;
        var mode = Modes.Find(modeName, config);
        if (!string.Equals(mode.Name, modeName.Trim(), StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"{SafeColor.Glyphs.Warn} 未知模式「{modeName}」，已回退到 {mode.Name}（/mode 查看可用模式）。");
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
                    WriteStartupNotice("任务为空：请提供任务描述（codeagent \"任务\"），或直接运行 codeagent 进入交互模式。");
                    agent.Close();
                    return 2;
                }
                var result = await RunTurnAsync(t => agent.RunAsync(task, t));
                if (IsCancelledTurn(result))
                    result = "\n" + FormatCancelLine("已取消。", ConsoleColumns()); // 哨兵映射回显示文本（一次性模式没有草稿回填）
                PrintResult(result, agent.StreamedLastRun, prefixNewline: false, agent.StreamedOnLineBoundary);
                agent.Close();
                return agent.LastTurnFailed ? 1 : 0; // 空回复视为失败，非零退出码供脚本判断
            }
            catch (ProviderException ex)
            {
                WriteStartupNotice(ex.Message);
                agent.Close();
                return 1;
            }
            catch (Exception ex)
            {
                WriteStartupNotice($"发生错误: {ex.GetType().Name}: {ex.Message}");
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
        // 此时退回追加模式，避免错位覆盖（余量列吸收目录里的 CJK 双宽字符）
        bool PromptFitsOneRow() =>
            Program.PromptFitsOneRow(PromptFor(opts, agent).TrimStart('\n'), ConsoleColumns());

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
                    Console.WriteLine($"↻ 重试上一条请求: {TextUtil.TruncateLine(agent.LastPrompt, 60)}");
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
                    using var scope = SafeColor.Scope(SafeColor.Danger);
                    Console.WriteLine(FormatNoticeLine(result, ConsoleColumns()));
                }
                else
                {
                    PrintResult(result, agent.StreamedLastRun, prefixNewline: true, agent.StreamedOnLineBoundary);
                }
                PrintTurnSummary(agent, sw.Elapsed, opts);
                // 上下文占用监控：配置了 autoCompactPercent 达标自动压缩；否则 ≥90% 提示建议 /compact
                {
                    var win = EffectiveContextWindow();
                    if (win > 0 && agent.ContextTokens > 0)
                    {
                        var pct = TextUtil.PercentOf(agent.ContextTokens, win);
                        if (config.AutoCompactPercent > 0 && pct >= config.AutoCompactPercent && !agent.LastTurnFailed)
                        {
                            try
                            {
                                var r = RunTurnAsync(async t =>
                                    await agent.CompactAsync(t) ? "COMPACTED" : "SHORT").GetAwaiter().GetResult();
                                if (r == "COMPACTED")
                                    Console.WriteLine(FormatConfirmLine(
                                        $"上下文已达 {pct}%（autoCompactPercent={config.AutoCompactPercent}），已自动压缩历史。",
                                        ConsoleColumns()));
                            }
                            catch (Exception ex)
                            {
                                WriteNotice($"自动压缩失败: {ex.Message}");
                            }
                        }
                        else if (pct >= 90)
                            Console.WriteLine(FormatNoticeLine(
                                $"上下文已用 {pct}%（{TextUtil.CompactTokenCount(agent.ContextTokens)}/{TextUtil.CompactTokenCount(win)}）：建议 /compact 压缩历史，否则即将自动裁剪最旧对话。",
                                ConsoleColumns()));
                    }
                }
            }
            catch (ProviderException ex)
            {
                WriteNotice($"\n{ex.Message}");
            }
            catch (Exception ex)
            {
                WriteNotice($"\n发生错误: {ex.GetType().Name}: {ex.Message}");
            }
        }

        agent.Close();
        return 0;
    }

    internal static bool IsReadableNonEmptyFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.LinkTarget is null && fi.Length > 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
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
            if (new DirectoryInfo(dir).LinkTarget is not null)
                return [];
            // 按最后写入时间排序（同秒滚动的 -2/-3 后缀文件名字典序不可靠）
            var nameComparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            return Directory.GetFiles(dir, "*.jsonl")
                .Where(IsReadableNonEmptyFile)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ThenByDescending(Path.GetFileName, nameComparer)
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
    internal static List<string> ResumableLogs(AgentClass agent, AgentConfig config)
    {
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return RecentSessionLogs(config)
            .Where(p => !string.Equals(p, agent.SessionPath, pathComparison))
            .ToList();
    }

    internal static ProviderOptions EnsureSelectedProvider(AgentConfig config)
    {
        config.Providers ??= new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(config.Provider))
            config.Provider = "openai";
        var name = config.Provider.Trim();
        config.Provider = name;
        if (!config.Providers.TryGetValue(name, out var opts) || opts is null)
        {
            foreach (var pair in config.Providers)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    name = pair.Key;
                    opts = pair.Value;
                    break;
                }
            }
        }
        config.Provider = name;
        if (opts is null)
        {
            opts = new ProviderOptions();
            config.Providers[name] = opts;
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
        arg.Length > 1 && arg[0] == '-' && arg != "--" && !KnownFlags.Contains(arg);

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
        var historyWidth = ConsoleColumns();
        var rows = new List<(string Role, string Content, bool IsError)>(msgs.Count);
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
            if (m.Role == MessageRole.Assistant && !string.IsNullOrEmpty(m.ThinkingText))
            {
                // 思考轮标记：内容被折叠进历史时仍能看出该轮带推理
                var redactedNote = m.RedactedThinkingData is { Count: > 0 } ? $" +{m.RedactedThinkingData.Count} 加密块" : "";
                content = (content.Length > 0 ? content + " " : "") + $"[思考 {m.ThinkingText.Length} 字符{redactedNote}]";
            }
            rows.Add((role, content, m.IsError));
        }
        // 先算全部角色前缀的最宽值再统一渲染：「[用户]」与「[工具read_file]」宽度不同，
        // 逐行直接打印会让正文起始列逐行跳动，列表就彻底没法扫读了
        var tagWidth = HistoryTagWidth(rows.Select(r => (r.IsError ? $"[{r.Role}] {SafeColor.Glyphs.Error} " : $"[{r.Role}] ")));
        foreach (var (role, content, isError) in rows)
            Console.WriteLine(FormatHistoryLine(role, content, isError, historyWidth, tagWidth));
    }

    /// <summary>历史列表单条内容的目标显示列数（CJK/emoji 占 2 列）。</summary>
    internal const int HistoryContentColumns = 300;

    /// <summary>
    /// 极窄终端下的角色前缀：宁可把角色名缩成「[用…]」，也不能整段丢掉。
    /// 丢掉前缀后这一行就成了**无标签正文**，列表里再也分不清哪条是用户、哪条是助手——
    /// 逐行可扫读正是历史列表存在的理由。连「[x]」都放不下时才退化成单字符标记。
    /// </summary>
    internal static string FitRoleTag(string role, bool isError, int budget)
    {
        if (budget <= 0)
            return string.Empty;
        var mark = isError ? SafeColor.Glyphs.Error : string.Empty;
        var full = $"[{role}]{(mark.Length > 0 ? " " + mark : string.Empty)} ";
        if (TextUtil.DisplayWidth(full) <= budget)
            return full;
        if (budget <= 2)
            return isError ? SafeColor.Glyphs.Warn + " " : SafeColor.Glyphs.NarrowDot; // 连方括号都放不下
        // 预算 = '[' + 角色名 + ']' + 标记 + 分隔空格
        var room = budget - 2 - (mark.Length > 0 ? TextUtil.DisplayWidth(mark) + 1 : 0) - 1;
        if (room >= 1)
            return "[" + InputLine.FitToWidth(role, room) + "]" + (mark.Length > 0 ? " " + mark : string.Empty) + " ";
        return isError ? SafeColor.Glyphs.Warn + " " : SafeColor.Glyphs.NarrowDot;
    }

    /// <summary>
    /// 历史列表的一行：先把多行内容折叠成单行（工具结果常带换行，否则打乱逐条列表），
    /// 再按显示宽度截断，最后加角色前缀。
    /// 旧实现先按字符数截断再折叠换行：300 个汉字实际占 600 列，一行铺满整屏后角色前缀错位；
    /// 且截断发生在换行折叠之前，折叠后实际长度可能超出预算。
    /// maxColumns 是**整行**预算（含角色前缀与缩进）：此前写死 300 列，80 列终端上一行铺满整屏。
    /// tagWidth &gt; 0 时按显示宽度补齐角色前缀，让各行正文起始列一致——参差的起始列是列表最伤可扫读的地方。
    /// </summary>
    internal static string FormatHistoryLine(
        string role, string content, bool isError, int maxColumns = HistoryContentColumns, int tagWidth = 0)
    {
        const string indent = "  ";
        var single = content.Replace("\r", "").Replace("\n", " ⏎ ").Replace("\t", "    ");
        var tag = isError ? $"[{role}] {SafeColor.Glyphs.Error} " : $"[{role}] ";
        // 统一前缀宽度的上限：不能超过终端能给键列的宽度，否则补齐反而把行撑爆
        var keyBudget = maxColumns - TextUtil.DisplayWidth(indent) - 1;
        if (tagWidth > 0 && tagWidth <= keyBudget)
            tag = InputLine.PadToDisplayWidth(tag, tagWidth);
        else if (TextUtil.DisplayWidth(tag) > Math.Max(1, keyBudget))
            tag = InputLine.FitToWidth(tag, Math.Max(1, keyBudget));
        // 角色前缀本身超出预算（极窄终端）：换成可辨识的缩写前缀，而不是整段丢弃——
        // 丢掉前缀的列表无法区分用户/助手，等于把逐条列表退化成一段无标签正文
        if (TextUtil.DisplayWidth(tag) > keyBudget)
            tag = FitRoleTag(role, isError, keyBudget);
        // 预算扣掉缩进与前缀：正文才是可扫读的部分
        var budget = maxColumns - TextUtil.DisplayWidth(indent) - TextUtil.DisplayWidth(tag);
        var text = budget <= 0
            ? InputLine.FitToWidth(single, Math.Max(1, maxColumns - TextUtil.DisplayWidth(indent) - TextUtil.DisplayWidth(tag)))
            : InputLine.FitToWidth(single, budget);
        return indent + tag + text;
    }

    /// <summary>历史列表统一角色前缀宽度的计算（各行前缀取最长者，正文起始列因此一致）。</summary>
    internal static int HistoryTagWidth(IEnumerable<string> tags) =>
        tags.Select(t => TextUtil.DisplayWidth(t)).DefaultIfEmpty(0).Max();

    /// <summary>回合摘要的可丢段优先级：费用 → 缓存比例 → 思考耗时 → 本回合 token 明细。
    /// 固定段保留「✓ 完成 + 轮数 + 工具调用数 + 总耗时」——这四项是回合结论的核心。
    /// 可丢段丢完仍超宽时先**脱掉装饰外框**（纯装饰，省 13 列），最后才硬截。</summary>
    internal static string BuildTurnSummary(
        int rounds, int toolCalls, string elapsed, string tokenText,
        string thinkText, string cacheText, string costText, int width)
    {
        var showTokens = true;
        var showThink = thinkText.Length > 0;
        var showCache = cacheText.Length > 0;
        var showCost = costText.Length > 0;

        bool showFrame = true;
        var showCalls = true;
        var showElapsed = true;
        var showRounds = true;

        string Render()
        {
            // 每段自身可能带前导空格（" 思考 2.5s"），靠字符串相加分隔会在丢段后留下双空格，
            // 间隔忽宽忽窄。统一收集成列表再用单空格连接：丢任意一段都不会改变其余段的间距。
            var parts = new List<string>();
            if (showRounds)
                parts.Add($"{rounds} 轮");
            if (showCalls)
                parts.Add($"{toolCalls} 次工具调用");
            if (showElapsed)
                parts.Add(elapsed);
            if (showTokens && tokenText.Length > 0)
                parts.Add(tokenText.Trim());
            if (showThink && thinkText.Trim().Length > 0)
                parts.Add(thinkText.Trim());
            if (showCache && cacheText.Trim().Length > 0)
                parts.Add(cacheText.Trim());
            if (showCost && costText.Trim().Length > 0)
                parts.Add(costText.Trim());
            var body = string.Join(" ", parts);
            // 外框是装饰不是信息：窄屏下先脱框（省 13 列），好过硬截出
            // 「看起来是完整摘要的残串」——用户会以为工具次数/耗时就那么多。
            return showFrame ? "── ✓ 完成 " + body + " ──" : "✓ " + body;
        }

        var text = Render();
        var drops = new Action[]
        {
            () => showCost = false,
            () => showCache = false,
            () => showThink = false,
            () => showTokens = false,
        };
        for (var i = 0; width > 0 && TextUtil.DisplayWidth(text) > width && i < drops.Length; i++)
        {
            drops[i]();
            text = Render();
        }
        if (width <= 0 || TextUtil.DisplayWidth(text) <= width)
            return text;
        // 丢完可丢段仍超宽：脱掉装饰外框再来一次（外框一次省 13 列，优先于丢数字）
        if (width > 0 && TextUtil.DisplayWidth(text) > width && showFrame)
        {
            showFrame = false;
            text = Render();
        }
        // 连核心段都放不下时按语义再丢：工具次数 → 耗时 → 轮数。
        // 绝不让 FitToWidth 把 12.3s 切成「12.…」——半个数字看起来像个完整但不同的值，
        // 比干脆不显示更容易误导。丢到只剩「✓ 完成」后它几乎在任何终端都放得下。
        var coreDrops = new Action[]
        {
            () => showCalls = false,
            () => showElapsed = false,
            () => showRounds = false,
        };
        for (var i = 0; width > 0 && TextUtil.DisplayWidth(text) > width && i < coreDrops.Length; i++)
        {
            coreDrops[i]();
            text = Render();
        }
        return width <= 0 || TextUtil.DisplayWidth(text) <= width ? text : InputLine.FitToWidth(text, width);
    }

    /// <summary>回合结束后打印摘要行（轮数/工具/时长/思考/tokens/缓存比例）——灰色弱化视觉噪音。
    /// token 显示本回合用量（与状态栏、spinner 定格行同口径）；会话累计见 /stats。
    /// 配置了单价时附本回合费用估算（≈$x.xx）。窄终端下按优先级丢段，保证仍是一行。</summary>
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
        using var scope = SafeColor.Scope(SafeColor.Muted);
        Console.WriteLine(BuildTurnSummary(
            agent.TurnRounds, agent.TurnToolCalls, TextUtil.FormatElapsed(elapsed),
            $"{agent.TurnInputTokens:N0} in / {agent.TurnOutputTokens:N0} out tok",
            think, cache, costText, ConsoleColumns()));
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
            if (new DirectoryInfo(sessionDir).LinkTarget is not null)
                return [];
            var nameComparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            return Directory.GetFiles(sessionDir, "*.json")
                .Where(IsReadableNonEmptyFile)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ThenByDescending(Path.GetFileName, nameComparer)
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
            WriteNotice($"写入配置失败: {ex.Message}");
        }
    }

    /// <summary>放开沙箱（fileAccess=full）前的二次确认：工作区外可读写是高危操作。
    /// 已处于 full 时再次经过不重复询问。EOF/非 y 一律视为取消（安全默认）。</summary>
    internal static bool ConfirmFullAccess(TextReader input, TextWriter output)
    {
        output.Write($"{SafeColor.Glyphs.Warn} 即将完全放开文件沙箱（工作区外可读写，仅限信任场景）。确认? [y/N] ");
        var answer = input.ReadLine()?.Trim();
        if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
            return true;
        output.WriteLine(FormatCancelLine("已取消（保持当前模式）。", ConsoleColumns()));
        return false;
    }

    /// <summary>通用覆盖确认（/save 同名快照等）：只有明确 y 放行，EOF/其他输入取消（安全默认）。</summary>
    internal static bool ConfirmReplace(TextReader input, TextWriter output, string question)
    {
        output.Write($"{SafeColor.Glyphs.Warn} {question} [y/N] ");
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
        using var scope = SafeColor.Scope(SafeColor.Muted);
        Console.WriteLine(FormatConfirmLine(FormatAccessSwitchedLine(mode, desc), ConsoleColumns()));
        if (showHint)
            Console.WriteLine(FormatHintLine("Shift+Tab 或 /access next 循环切换; /access <strict|whitelist|full> 直接指定", ConsoleColumns()));
    }

    /// <summary>按 diff 行首标记着色输出：+ 绿 / - 红 / @@ 青 / == 标题亮白 / ---+++ 文件头灰。
    /// 窄终端下按显示宽度裁剪：CJK 路径与长行会撑破终端。</summary>
    internal static void PrintColoredDiff(string diff)
    {
        var width = ConsoleColumns();
        foreach (var line in DiffUtil.SplitLines(diff))
        {
            // 作用域而非 Foreground/Reset 成对：FormatDiffLine 抛异常时若漏掉 Reset，
            // 循环会继续，于是**后面每一行 diff 都染上上一行的颜色**，
            // 而用户根本不知道出过错。
            ConsoleColor? tint = null;
            if (line.StartsWith("== ", StringComparison.Ordinal))
                tint = SafeColor.Emphasis;         // 文件标题
            else if (line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("+++", StringComparison.Ordinal))
                tint = SafeColor.Muted;    // 文件头
            else if (line.StartsWith("@@", StringComparison.Ordinal))
                tint = SafeColor.Accent;        // hunk 头
            else if (line.StartsWith('+'))
                tint = SafeColor.Success;       // 新增
            else if (line.StartsWith('-'))
                tint = SafeColor.Danger;         // 删除
            using var scope = tint is { } t ? SafeColor.Scope(t) : null;
            Console.WriteLine(FormatDiffLine(line, width));
        }
    }

    /// <summary>diff 文件标题/文件头行：把中间/开头的路径按**尾部保留**缩短。
    /// 硬截尾部（`== src/very/long/Pa…`）会让人以为是另一个文件——路径最有信息量的是末段文件名。
    /// 形态不识别（既不是 `== p ==` 也不是 `--- p` / `+++ p`）时返回 null，交给调用方按普通行处理。</summary>
    internal static string? ShortenDiffPathLine(string line, int width)
    {
        // `== 路径 ==`（两端都有等号）
        if (line.StartsWith("== ", StringComparison.Ordinal) && line.EndsWith(" ==", StringComparison.Ordinal))
        {
            var path = line[3..^3];
            var budget = width - 7; // "== " + " ==" 共 7 列
            return budget >= 1 ? "== " + ShortenPath(path, budget) + " ==" : null;
        }
        // `--- 路径` / `+++ 路径`
        foreach (var marker in new[] { "--- ", "+++ " })
        {
            if (!line.StartsWith(marker, StringComparison.Ordinal))
                continue;
            var path = line[4..];
            var budget = width - 4;
            return budget >= 1 ? marker + ShortenPath(path, budget) : null;
        }
        return null;
    }

    /// <summary>diff 行的显示宽度裁剪。
    /// 三处特别处理：
    /// ①`@@` 头尾的 `@@ 上下文标题`（C# 里常是很长的方法签名）是装饰性的，
    ///   放不下时整段丢掉，**保住行号范围**——行号才是 hunk 的结构信息；
    /// ②文件标题/文件头行按**尾部保留**缩短路径：硬截尾部会让人误以为是另一个文件；
    /// ③`+`/`-` 内容行必须带 `…` 标记：被截掉的 +行看起来就是"新增了这么多"，
    ///   用户会以为 diff 本来就这么短。
    /// ①再窄一档时按 <c>@@ -a,b +c,d @@</c> → <c>@@ -a,b +c,d</c> → <c>@@</c> 逐级退让，
    /// 始终保住 hunk 标记：一旦被裁成 `-12,7…`，行号范围就整个丢了，
    /// 而行号正是这里唯一不可替代的信息。</summary>
    internal static string FormatDiffLine(string line, int width)
    {
        if (width <= 0 || TextUtil.DisplayWidth(line) <= width)
            return line;
        if (line.StartsWith("@@", StringComparison.Ordinal))
        {
            var end = line.LastIndexOf("@@", StringComparison.Ordinal);
            if (end > 2)
            {
                var bare = line[..(end + 2)];
                if (TextUtil.DisplayWidth(bare) <= width)
                    return bare;
                // 省掉收尾的 " @@"：hunk 头以 @@ 开头已经足够识别。
                // 必须 TrimEnd：`line[..end]` 末尾那个空格会留下来，
                // 变成以空格结尾的一行——复制 diff 时会带进无意义的空白。
                var withoutTail = line[..end].TrimEnd();
                if (TextUtil.DisplayWidth(withoutTail) <= width)
                    return withoutTail;
                // 只剩标记本身才占得下时，宁可只留 @@ 也不裁行号
                if (width >= 2)
                    return "@@";
            }
        }
        if (ShortenDiffPathLine(line, width) is { } shortened)
            return shortened;
        return InputLine.FitToWidth(line, width);
    }

    /// <summary>深路径显示截断：超长时保留尾部（工作区名永远可见），前缀省略号。
    /// max 是**显示列数**：按字符数算会让中文路径实际占两倍宽（42 个汉字 = 84 列），
    /// 正是这个函数要防的溢出。尾部按码点回退，保证不劈开代理对。</summary>
    internal static string TruncatePathHead(string path, int max = 42)
    {
        if (max <= 0)
            return string.Empty;
        if (TextUtil.DisplayWidth(path) <= max)
            return path;
        var keep = max - 1; // 1 列给省略号
        if (keep <= 0)
            return "…";
        var end = path.Length;
        var used = 0;
        while (end > 0)
        {
            // 低代理项必须连高代理项一起取，否则会截出半个码点
            var step = char.IsLowSurrogate(path[end - 1]) && end >= 2 && char.IsHighSurrogate(path[end - 2]) ? 2 : 1;
            var w = TextUtil.DisplayWidth(path.Substring(end - step, step));
            if (used + w > keep)
                break;
            used += w;
            end -= step;
        }
        if (end >= path.Length)
            return "…";
        return "…" + path[end..];
    }

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

    /// <summary>提示符文本：完整形态为 `[模式|模型] 目录&gt; `。
    /// 超宽时按固定优先级降级：先去掉模型（模式比模型更该一眼看到），再截短目录名，
    /// **模式永不丢弃**——没有模式的提示符没有意义。width &lt;= 0 时不猜测，保持完整形态。
    /// </summary>
    internal static string BuildPromptText(string mode, string model, string dir, int width)
    {
        var full = $"[{mode}|{model}] {dir}> ";
        if (width <= 0)
            return full;
        // 正常宽度下为后续输入留出余量，这样 PromptFitsOneRow 仍能原地覆盖切换块；
        // 极窄宽度下余量会把模式挤没（[模式] 本身就双宽），此时只保证不超宽——
        // 此时 PromptFitsOneRow 会诚实地返回 false，切换块改走追加模式。
        var budget = width - PromptFitMargin;
        if (budget < 4)
            budget = width;
        if (TextUtil.DisplayWidth(full) <= budget)
            return full;
        var noModel = $"[{mode}] {dir}> ";
        if (TextUtil.DisplayWidth(noModel) <= budget)
            return noModel;
        var head = $"[{mode}]";
        // "[模式] 目录…> " 里目录可用宽度：扣掉 "[" "]" "…> " 四段
        var room = budget - TextUtil.DisplayWidth(head) - 4;
        if (room > 0)
            return $"{head} {InputLine.FitToWidth(dir, room)}…> ";
        // 模式名本身（CJK 双宽）就超宽：截短模式但**绝不去掉**——没有模式的提示符没有意义
        var modeRoom = Math.Max(1, budget - 6); // "[模式…]> " 的固定开销
        return "[" + InputLine.FitToWidth(mode, modeRoom) + "]…> ";
    }

    /// <summary>提示符单行判定必须预留的余量列数（吸收目录名/模型短名里的 CJK 双宽字符与终端边框）。</summary>
    internal const int PromptFitMargin = 8;

    /// <summary>
    /// 提示符能否单行放得下（决定切换块能否原地覆盖，而不是追加一行）。
    /// 按显示宽度而非字符数判断：中文模式名按 1 字符计却占 2 列，用 char 数会误判为放得下，
    /// 折行后 \x1b[3A 回到的「块顶」算错，切换确认覆盖到历史输出上。
    /// 宽度未知（0）返回 false：原地覆盖依赖确定的行几何，追加模式才是安全方向。
    /// </summary>
    internal static bool PromptFitsOneRow(string prompt, int windowWidth, int margin = PromptFitMargin) =>
        windowWidth > 0 && TextUtil.DisplayWidth(prompt) + margin <= windowWidth;

    /// <summary>
    /// 确认行（模式/权限/模型/provider 切换成功）：`✔ 正文`，与工具结果的 `✔` 约定一致。
    /// 与告警行共用同一宽度预算——切换确认常附带完整保存路径（可很长），
    /// 折行后 `✔` 会被留在上一行，扫读时看不出「切换到底成功没有」。
    /// </summary>
    internal static string FormatConfirmLine(string body, int width = 0, string marker = "✔") =>
        FormatNoticeLine(body, width, marker);

    /// <summary>取消提示行：`⏹ 正文`。
    /// 与 ✔（成功）、⚠（错误）构成三类可一眼分辨的结局标记。
    /// 此前取消提示散落各处且形态不一（有的带 ⏹、有的什么都不带），
    /// 用户扫读时无法用统一规则找出「这一轮发生了什么」。</summary>
    internal static string FormatCancelLine(string body, int width = 0) => FormatNoticeLine(body, width, "⏹");

    /// <summary>启动期错误/警告行（写入 stderr）：`⚠ 正文`，与交互期的告警行同一格式。
    /// 启动错误常带完整路径与异常消息，宽度未知时不做猜测性折行，但已知宽度下必须不溢出。</summary>
    private static void WriteStartupNotice(string body) =>
        Console.Error.WriteLine(FormatNoticeLine(body, ConsoleColumns()));

    /// <summary>操作提示行（解释"接下来能输入什么"）：两空格缩进 + 按显示宽度裁剪。
    /// 提示行此前都是裸字符串，`（服务未返回任何模型：检查 baseUrl 是否指向支持 /models 的端点…）`
    /// 这类长中文在 40 列终端上会硬折行，续行与上一条提示失去关联。</summary>
    internal static string FormatHintLine(string body, int width = 0) =>
        FormatResultLine("  " + body, width);

    /// <summary>查询词在结果行里能占的列数上限（占整行的比例）。
    /// 用户输入的长度不受控，`/find 「很长很长…」` 曾经把整行撑爆并把查询词切掉一半——
    /// 用户看到半截查询根本没法确认自己搜的是什么。</summary>
    internal const double QueryColumnMaxRatio = 0.5;

    /// <summary>把「前缀 + 用户查询词 + 后缀」压进整行预算：查询词**优先保头**（用户记得住开头），
    /// 超长时补 `…` 明确标记截断，绝不产出「看起来是完整查询」的残串；
    /// 预算不足以放下任何查询内容时，返回 null 交给调用方按普通行处理。</summary>
    internal static string? FitQueryLine(string before, string keyword, string after, int width)
    {
        if (width <= 0 || keyword.Length == 0)
            return null;
        var head = TextUtil.DisplayWidth(before);
        var tail = TextUtil.DisplayWidth(after);
        var room = width - head - tail;
        if (room < 3)
            return null;
        var cap = Math.Max(3, (int)(width * QueryColumnMaxRatio));
        var budget = Math.Min(room, cap);
        var fitted = InputLine.FitToWidth(keyword, budget);
        return TextUtil.DisplayWidth(fitted) < TextUtil.DisplayWidth(keyword) || fitted.Length == keyword.Length
            ? before + fitted + after
            : null;
    }

    /// <summary>「某设置已保存」确认行：`<动作>: <值>，已保存到 <路径>`。
    /// **值是主体**（用户刚改了什么），保存路径是补充：模型名/provider 名可以很长
    /// （openai/gpt-4.1-2025-04-14），此前整行硬截尾部会把**值本身**切掉，
    /// 用户看不到自己切到了什么。降级顺序：先丢保存从句，再按尾部保留缩短路径。
    /// width &lt;= 0 表示宽度未知：原样拼接。</summary>
    internal static string FormatSettingSavedLine(string action, string value, string savePath, int width = 0)
    {
        var head = $"{action}: {value}";
        if (width <= 0)
            return $"{head}，已保存到 {savePath}";
        var clause = "，已保存到 ";
        var room = width - TextUtil.DisplayWidth(head) - TextUtil.DisplayWidth(clause);
        if (room < MinSavedPathWidth)
        {
            // 路径一行都放不下：整段丢掉保存从句，只留「动作: 值」
            if (TextUtil.DisplayWidth(head) > width)
                return InputLine.FitToWidth(head, width);
            return head;
        }
        return head + clause + ShortenPath(savePath, room);
    }

    /// <summary>保存路径从句至少要有的列数。低于此值半个路径（`C:\Us…`）
    /// 既看不出存到哪儿，又不如不显示。</summary>
    internal const int MinSavedPathWidth = 6;

    /// <summary>普通结果/说明行（无标记）：按显示宽度裁剪。
    /// /find 这类输出会把**用户输入的关键字**与快照名直接拼进行里，
    /// 二者长度不受控，没有宽度预算就必然溢出。</summary>
    internal static string FormatResultLine(string body, int width = 0) =>
        width <= 0 ? body : InputLine.FitToWidth(body, width);

    /// <summary>供其他类复用的终端列数（0 = 未知）。</summary>
    internal static int ConsoleColumnsForNotice() => ConsoleColumns();
    /// <summary>当前终端列数（未知时为 0）。供非 Program 类型（如工具预览）复用，
    /// 免得各处复制一份「怎么读终端宽度」，读到不一致的值。</summary>
    internal static int Columns() => ConsoleColumns();

    /// <summary>正文末尾若是路径，就按**尾部保留**缩短那一段（其余文字原样保留）。
    /// 确认/告警行常以「已保存到 C:\Users\…\会话.json」「已导出: out/report.md」结尾——
    /// 路径**就是**这句话的重点，硬截尾部等于把「存到哪儿了」整个切掉，
    /// 留下一句没有落点的「已保存到 C:\…」。宽度不够放下任何路径内容时返回 null。</summary>
    internal static string? ShortenTrailingPath(string body, int budget)
    {
        if (budget <= 0 || body.Length == 0)
            return null;
        var parts = body.Split(' ');
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            var token = parts[i];
            if (token.Length < 6 || (!token.Contains('/') && !token.Contains('\\')))
                continue;
            var headWidth = i > 0 ? TextUtil.DisplayWidth(string.Join(" ", parts.Take(i))) + 1 : 0;
            var pathBudget = budget - headWidth;
            if (pathBudget < 6)
                return null; // 放不下任何有意义的路径内容：交给普通截断，别造出假路径
            var shortened = ShortenPath(token, pathBudget);
            if (TextUtil.DisplayWidth(shortened) >= TextUtil.DisplayWidth(token))
                return null; // 本来就放得下
            var rebuilt = new List<string>(parts.Take(i)) { shortened };
            for (var j = i + 1; j < parts.Length; j++)
                rebuilt.Add(parts[j]);
            return string.Join(" ", rebuilt);
        }
        return null;
    }

    /// <summary>
    /// 告警/错误行统一格式：`⚠ 正文`，窄终端按显示宽度截断**正文**而非整行——
    /// 标记必须始终留在行首可见：整行截断会让 ⚠ 孤零零留在上一行，扫读时反而找不到告警。
    /// 正文末尾是路径时先按尾部保留缩短路径（见 <see cref="ShortenTrailingPath"/>）。
    /// width &lt;= 0 表示宽度未知，不截断。
    /// </summary>
    internal static string FormatNoticeLine(string body, int width = 0, string? marker = null)
    {
        var mark = marker ?? SafeColor.Glyphs.Warn;
        if (width <= 0)
            return $"{mark} {body}";
        var budget = width - TextUtil.DisplayWidth(mark) - 1;
        if (budget <= 0)
            return mark;
        return $"{mark} {ShortenTrailingPath(body, budget) ?? InputLine.FitToWidth(body, budget)}";
    }

    /// <summary>所有「回显用户输入或异常消息」的通知行的**唯一出口**。
    /// 这些行的正文长度不受控：<c>rest</c> 是用户敲的任意文本、<c>ex.Message</c>
    /// 可能带整条文件路径或嵌套异常。直接 <c>Console.WriteLine($"…")</c> 就没有宽度约束，
    /// 窄终端上会硬折行、把后面的提示符顶走。走这里统一收口。</summary>
    internal static void WriteNotice(string body, string? marker = null) =>
        Console.WriteLine(FormatNoticeLine(body, ConsoleColumns(), marker));

    /// <summary>实测宽度的可信下限：低于此值视为测量失效（终端最小化、拖拽过程中的抖动、
    /// 某些终端在重设尺寸时短暂返回 1~2 列）。此时不能拿它去裁剪——否则每一行
    /// 都会被截成只剩一个省略号，比不裁剪糟得多。</summary>
    internal const int MinPlausibleColumns = 8;

    /// <summary>由「实测宽度」与「上一次的好值」得出本轮应使用的宽度。
    /// 读不到（0/异常）时**沿用上一次的好值**，而不是退化成 0——
    /// 宽度 0 的含义是「未知、不截断」，一次测量失败就让后续所有输出无限宽溢出，
    /// 正是整套宽度预算要防的事。</summary>
    internal static int ResolveColumns(int measured, int lastGood)
    {
        if (measured >= MinPlausibleColumns)
            return Math.Clamp(measured, 0, 300);
        return lastGood > 0 ? lastGood : measured;
    }

    /// <summary>读取终端宽度：0 = 未知（输出重定向或读取失败）。
    /// 读取失败时沿用上一次成功读到的值，避免单次抖动打回"不截断"。</summary>
    private static int ConsoleColumns()
    {
        if (Console.IsOutputRedirected)
            return 0;
        try
        {
            var width = Console.WindowWidth;
            var resolved = ResolveColumns(width, _lastGoodColumns);
            if (resolved >= MinPlausibleColumns)
                _lastGoodColumns = resolved;
            return resolved;
        }
        catch
        {
            return _lastGoodColumns;
        }
    }

    private static int _lastGoodColumns; // 上一次成功读到的可信宽度（跨调用保留）

    /// <summary>
    /// 组装状态栏并在窄终端下按优先级丢段，保证输出永远不超过 <paramref name="width"/> 列。
    /// 固定段：⏵ 模式 · 模型 · 目录；可丢段（由低到高）：思考强度 → 本回合 token → 上下文 → git 分支。
    /// 全部丢完仍超宽时按显示宽度硬截断（CJK/emoji 占 2 列）。
    /// </summary>
    /// <summary>路径按显示宽度缩短：保留**末尾**分段（路径最有信息量的是末段），
    /// 折叠的头部用 …\ 标注。直接按显示宽度硬截会留下「看起来是完整路径的残串」，
    /// 用户会以为那就是真实目录——这比截断本身更危险。</summary>
    internal static string ShortenPath(string path, int budget)
    {
        if (budget <= 0 || TextUtil.DisplayWidth(path) <= budget)
            return path;
        var separator = path.Contains('\\', StringComparison.Ordinal) && !path.Contains('/', StringComparison.Ordinal) ? '\\' : '/';
        var ellipsis = "…" + separator;
        var segments = path.Split(separator);
        var tail = new List<string>();
        var used = TextUtil.DisplayWidth(ellipsis);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var segment = segments[i];
            if (segment.Length == 0)
                continue;
            var w = TextUtil.DisplayWidth(segment) + (tail.Count > 0 ? 1 : 0);
            if (used + w > budget)
                break;
            tail.Insert(0, segment);
            used += w;
        }
        if (tail.Count > 0)
            return ellipsis + string.Join(separator, tail);
        // 连最后一段都放不下：截掉最后一段的尾部并加 … 前缀，绝不返回无标记的残串
        if (budget <= 1)
            return "…";
        var last = segments.Length > 0 ? segments[^1] : path;
        return "…" + InputLine.FitToWidth(last, budget - 1); // 1 列留给前缀 …
    }

    /// <summary>
    /// 状态栏分段降级顺序。优先级由「现在最需要知道什么」决定：
    /// 模式/模型在行首（永不丢）→ ctx 百分比（离上限还有多远，最该盯）→ 本回合 token（花了多少）
    /// → 目录（自带缩_shortening阶梯_，其次）→ 分支/思考档（冷门设置）。
    /// 此前把 ctx 排在目录缩短**之前**丢，于是长路径场景下最有操作价值的
    /// 「上下文用了多少」先消失，剩下一堆静态信息。
    /// </summary>
    /// <summary>状态栏最窄档仍要显示目录时，目录至少要有的列数。
    /// 低于此值与其显示半截路径，不如整个不显示——半截路径会指向不存在的目录。</summary>
    internal const int MinStatusPathWidth = 6;

    internal static string BuildStatusBar(
        string mode, string model, string cwd, string? branch,
        string turnIn, string turnOut, string ctxText, string thinkText, int width)
    {
        bool showBranch = !string.IsNullOrEmpty(branch);
        var showTurn = true;
        var showCtx = true;
        var showThink = !string.IsNullOrEmpty(thinkText);
        var shownPath = string.Empty;

        string Render()
        {
            // 目录与分支合成一段：与旧版「cwd (branch)」一致，窄屏省 3 列
            var path = shownPath.Length > 0 ? shownPath : cwd;
            var location = showBranch ? $"{path} ({branch})" : path;
            var parts = new List<string> { $"{SafeColor.Glyphs.StatusMark} {mode}{SafeColor.Glyphs.SegmentSeparator}{model}", location };
            if (showTurn) parts.Add($"{turnIn} in / {turnOut} out");
            if (showCtx) parts.Add(ctxText);
            if (showThink) parts.Add(thinkText);
            return string.Join(SafeColor.Glyphs.SegmentSeparator, parts);
        }

        var text = Render();
        // 第一轮：丢冷门段（思考档 → 分支 → 本回合 token），ctx 保留到最后
        var drops = new Action[] { () => showThink = false, () => showBranch = false, () => showTurn = false };
        for (var i = 0; width > 0 && TextUtil.DisplayWidth(text) > width && i < drops.Length; i++)
        {
            drops[i]();
            text = Render();
        }
        if (width <= 0 || TextUtil.DisplayWidth(text) <= width)
            return text;
        // 第二轮：缩短目录。必须走 Render 重画整条状态栏——直接拼 head+路径
        // 会把仍然保留的 ctx/token 段一起丢掉（缩短路径是换内容，不是减段）。
        // 路径预算要扣掉**仍保留的其他段**，否则缩完还是超宽，ctx 白白被牺牲。
        // 路径预算要扣掉**仍保留的其他段**以及它们各自前面的 " · " 分隔符，
        // 否则缩完还是超宽，ctx 白白被牺牲。（head 已含路径前的分隔符，不再重复扣。）
        var head = $"{SafeColor.Glyphs.StatusMark} {mode}{SafeColor.Glyphs.SegmentSeparator}{model}{SafeColor.Glyphs.SegmentSeparator}";
        var others = new List<string>();
        if (showTurn) others.Add($"{turnIn} in / {turnOut} out");
        if (showCtx) others.Add(ctxText);
        if (showThink) others.Add(thinkText);
        var othersWidth = others.Sum(TextUtil.DisplayWidth) + others.Count * 3;
        var pathBudget = width - TextUtil.DisplayWidth(head) - othersWidth;
        if (pathBudget > 0)
        {
            var location = showBranch ? $"{cwd} ({branch})" : cwd;
            var shortened = ShortenPath(location, pathBudget);
            if (TextUtil.DisplayWidth(shortened) < TextUtil.DisplayWidth(location))
            {
                shownPath = ShortenPath(cwd, pathBudget);
                text = Render();
                if (TextUtil.DisplayWidth(text) <= width)
                    return text;
                shownPath = string.Empty; // 缩短后仍超宽：回退，交给下一轮
            }
        }
        // 第三轮：连目录都缩不动了才丢 ctx（上下文用量是最后才牺牲的信息）
        if (showCtx)
        {
            showCtx = false;
            text = Render();
            if (width <= 0 || TextUtil.DisplayWidth(text) <= width)
                return text;
        }
        // 第四轮：其余段全丢光后仍超宽——再按**保尾部**缩短一次目录。
        // 此前直接 FitToWidth 截尾部，得到「⏵ plan · gpt-5 · src/CodeAg…」：
        // 路径最有信息量的是末段，硬截恰好把它切掉，看起来像另一个目录
        // （与 diff 文件头同一个病，见 ShortenPath）。放不下末段就整个去掉目录，
        // 也不留半截路径。
        if (width > 0 && shownPath.Length == 0)
        {
            var minimalHead = $"{SafeColor.Glyphs.StatusMark} {mode}{SafeColor.Glyphs.SegmentSeparator}{model}{SafeColor.Glyphs.SegmentSeparator}";
            var minimalBudget = width - TextUtil.DisplayWidth(minimalHead);
            if (minimalBudget >= MinStatusPathWidth)
            {
                shownPath = ShortenPath(cwd, minimalBudget);
                text = Render();
                if (TextUtil.DisplayWidth(text) <= width)
                    return text;
                shownPath = string.Empty; // 缩短后仍超宽：回退，交给硬截兜底
            }
        }
        return width <= 0 || TextUtil.DisplayWidth(text) <= width ? text : InputLine.FitToWidth(text, width);
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
            think = done ? $"think:auto→{(efforts is { Count: > 0 } ? efforts[^1] : "off")}" : "think:auto";
        }
        else
        {
            think = thinkingEffort != "off" ? $"think:{thinkingEffort}" : "";
        }
        var ctx = contextWindow > 0
            ? $"ctx {TextUtil.CompactTokenCount(agent.ContextTokens)}/{TextUtil.CompactTokenCount(contextWindow)} ({TextUtil.PercentOf(agent.ContextTokens, contextWindow)}%)"
            : $"ctx {TextUtil.CompactTokenCount(agent.ContextTokens)}";
        var shownCwd = TruncatePathHead(Environment.CurrentDirectory);
        // git 分支段（非仓库整体省略）：多仓库/多分支工作流下快速确认当前所在位置
        var branch = CachedBranch(Environment.CurrentDirectory);
        using var scope = SafeColor.Scope(SafeColor.Muted);
        Console.WriteLine(BuildStatusBar(
            agent.CurrentMode.Name, opts.Model, shownCwd, branch,
            TextUtil.CompactTokenCount(agent.TurnInputTokens), TextUtil.CompactTokenCount(agent.TurnOutputTokens),
            ctx, think, ConsoleColumns()));
    }

    /// <summary>构建提示符：[模式|模型短名] 目录名> </summary>
    private static string PromptFor(ProviderOptions opts, AgentClass agent)
    {
        var model = TextUtil.ShortModelName(opts.Model);
        var dir = new DirectoryInfo(Environment.CurrentDirectory).Name;
        return "\n" + BuildPromptText(agent.CurrentMode.Name, model, dir, ConsoleColumns());
    }

    /// <summary>输出最终答复：若已流式打印过则按需补换行，否则整体打印。
    /// 流式路径此前**无条件**补一个换行：模型最后一段本来就带换行时会多出一个空行，
    /// 而没带换行时又必须补——否则工具状态行/回合摘要会粘在正文同一行。</summary>
    private static void PrintResult(string result, bool streamed, bool prefixNewline, bool streamedOnLineBoundary = false)
    {
        if (streamed)
        {
            if (result.Length > 0 && !streamedOnLineBoundary)
                Console.WriteLine();
        }
        else
        {
            Console.WriteLine((prefixNewline ? "\n" : "") + result);
        }
    }

    /// <summary>PrintResult 的测试入口（internal 以便断言实际写出的字符）。</summary>
    internal static void PrintResultForTest(string result, bool streamed, bool prefixNewline, bool streamedOnLineBoundary) =>
        PrintResult(result, streamed, prefixNewline, streamedOnLineBoundary);

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
                Console.WriteLine(FormatHintLine("（服务未返回任何模型：检查 baseUrl 是否指向支持 /models 的端点、API Key 是否有列表权限；仍可直接 /model <名称> 使用）", ConsoleColumns()));
                return;
            }
            var marked = false;
            // 与 /help、/tools、/mode 同一渲染器：编号列按显示宽度对齐（1) 与 100) 原本不齐），
            // 窄终端下长模型名不再硬折行
            var modelEntries = new List<HelpEntry>(rows.Count);
            foreach (var (num, m) in rows)
            {
                var isCurrent = string.Equals(m, currentModel, StringComparison.OrdinalIgnoreCase);
                if (isCurrent)
                    marked = true;
                modelEntries.Add(new HelpEntry($"{num})", m + (isCurrent ? "  *" : string.Empty)));
            }
            Console.WriteLine(FormatHelpList(modelEntries, ConsoleColumns()));
            Console.WriteLine(FormatHintLine("提示: /model <编号> 可直接切换", ConsoleColumns()));
            if (marked)
                Console.WriteLine(FormatHintLine("* = 当前配置的模型", ConsoleColumns()));
            else if (!string.IsNullOrWhiteSpace(currentModel))
                Console.WriteLine($"  （当前配置的模型不在列表中: {currentModel}）");
        }
        catch (Exception ex)
        {
            WriteNotice($"无法获取模型列表: {ex.Message}");
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

    /// <summary>启动横幅的键列宽度。取最长键 `Workspace` 的 9 列，
    /// 让所有键（含中文键）的冒号落在同一列。
    /// 此前是手打空格对齐：`会话日志`（8 列）后补 2 空格落在第 10 列，
    /// 而 `Version`（7 列）补 2 空格落在第 9 列——**中文键把整列推歪了一格**。
    /// 这正是 FormatKeyValueLine 当初要解决的问题，横幅却一直没用它。</summary>
    internal const int BannerKeyWidth = 9;

    /// <summary>横幅里的键值行：统一走 FormatKeyValueLine，按显示宽度收口。
    /// 启动横幅是最早打印的一屏，baseUrl / 工作区路径 / 会话日志路径都可能很长，
    /// 此前一律 <c>Console.WriteLine($"…")</c> 没有宽度预算，窄终端上会硬折行
    /// 把最后那行分隔线顶走。</summary>
    private static void BannerRow(string key, string value) =>
        Console.WriteLine(FormatKeyValueLine(key, value, BannerKeyWidth, ConsoleColumns()));

    private static void PrintBanner(AgentConfig config, ProviderOptions opts, AgentClass agent)
    {
        try
        {
            // 终端标签页标题：多标签时快速区分项目/模型（不支持标题的终端静默忽略）
            Console.Title = $"CodeAgent · {agent.CurrentMode.Name} · {opts.Model}";
        }
        catch { /* 平台不支持：忽略 */ }
        var width = ConsoleColumns();
        Console.WriteLine(InputLine.FitToWidth("── CodeAgent " + new string('─', 37), width));
        BannerRow("Version", InformationalVersion);
        var bannerOverride = config.PersistedProvider is not null &&
                             !string.Equals(config.Provider, config.PersistedProvider, StringComparison.OrdinalIgnoreCase);
        BannerRow("Provider", $"{config.Provider} ({opts.Type}){(bannerOverride ? "（会话级覆盖）" : "")}");
        BannerRow("Model", opts.Model);
        BannerRow("Mode", agent.CurrentMode.Name);
        BannerRow("Thinking", $"{config.ThinkingEffort}{(config.ThinkingEffort == "auto" ? "（自动探测模型推理档位，状态栏显示实际生效值）" : "")}");
        BannerRow("BaseUrl", opts.BaseUrl);
        BannerRow("Workspace", Environment.CurrentDirectory);
        var bannerBranch = GitInfo.CurrentBranch(Environment.CurrentDirectory);
        if (bannerBranch is not null)
            BannerRow("Git", bannerBranch);
        if (agent.SessionPath is not null)
            BannerRow("会话日志", agent.SessionPath);
        if (config.SourceFile is not null)
            BannerRow("配置文件", config.SourceFile);
        Console.WriteLine(FormatHintLine("输入 /help 查看命令；直接输入任务描述即可开始。", width));
        Console.WriteLine(InputLine.FitToWidth(new string('─', 58), width));
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
                        WriteNotice($"压缩失败: {ex.Message}");
                        break;
                    }
                    if (IsCancelledTurn(result))
                        Console.WriteLine(FormatCancelLine("已取消压缩（历史未变动）。", ConsoleColumns()));
                    else if (result == "SHORT")
                        Console.WriteLine($"{SafeColor.Glyphs.Warn} 当前对话过短，无需压缩。");
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
                if (rest.Trim().Equals("next", StringComparison.OrdinalIgnoreCase))
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
                        WriteNotice($"无效权限模式: {rest}(可选 strict | whitelist | full)");
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
                        Console.WriteLine(FormatNoticeLine(
                            $"没有供应商「{wanted}」，可用: {string.Join(", ", config.Providers.Keys)}", ConsoleColumns()));
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
                        Console.WriteLine(FormatConfirmLine(FormatSettingSavedLine("已切换 Provider", $"{hit.Key}，模型 {opts.Model}", savePath, ConsoleColumns()), ConsoleColumns()));
                    }
                    catch (Exception ex)
                    {
                        WriteNotice($"切换失败: {ex.Message}");
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
                            if (knownModels.Count == 0)
                            {
                                Console.WriteLine("模型列表为空（服务可能不支持 /models）：直接输入完整模型名即可。");
                                break;
                            }
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
                            WriteNotice($"获取模型列表失败: {ex.Message}");
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
                                WriteNotice($"模型列表中没有「{modelArg}」（共 {knownModels.Count} 个模型）");
                                if (near.Count > 0)
                                    WriteNotice($"  相近的模型: {string.Join("、", near)}");
                                Console.WriteLine(FormatHintLine("仍将按输入保存；/models [关键字] 可查列表", ConsoleColumns()));
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
                        Console.WriteLine(FormatConfirmLine(FormatSettingSavedLine("已切换模型", opts.Model, savePath, ConsoleColumns()), ConsoleColumns()));
                        try { Console.Title = $"CodeAgent · {agent.CurrentMode.Name} · {opts.Model}"; } catch { }
                    }
                    catch (Exception ex)
                    {
                        WriteNotice($"切换失败: {ex.Message}");
                    }
                }
                break;

            case "/config":
                {
                    var cfgOverride = config.PersistedProvider is not null &&
                                      !string.Equals(config.Provider, config.PersistedProvider, StringComparison.OrdinalIgnoreCase);
                    // 与状态栏/\/stats 同口径：配置 > 内置表 > 后台探测（探测完成后 /config 能显示 API 元数据值）
                    var effWin = EffectiveContextWindow(config, opts, ctxProbe);
                    var ctxDesc = effWin > 0
                        ? config.ContextWindow > 0 ? $"{effWin:N0}（配置）"
                        : KnownContextWindows.TryGet(opts.Model) is { } known && known == effWin ? $"{known:N0}（按模型名自动识别）"
                        : $"{effWin:N0}（/models 元数据探测）"
                        : "未知（可配置 contextWindow）";
                    var roDirs = config.ReadOnlyDirs.Count == 0 ? "" : $"{config.FileAccess}（只读白名单: {string.Join(", ", config.ReadOnlyDirs)}）";
                    // 一行一项：此前多个设置挤在一行（MaxIter/MaxHistory/ContextWindow 三个一列），
                    // 且键列手数空格补齐后被中文键（工具日志/界面/目录）撑歪。
                    var configRows = new List<(string Key, string Value)>
                    {
                        ("Provider", $"{config.Provider} ({opts.Type}){(cfgOverride ? "（会话级覆盖，不持久化）" : "")}"),
                        ("Model", opts.Model),
                        ("BaseUrl", opts.BaseUrl),
                        ("ApiKey", string.IsNullOrEmpty(opts.ApiKey) ? $"env[{opts.ApiKeyEnv ?? "?"}]" : "****"),
                        ("MaxIter", config.MaxToolIterations.ToString("N0")),
                        ("MaxHistoryChars", $"{config.MaxHistoryChars:N0}"),
                        ("ContextWindow", ctxDesc),
                        ("AutoCompact", config.AutoCompactPercent > 0 ? $"{config.AutoCompactPercent}%（达标自动压缩）" : "off（90% 时仅提示）"),
                        ("命令执行", config.AllowCommands ? "on" : "off"),
                        ("命令确认", config.ConfirmCommands ? "on" : "off"),
                        ("Shell", config.Shell),
                        ("命令超时", $"{config.CommandTimeoutSeconds}s"),
                        ("工具日志", config.ShowToolCalls ? "on" : "off"),
                        ("流式输出", config.StreamOutput ? "on" : "off"),
                        ("会话日志", config.SaveSessions ? $"on（保留 {config.MaxSessionLogs}）" : "off"),
                        ("Markdown 渲染", config.RenderMarkdown ? "on" : "off"),
                        ("菜单", config.TuiAnsi ? "ANSI 原地" : "滚动式"),
                        ("默认模式", config.DefaultMode),
                        ("会话目录", config.SessionDir),
                        ("导出目录", config.ExportDir),
                        ("Access", roDirs),
                    };
                    var configKeyWidth = configRows.Max(r => TextUtil.DisplayWidth(r.Key));
                    var configWidth = ConsoleColumns();
                    foreach (var (key, value) in configRows)
                        Console.WriteLine(FormatKeyValueLine(key, value, configKeyWidth, configWidth));
                }
                break;

            case "/session":
                Console.WriteLine(agent.SessionPath ?? "会话日志未启用（config.SaveSessions=false）。");
                {
                    var logs = RecentSessionLogs(config, int.MaxValue);
                    if (logs.Count > 0)
                        Console.WriteLine($"目录内共 {logs.Count} 个会话日志（保留上限 maxSessionLogs={config.MaxSessionLogs}，滚动新日志时自动清理最旧的；0 = 不清理）。");
                    // 磁盘占用速览：会话日志 + 命名快照 + 导出
                    try
                    {
                        var baseDir = Path.Combine(Environment.CurrentDirectory, ".codeagent");
                        if (Directory.Exists(baseDir))
                        {
                            var totalBytes = TextUtil.GetDirectorySizeBytes(baseDir);
                            Console.WriteLine($".codeagent 目录占用 {TextUtil.FormatBytes(totalBytes)}（/diag 可再次查看；整目录可安全删除）。");
                        }
                    }
                    catch { /* 统计失败不显示 */ }
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
                    Console.WriteLine(FormatCancelLine("已取消配置向导。", ConsoleColumns()));
                    break;
                }
                catch (Exception ex)
                {
                    WriteStartupNotice($"配置向导失败: {ex.Message}");
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
                    // 注意：此处不自动附 diff——TryUndo 弹出条目后 DiffAt(1) 指向下一条旧记录，
                    // 与刚撤销的改动无关；需要看内容用 /diff（对比撤销快照与当前文件）
                }
                else if (rest.Trim().Equals("list", StringComparison.OrdinalIgnoreCase))
                {
                    var list = agent.Context.Undo.ListEntries(width: ConsoleColumns());
                    Console.WriteLine(list.Length == 0 ? "没有可撤销的操作。" : $"可撤销操作（编号 1 = 最近）:\n{list}");
                }
                else if (rest.Trim().Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    // 清空撤销历史：已落盘的文件修改无法回滚，仅丢弃可撤销记录
                    if (agent.Context.Undo.Count == 0)
                        Console.WriteLine("撤销历史已经是空的。");
                    else
                    {
                        var n = agent.Context.Undo.Count;
                        agent.Context.Undo.Clear();
                        Console.WriteLine($"已清空 {n} 条撤销记录（已写入文件的修改不会被回滚）。");
                    }
                }
                else if (string.IsNullOrWhiteSpace(rest))
                {
                    var list = agent.Context.Undo.ListEntries(width: ConsoleColumns());
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
                            Console.WriteLine(FormatCancelLine("已取消。", ConsoleColumns()));
                    }
                }
                else
                {
                    Console.WriteLine("用法: /undo [N|list|clear] —— N = 撤销最近 N 次, list = 列出历史, clear = 清空撤销记录");
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
                            Console.WriteLine(FormatCancelLine("已取消保存。", ConsoleColumns()));
                            break;
                        }
                        agent.SaveSession(rest.Trim());
                        Console.WriteLine(FormatConfirmLine($"已保存会话: {rest.Trim()}", ConsoleColumns()));
                    }
                    catch (Exception ex)
                    {
                        WriteNotice($"保存失败: {ex.Message}");
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
                        // 名称列对齐：原本 "  名称（相对时间）" 各行年龄起始位置参差
                        Console.WriteLine(FormatHelpList(
                            sessions.Select(s => new HelpEntry(s.Item1, $"（{s.Item2}）")).ToList(),
                            ConsoleColumns()));
                    }
                }
                else
                {
                    try
                    {
                        agent.LoadSession(rest.Trim());
                        Console.WriteLine(FormatConfirmLine($"已恢复会话: {rest.Trim()}", ConsoleColumns()));
                        PrintConversation(agent, 20); // 显示恢复的最近 20 条（全量打印长会话会刷屏）
                    }
                    catch (Exception ex)
                    {
                        WriteNotice($"加载失败: {ex.Message}");
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
                            Console.WriteLine(FormatConfirmLine($"已恢复会话: {Path.GetFileName(logs[ridx - 1])}", ConsoleColumns(), "↩"));
                            PrintConversation(agent, 20);
                        }
                        else
                            Console.WriteLine($"{SafeColor.Glyphs.Warn} 会话日志无法恢复（文件可能损坏）。");
                    }
                    else
                    {
                        // 数字越界时明确指出范围（静默回退到列表曾让人以为编号生效了）
                        var resumeEntries = new List<HelpEntry>(logs.Count);
                        if (int.TryParse(rest.Trim(), out _))
                            Console.WriteLine($"{SafeColor.Glyphs.Warn} 编号超出范围（可用 1-{logs.Count}）。最近的会话:");
                        else
                            Console.WriteLine("最近的会话（输入 /resume <编号> 恢复，--continue 启动时自动恢复最近一次）:");
                        for (int i = 0; i < logs.Count; i++)
                        {
                            // 文件名只是时间戳：附上相对时间、首条用户消息预览与条数，才能认出哪个会话是哪段对话
                            var (preview, count, capped) = AgentClass.SessionLogSummary(logs[i]);
                            var label = Path.GetFileNameWithoutExtension(logs[i]);
                            var age = TextUtil.RelativeTime(File.GetLastWriteTimeUtc(logs[i]), DateTime.UtcNow);
                            var countText = capped ? $"≥{count} 条" : $"{count} 条"; // 封顶后是下限，不是精确值
                            var detail = preview is null
                                ? $"{label}（{age}，{countText}）"
                                : $"{label} · {age} · {TextUtil.TruncateLine(preview, 50)}（{countText}）";
                            // 编号列对齐 + 说明按宽度折行（与 /models、/tools 同一渲染器）
                            resumeEntries.Add(new HelpEntry($"{i + 1})", detail));
                        }
                        Console.WriteLine(FormatHelpList(resumeEntries, ConsoleColumns()));
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
                        Console.WriteLine(FormatResultLine("用法: /find <关键字> —— 在历史会话日志里搜索内容（与 /resume 同源，最新在前）", ConsoleColumns()));
                        break;
                    }
                    var logs = ResumableLogs(agent, config);
                    if (logs.Count == 0)
                    {
                        Console.WriteLine(FormatResultLine("没有可搜索的会话记录（先正常对话过一次，或检查 saveSessions 配置）。", ConsoleColumns()));
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
                        Console.WriteLine(FormatResultLine($"{label} · {age}（/resume 可恢复）:", ConsoleColumns()));
                        foreach (var (role, snippet) in hits)
                            Console.WriteLine(FormatSearchHitLine(role, snippet, ConsoleColumns()));
                        printed++;
                    }
                    if (printed == 0)
                        Console.WriteLine(FormatResultLine(
                            FitQueryLine("历史会话中没有匹配「", kw, "」的内容。", ConsoleColumns())
                            ?? $"历史会话中没有匹配「{kw}」的内容。", ConsoleColumns()));
                    else if (moreAvailable)
                        Console.WriteLine(FormatResultLine("…（仅显示前 5 个命中文件，更精确的关键字可减少噪音）", ConsoleColumns()));

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
                        Console.WriteLine(FormatResultLine($"快照 {name} · {age}（/load {name} 恢复）:", ConsoleColumns()));
                        foreach (var (role, snippet) in hits)
                            Console.WriteLine(FormatSearchHitLine(role, snippet, ConsoleColumns()));
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
                    if (arg.Equals("all", StringComparison.OrdinalIgnoreCase))
                    {
                        // /export all：批量导出全部可恢复的历史会话与命名快照（单个失败跳过不中断）
                        var logs = ResumableLogs(agent, config);
                        if (logs.Count == 0 && !Directory.Exists(Path.Combine(Environment.CurrentDirectory, config.SessionDir)))
                        {
                            Console.WriteLine($"{SafeColor.Glyphs.Warn} 没有历史会话日志可导出。");
                            break;
                        }
                        var ok = 0;
                        foreach (var log in logs)
                        {
                            try
                            {
                                var exported = agent.ExportSessionLogMarkdown(log);
                                Console.WriteLine(FormatConfirmLine($"{Path.GetFileNameWithoutExtension(log)} → {exported}", ConsoleColumns()));
                                ok++;
                            }
                            catch (Exception ex)
                            {
                                WriteNotice($"跳过 {Path.GetFileName(log)}: {ex.Message}");
                            }
                        }
                        // 命名快照一并导出
                        foreach (var (name, _) in SavedSessions(Path.Combine(Environment.CurrentDirectory, config.SessionDir)))
                        {
                            try
                            {
                                var exported = agent.ExportMarkdown(name);
                                Console.WriteLine(FormatConfirmLine($"快照 {name} → {exported}", ConsoleColumns()));
                                ok++;
                            }
                            catch (Exception ex)
                            {
                                WriteNotice($"跳过快照 {name}: {ex.Message}");
                            }
                        }
                        Console.WriteLine($"共处理 {ok} 个会话。");
                        break;
                    }
                    if (arg.Length > 0 && agent.SessionExists(arg))
                        file = agent.ExportMarkdown(arg);
                    else if (int.TryParse(arg, out var eidx) && eidx >= 1)
                    {
                        var logs = ResumableLogs(agent, config);
                        if (logs.Count == 0)
                        {
                            Console.WriteLine($"{SafeColor.Glyphs.Warn} 没有历史会话日志可导出（先正常对话过一次，或 /save <名> 后 /export <名>）。");
                            break;
                        }
                        if (eidx > logs.Count)
                        {
                            Console.WriteLine($"{SafeColor.Glyphs.Warn} 编号超出范围（可用 1-{logs.Count}，/resume 查看列表）。");
                            break;
                        }
                        file = agent.ExportSessionLogMarkdown(logs[eidx - 1]);
                    }
                    else
                        file = agent.ExportMarkdown(arg.Length == 0 ? null : arg);
                    Console.WriteLine(FormatConfirmLine($"已导出: {file}", ConsoleColumns()));
                }
                catch (Exception ex)
                {
                    WriteNotice($"导出失败: {ex.Message}");
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
                    // 曾是一整句 100+ 列的中文，80 列终端必折行，扫读时找不到某个数字。
                    // 拆成键值行后每项独立成行，窄终端也不会把数字甩到行尾。
                    var statRows = new List<(string Key, string Value)>
                    {
                        ("模型", opts.Model),
                        ("请求次数", $"{agent.ProviderCalls:N0}"),
                        ("输入 tokens", $"{agent.TotalInputTokens:N0}"),
                        ("输出 tokens", $"{agent.TotalOutputTokens:N0}"),
                    };
                    if (agent.ProviderCalls > 0)
                        statRows.Add(("平均每次", $"{avg:N0} tokens"));
                    if (agent.TotalCachedTokens > 0)
                        statRows.Add(("缓存命中", $"{agent.TotalCachedTokens:N0} tokens"));
                    statRows.Add(("当前上下文", ctxText));
                    statRows.Add(("会话时长", TextUtil.FormatSessionTime(SessionStopwatch.Elapsed)));
                    if (cost is { } c)
                        statRows.Add(("累计费用", $"≈${TextUtil.FormatCost(c)}"));
                    var statKeyWidth = statRows.Max(r => TextUtil.DisplayWidth(r.Key));
                    // 数值列右对齐：请求次数/输入/输出 tokens 量级不同，左对齐时位数不在同一列
                    var statRight = ShouldRightAlignValues(statRows.Select(r => r.Value));
                    Console.WriteLine("会话统计:");
                    foreach (var (key, value) in statRows)
                        Console.WriteLine(FormatKeyValueLine(key, value, statKeyWidth, ConsoleColumns(), statRight));
                }
                break;

            case "/tools":
                var modeTools = agent.ToolsForMode();
                Console.WriteLine($"可用工具（当前模式: {agent.CurrentMode.Name}，共 {modeTools.Count} 个）:");
                // 与 /help 同一渲染器：工具名对齐成列，说明按终端宽度折行。
                // 此前每行硬拼接，110 个工具的长说明在窄终端会从词中间断开。
                Console.WriteLine(FormatHelpList(
                    modeTools.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(t => new HelpEntry(t.Name, t.Description)).ToList(),
                    ConsoleColumns()));
                break;

            case "/providers":
                Console.WriteLine($"已配置的 Provider（当前: {config.Provider}，-p <名> 或改 provider 切换）:");
                {
                    var providerEntries = new List<HelpEntry>();
                    foreach (var kv in config.Providers)
                    {
                        var isCurrent = string.Equals(kv.Key, config.Provider, StringComparison.OrdinalIgnoreCase);
                        // 会话级覆盖（env / -p）指向的目标：标注以免用户误以为已持久化
                        var sessionOnly = isCurrent && config.PersistedProvider is not null &&
                                          !string.Equals(kv.Key, config.PersistedProvider, StringComparison.OrdinalIgnoreCase);
                        var cur = isCurrent ? (sessionOnly ? " ←（会话级）" : " ←") : "";
                        var price = kv.Value.PricePerMillionInput > 0
                            ? $"  单价: ${kv.Value.PricePerMillionInput:F2}/${kv.Value.PricePerMillionOutput:F2} per M"
                            : "";
                        // baseUrl 可能很长，统一交给宽度自适应渲染器折行
                        providerEntries.Add(new HelpEntry(kv.Key,
                            $"({kv.Value.Type}) 模型: {kv.Value.Model}  baseUrl: {kv.Value.BaseUrl}{price}{cur}"));
                    }
                    Console.WriteLine(FormatHelpList(providerEntries, ConsoleColumns()));
                }
                break;

            case "/mode":
                if (string.IsNullOrWhiteSpace(rest))
                {
                    Console.WriteLine($"当前模式: {agent.CurrentMode.Name}");
                    Console.WriteLine(ModeListText(config, agent.CurrentMode.Name));
                    Console.WriteLine("（提示: 按 Alt+M 弹出模式菜单，Shift+Tab 快速切换下一个模式）");
                }
                else if (rest.Trim().Equals("next", StringComparison.OrdinalIgnoreCase))
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
                        Console.WriteLine(FormatNoticeLine(
                            $"没有模式「{wanted}」，可用: {string.Join(", ", modes.Select(m => m.Name))}", ConsoleColumns()));
                        var near = modes.Where(m => m.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                                                    || wanted.Contains(m.Name, StringComparison.OrdinalIgnoreCase))
                                        .Select(m => m.Name).Take(3).ToList();
                        if (near.Count > 0)
                            WriteNotice($"  相近的模式: {string.Join("、", near)}");
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
                        : $"{SafeColor.Glyphs.Warn} 无法访问剪贴板（需要 clip.exe / pbcopy / xclip）。");
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
                        // 单列路径列表：深路径在窄终端硬折行后，续行会顶到行首与首行失去关联
                        Console.WriteLine(FormatPathList(
                            paths.Select(p => agent.Context.Workspace.ToRelative(p).Replace('\\', '/')),
                            ConsoleColumns()));
                    }
                }
                break;

            case "/diag":
                // 终端环境诊断：定位输入卡顿 / 菜单渲染问题
                {
                    static string W(Func<int> f)
                    {
                        try { return f().ToString(); }
                        catch (Exception e) { return $"读取失败: {e.Message}"; }
                    }
                    var wt = Environment.GetEnvironmentVariable("WT_SESSION");
                    var term = Environment.GetEnvironmentVariable("TERM_PROGRAM");
                    // 键列宽度由所有键的显示宽度决定：中文键算 2 列，值再按剩余宽度截断
                    var diagRows = new (string Key, string Value)[]
                    {
                        ("IsInputRedirected", Console.IsInputRedirected.ToString()),
                        ("OutputEncoding", $"{Console.OutputEncoding.WebName} (CP{W(() => (int)Console.OutputEncoding.CodePage)})"),
                        ("WindowWidth", W(() => Console.WindowWidth)),
                        ("WindowHeight", W(() => Console.WindowHeight)),
                        ("BufferWidth", W(() => Console.BufferWidth)),
                        ("CursorLeft/Top", $"{W(() => Console.CursorLeft)}/{W(() => Console.CursorTop)}"),
                        ("TuiAnsi", config.TuiAnsi.ToString()),
                        ("IsOutputRedirected", Console.IsOutputRedirected.ToString()),
                        ("Terminal", wt is not null ? "Windows Terminal" : term is not null ? term : "未知（conhost 或其他）"),
                        ("Git branch", GitInfo.CurrentBranch(Environment.CurrentDirectory) ?? "(非 git 仓库)"),
                    };
                    var keyWidth = diagRows.Max(r => TextUtil.DisplayWidth(r.Key));
                    var diagWidth = ConsoleColumns();
                    Console.WriteLine("终端诊断:");
                    foreach (var row in diagRows)
                        Console.WriteLine(FormatKeyValueLine(row.Key, row.Value, keyWidth, diagWidth));
                    try
                    {
                        var caDir = Path.Combine(Environment.CurrentDirectory, ".codeagent");
                        if (Directory.Exists(caDir))
                        {
                            var bytes = TextUtil.GetDirectorySizeBytes(caDir);
                            Console.WriteLine(FormatKeyValueLine(
                                ".codeagent 大小",
                                $"{TextUtil.FormatBytes(bytes)}（会话日志/导出/历史，可整目录删除）",
                                keyWidth, diagWidth));
                        }
                    }
                    catch { /* 统计失败不影响诊断输出 */ }
                }
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
                            Console.WriteLine(FormatConfirmLine(FormatSettingSavedLine("思考强度已设为", v, savePath, ConsoleColumns()), ConsoleColumns()));
                        }
                        catch (Exception ex)
                        {
                            WriteNotice($"思考强度已设为: {v}（保存配置失败: {ex.Message}）");
                        }
                    }
                    else
                    {
                        WriteNotice($"无效值: {rest}（可选: off / low / medium / high / auto）");
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
                            Console.WriteLine(FormatConfirmLine(FormatSettingSavedLine("命令 shell 已设为", v, savePath, ConsoleColumns()), ConsoleColumns()));
                        }
                        catch (Exception ex)
                        {
                            WriteNotice($"命令 shell 已设为: {v}（保存配置失败: {ex.Message}）");
                        }
                    }
                    else
                    {
                        WriteNotice($"无效值: {rest}（可选: cmd / powershell / pwsh / bash / sh / auto）");
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
                Console.WriteLine(FormatNoticeLine(
                    $"未知命令: {cmd}（输入 /help 查看命令，Tab 可补全）", ConsoleColumns()));
                break;
        }
        return suppressStatusBar;
    }

    /// <summary>「某项已切换为某值（说明）」确认行的统一降级。
    /// **值是主体**（用户刚切到了什么），说明是补充。直接交给 FormatConfirmLine
    /// 硬截尾部会把值本身切掉（`已切换权限: whi…`）——用户看不到自己切到了哪里。
    /// 降级顺序：先按剩余列数截说明，说明一行都放不下时**整段丢掉**说明；
    /// 值本身也超宽才截，并标「…」。width &lt;= 0 表示宽度未知：原样拼接。
    /// 模式切换（`已切换模式: plan — …`）与权限切换（`已切换权限: whitelist（…）`）
    /// 共用这一个阶梯，差别只在分隔符——两份几乎一样的实现迟早会只改一个。</summary>
    internal static string FormatSwitchedWithDetailLine(string action, string value, string detail, string separator, int width = 0, int minDetailWidth = 0)
    {
        var head = $"{action}: {value}";
        if (width <= 0)
            return detail.Length > 0 ? head + separator + detail : head;
        var floor = minDetailWidth > 0 ? minDetailWidth : MinModeDescriptionWidth;
        var room = width - TextUtil.DisplayWidth(head) - TextUtil.DisplayWidth(separator);
        if (room >= floor)
            return head + separator + InputLine.FitToWidth(detail, room);
        // 说明一行都放不下：只给值。半个说明（`自动…`）传达的信息比没有更少，
        // 还白白挤掉值。
        return InputLine.FitToWidth(head, width);
    }

    /// <summary>模式说明至少要有的列数。低于此值说明不如只显示名称——
    /// 半个说明（「自动…」）传达的信息比没有更少，还挤掉了名称。</summary>
    internal const int MinModeDescriptionWidth = 6;

    /// <summary>模式切换确认行：`已切换模式: 名称 — 说明`。
    /// 模式**名称**必须完整显示——用户需要确认自己切到了哪个模式。
    /// 委托给 <see cref="FormatSwitchedWithDetailLine"/>，与权限切换共用同一套降级阶梯。</summary>
    internal static string FormatModeSwitchedLine(string name, string description, int width = 0) =>
        FormatSwitchedWithDetailLine("已切换模式", name, description, " — ", width);

    /// <summary>权限切换确认行：`已切换权限: 模式（说明）`。
    /// 权限名（strict/whitelist/full）是主体，说明是补充。整行硬截尾部会让**权限名**
    /// 被切掉（`已切换权限: whi…`）——用户不知道自己切到了哪个权限，而 Shift+Tab
    /// 正是连着按下去的。全角括号比值更先让位：说明连一列都放不下时整段丢掉括号。</summary>
    internal static string FormatAccessSwitchedLine(string mode, string description, int width = 0)
    {
        if (width <= 0)
            return $"已切换权限: {mode}（{description}）";
        // 「（」「）」各占 2 列，先把括号占的列从预算里扣掉
        var room = width - TextUtil.DisplayWidth("已切换权限: ") - TextUtil.DisplayWidth(mode) - 4;
        if (room < MinModeDescriptionWidth)
            return InputLine.FitToWidth($"已切换权限: {mode}", width);
        return $"已切换权限: {mode}（{InputLine.FitToWidth(description, room)}）";
    }

    /// <summary>模式切换的灰色单行确认（Tab / /mode 用；状态栏本轮跳过，避免模式名重复三处）。</summary>
    private static void PrintModeSwitched(AgentMode mode, string model)
    {
        try { Console.Title = $"CodeAgent · {mode.Name} · {model}"; } catch { /* 部分终端不支持标题 */ }
        using var scope = SafeColor.Scope(SafeColor.Muted);
        Console.WriteLine(FormatConfirmLine(FormatModeSwitchedLine(mode.Name, mode.Description), ConsoleColumns()));
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

    /// <summary>模式列表文本（/mode 无参数用）：当前模式标 ←。
    /// 与 /help、/tools 共用同一宽度自适应渲染器——模式说明是中文，窄终端同样需要折行。</summary>
    internal static string ModeListText(AgentConfig config, string currentMode) =>
        FormatHelpList(
            Modes.Build(config)
                .Select(m => new HelpEntry(m.Name,
                    m.Description + (m.Name.Equals(currentMode, StringComparison.OrdinalIgnoreCase) ? "  ←" : string.Empty)))
                .ToList(),
            ConsoleColumns());
    /// <summary>命令是否为模式/权限切换。必须与 HandleCommand 的切换分支保持一致
    /// （切换命令恰好输出一行确认并跳过状态栏，原地覆盖按「消息+空行+提示符」三行计算）。</summary>
    internal static bool IsSwitchCommand(string cmd, string rest) =>
        rest.Trim().Equals("next", StringComparison.OrdinalIgnoreCase) && cmd is "/mode" or "/access"
        || cmd == "/mode" && !string.IsNullOrWhiteSpace(rest)
        || cmd == "/access" && rest.Trim().ToLowerInvariant() is "strict" or "whitelist" or "full";

    /// <summary>帮助条目：命令/参数 + 说明。</summary>
    internal readonly record struct HelpEntry(string Command, string Description);

    /// <summary>说明列至少保留的宽度：低于此值时分列显示反而更难读，改为上下两行。</summary>
    private const int MinHelpBodyWidth = 24;

    /// <summary>说明折行后的续行缩进宽度（"  " + "    "）。续行不再带命令列，
    /// 所以折行预算必须扣掉**这个**缩进，而不是命令列宽度。</summary>
    private const int HelpContIndent = 6;

    /// <summary>折行后的说明块：每一行都带续行缩进。
    /// 直接 `indent + "    " + WrapDisplay(…)` 只会给**首行**加缩进，
    /// 第 2..n 行跑到行首——续行看起来像顶层输出，读者会以为这是另一条命令。
    /// 用 <see cref="TextUtil.IndentBlock"/> 给每一行都补上前缀。</summary>
    internal static string HelpContinuation(string description, int width) =>
        TextUtil.IndentBlock(TextUtil.WrapDisplay(description, width), new string(' ', HelpContIndent));

    /// <summary>帮助列表渲染。
    /// 命令列按最长条目自适应；说明放不下时**整体移到下一行并缩进折行**，
    /// 而不是交给终端硬折行——硬折行会在词中间断开、丢失缩进，续行看起来像另一条内容。
    /// width &lt;= 0 表示宽度未知，保持单列不猜测折行位置。</summary>
    internal static string FormatHelpList(IReadOnlyList<HelpEntry> entries, int width = 0)
    {
        if (entries.Count == 0)
            return string.Empty;
        const string indent = "  ";
        const int gap = 2;
        var commandWidth = entries.Max(e => TextUtil.DisplayWidth(e.Command));
        var useColumns = width <= 0 || width - indent.Length - commandWidth - gap >= MinHelpBodyWidth;
        // 走单列（命令与说明同行）时，说明能拿到的列数 = 整行 - 缩进 - 命令列 - 间隔。
        var inlineWidth = width - indent.Length - commandWidth - gap;
        // 折行后的续行只有缩进、没有命令列，预算必须按续行缩进（6 列）算。
        // 此前这里错用了 inlineWidth 给续行，等于给续行预留了命令列的宽度，
        // 结果 2 + 4 + (width - 2 - commandWidth - 2) = width + 2 - commandWidth 列——
        // 只要命令列宽于 2 列（永远是），每一行折行的说明都会溢出终端。
        var contWidth = width - HelpContIndent;
        var output = new StringBuilder();
        foreach (var entry in entries)
        {
            if (!useColumns)
            {
                // 命令名本身可能比终端还宽（dependency_lockfile_report = 25 列）：
                // 必须截断，否则这一行会硬折行，把整列表的网格结构打散。
                output.AppendLine(indent + FitColumn(entry.Command, width));
                if (entry.Description.Length > 0)
                    output.AppendLine(HelpContinuation(entry.Description, contWidth));
                continue;
            }
            // 说明放不进同行就整体挪到下一行；判定用**同行**预算，折行用**续行**预算
            if (width > 0 && TextUtil.DisplayWidth(entry.Description) > inlineWidth)
            {
                output.AppendLine(indent + FitColumn(entry.Command, width));
                output.AppendLine(HelpContinuation(entry.Description, contWidth));
                continue;
            }
            output.AppendLine(indent + InputLine.PadToDisplayWidth(entry.Command, commandWidth + gap) + entry.Description);
        }
        return output.ToString().TrimEnd();
    }

    /// <summary>键列占整行宽度的上限比例：键是**标签**、值是**数据**。
    /// 窄屏上把 18 列留给 `IsOutputRedirected` 这类键、只换来 7 列数据是本末倒置——
    /// 用户看得见「是哪个设置」，却看不见「设成了什么」。</summary>
    internal const double KeyColumnMaxRatio = 0.4;

    /// <summary>键列在给定整行宽度下能占的列数上限（宽度未知返回 0 = 不限）。
    /// 所有键值行共用同一个 <c>keyWidth</c>，所以收窄是对**整列**等比生效的，
    /// 冒号列对齐不受影响。</summary>
    internal static int KeyColumnBudget(int width, int indentWidth = 2, int separatorWidth = 3)
    {
        if (width <= 0)
            return 0;
        return Math.Max(1, (int)(width * KeyColumnMaxRatio));
    }

    /// <summary>键值行统一格式：两空格缩进 + 键列按**显示宽度**对齐 + " : " + 值。
    /// 曾用手数空格对齐，`.codeagent 大小` 这类含中文的键（2 字符占 4 列）
    /// 和恰好 18 字符的 `IsOutputRedirected` 都会把冒号挤歪，整列失去对齐。
    /// 值超过剩余宽度时按显示宽度截断（宽度未知则不截断）。
    /// 窄终端下键列还会按 <see cref="KeyColumnMaxRatio"/> 收窄，把宽度让给值。
    /// rightAlignValue：数值列右对齐，让 `128` 与 `1,234,567` 的位数落在同一列、一眼可比大小。</summary>
    internal static string FormatKeyValueLine(string key, string value, int keyWidth = 0, int width = 0, bool rightAlignValue = false)
    {
        const string indent = "  ";
        const string separator = " : ";
        var column = keyWidth > 0 ? Math.Max(keyWidth, TextUtil.DisplayWidth(key)) : TextUtil.DisplayWidth(key);
        if (width <= 0)
            return indent + InputLine.PadToDisplayWidth(key, column) + separator + value;
        // 键列本身可能就比终端还宽（IsOutputRedirected = 18 列 + 缩进 + 分隔符 = 24 列）：
        // 必须裁剪，否则这一行硬折行会把后续整列全部推歪。裁剪后不能再补回 column，
        // 否则刚截掉的字符又被空格填回来。
        var keyBudget = width - indent.Length - separator.Length;
        if (keyBudget <= 0)
            return InputLine.FitToWidth(indent + key + separator.TrimStart(), Math.Max(1, width));
        // 标签列不能吃掉整行：键收窄后宽度让给值（整列等比收窄，冒号对齐不变）。
        // 上限只减不增——窄键（如 `a`）不能被撑到上限。
        var columnCap = Math.Min(column, Math.Max(1, KeyColumnBudget(width)));
        var padded = InputLine.PadToDisplayWidth(InputLine.FitToWidth(key, keyBudget), Math.Min(columnCap, keyBudget));
        var head = indent + padded + separator;
        var valueBudget = width - TextUtil.DisplayWidth(head);
        if (valueBudget <= 0)
            // 值放不下时必须标「…」。此前直接甩下一个孤零零的冒号（`  key :`），
            // 读起来像"值是空的"，而真实情况是"值被终端宽度截掉了"——
            // 两者含义完全不同，后者更值得警惕（用户可能据此以为配置真的没设）。
            return InputLine.FitToWidth(head.TrimEnd() + (valueBudget == 0 ? "…" : string.Empty), Math.Max(1, width));
        var fitted = InputLine.FitToWidth(value, valueBudget);
        if (!rightAlignValue)
            return head + fitted;
        // 右对齐：先补空格再裁。补够 valueBudget 列，超长值仍以裁剪为准（不会溢出）。
        var pad = valueBudget - TextUtil.DisplayWidth(fitted);
        return pad > 0 ? head + new string(' ', pad) + fitted : head + fitted;
    }

    /// <summary>一组键值行的值是否应当右对齐：**全部**值都是「数字（可带千分位/小数）+ 可选单位」才右对齐。
    /// 只对部分行右对齐会看起来像渲染错位；有一个非数值行就整体左对齐。</summary>
    internal static bool ShouldRightAlignValues(IEnumerable<string> values)
    {
        var any = false;
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;
            any = true;
            if (!IsNumericValue(value))
                return false;
        }
        return any;
    }

    /// <summary>判断是否为纯数值（允许符号、千分位、小数点、百分号与末尾单位词，如 `1,234 tokens` / `92%`）。</summary>
    internal static bool IsNumericValue(string value)
    {
        var text = value.Trim();
        if (text.Length == 0)
            return false;
        var i = 0;
        if (text[i] is '+' or '-')
            i++;
        // 货币/近似前缀在数字之前（≈$1.23、$12、€3）
        while (i < text.Length && !char.IsAsciiDigit(text[i]) && !char.IsAsciiLetter(text[i]))
            i++;
        var digits = 0;
        var seenDot = false;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsAsciiDigit(c))
            {
                digits++;
                continue;
            }
            if (c == ',' && digits > 0 && !seenDot)
                continue;
            if (c == '.' && digits > 0 && !seenDot)
            {
                seenDot = true;
                continue;
            }
            break;
        }
        if (digits == 0)
            return false;
        // 余下必须是单位/百分号/货币前缀这类非数字尾巴（不允许再出现数字）
        return text[i..].All(c => !char.IsAsciiDigit(c));
    }

    /// <summary>会话搜索命中行：`  [角色] 摘要`。摘要按**显示宽度**裁到终端剩余列——
    /// 曾写死 110 字符：80 列终端溢出 30 列，而中文摘要在 110 字符（220 列）时就被砍掉一半，
    /// 宽屏白白浪费。宽度未知时回退到 110 字符，保持原有观感。
    /// 终端比角色标记还窄时，按 <see cref="FitRoleTag"/> 缩成可辨识的缩写并给摘要留出余量——
    /// 直接丢掉摘要会得到一列长得一模一样的角色标记，完全看不出命中了什么。</summary>
    internal static string FormatSearchHitLine(string role, string snippet, int width = 0)
    {
        const string indent = "  ";
        var tag = $"[{role}] ";
        var head = indent + tag;
        if (width <= 0)
            return head + TextUtil.TruncateLine(snippet, 110);
        var budget = width - TextUtil.DisplayWidth(head);
        if (budget <= 0)
        {
            // 复用历史列表的缩写规则：同一角色在 /history 与搜索结果里长得一样
            var shortTag = FitRoleTag(role, false, Math.Max(1, width - indent.Length));
            var snippetBudget = width - indent.Length - TextUtil.DisplayWidth(shortTag);
            return snippetBudget <= 0
                ? indent + shortTag.TrimEnd()
                : indent + shortTag + InputLine.FitToWidth(snippet, snippetBudget);
        }
        return head + InputLine.FitToWidth(snippet, budget);
    }

    /// <summary>把命令名裁到给定宽度（width 未知时原样返回）。</summary>
    private static string FitColumn(string command, int width) =>
        width <= 0 ? command : InputLine.FitToWidth(command, Math.Max(1, width - 2));

    /// <summary>单列列表（路径等无空格长词）：每条一行，超宽时按显示宽度折行并**悬挂缩进**，
    /// 续行对齐到正文起点。终端硬折行会把续行顶到行首，续行与首行看起来毫无关系。
    /// 宽度不足以容纳任何完整字符时宁可原样输出，也不静默丢内容。</summary>
    internal static string FormatPathList(IEnumerable<string> items, int width = 0)
    {
        var output = new StringBuilder();
        const string indent = "  ";
        const string hang = "    ";
        foreach (var item in items)
        {
            if (width <= 0 || TextUtil.DisplayWidth(item) <= width - indent.Length)
            {
                output.AppendLine(indent + item);
                continue;
            }
            var head = TextUtil.TakeDisplayWidth(item, width - indent.Length);
            if (head.Length == 0)
            {
                output.AppendLine(indent + item); // 退化：原样输出，不丢内容
                continue;
            }
            output.AppendLine(indent + head);
            var rest = item[head.Length..];
            while (rest.Length > 0)
            {
                var part = TextUtil.TakeDisplayWidth(rest, width - hang.Length);
                if (part.Length == 0)
                {
                    output.AppendLine(hang + rest);
                    break;
                }
                output.AppendLine(hang + part);
                rest = rest[part.Length..];
            }
        }
        return output.ToString().TrimEnd();
    }

    /// <summary>REPL 命令清单（结构化，供 /help 按终端宽度渲染）。</summary>
    internal static readonly HelpEntry[] ReplCommands =
    [
        new("/help", "显示本帮助"),
        new("/clear", "清空对话历史"),
        new("/compact [重点]", "压缩对话历史为摘要（重点并入摘要指令；/clear 是彻底清空）"),
        new("/cls", "清空屏幕（或按 Ctrl+L）"),
        new("/model [名称|编号]", "查看或切换模型（编号按完整列表）"),
        new("/provider [名]", "查看或切换供应商（无需重启）"),
        new("/copy", "复制最近一条助手回复到剪贴板"),
        new("/prompt", "查看当前生效的系统提示"),
        new("/files", "列出本次会话修改过的文件"),
        new("/config", "显示当前配置"),
        new("/session", "显示会话日志路径"),
        new("/setup", "运行交互式供应商配置向导"),
        new("/undo", "撤销最近一次文件修改（write/edit）"),
        new("/diff [N]", "显示最近一次修改的 diff（N = 倒数第 N 条）"),
        new("/save <名>", "保存当前会话（命名快照）"),
        new("/load <名>", "恢复已保存的会话"),
        new("/export [名/编号/all]", "导出会话为 Markdown（同名快照优先；编号为 /resume 列表中的历史会话；all = 全部）"),
        new("/stats", "显示 token 用量统计"),
        new("/retry", "重新执行上一条请求"),
        new("/tools", "列出可用工具"),
        new("/providers", "显示已配置的 Provider"),
        new("/models [关键字]", "列出/过滤模型（过滤时编号不变）"),
        new("/diag", "显示终端环境诊断"),
        new("/history [N]", "显示对话历史（N = 最近 N 条）"),
        new("/resume [编号]", "恢复历史会话（--continue 启动时自动恢复最近一次）"),
        new("/find <关键字>", "在历史会话日志中搜索内容"),
        new("/thinking", "查看或设置思考强度（off/low/medium/high/auto）"),
        new("/shell [名称]", "查看或切换命令 shell（cmd/powershell/pwsh/bash/sh，auto=自动检测）"),
        new("/mode [名称]", "查看或切换工作模式（内置 8 种 + 自定义）"),
        new("/access [模式]", "查看或切换文件访问权限（strict/whitelist/full，next 循环切换）"),
        new("/exit, /quit", "退出"),
    ];

    private static void PrintReplHelp()
    {
        Console.WriteLine("命令:");
        Console.WriteLine(FormatHelpList(ReplCommands, ConsoleColumns()));
        Console.WriteLine("""
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
