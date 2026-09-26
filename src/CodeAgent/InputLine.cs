using System.Text;

namespace CodeAgent;

/// <summary>括号粘贴模式（bracketed paste）使能/复位：终端把粘贴内容包在
/// ESC[200~ … ESC[201~ 里，粘贴边界从计时启发式变成确定性标记。
/// 模式在进程生命周期内保持开启（每个提示符都可能粘贴）；退出时必须复位，
/// 否则标记序列残留给后续的 shell（cmd 对它显示乱码）。不支持的终端忽略使能序列，
/// 输入层自动回退到旧的间隔启发式。</summary>
public static class BracketedPaste
{
    private static bool _enabled;

    /// <summary>开启括号粘贴（幂等）。只在交互式 ANSI 终端下有效。</summary>
    public static void Enable()
    {
        if (_enabled)
            return;
        _enabled = true;
        Console.Write("\x1b[?2004h");
        // 进程退出兜底复位（/exit 的 Environment.Exit 也会触发 ProcessExit）；
        // 正常路径关闭的可靠性不依赖这里，双发一次复位序列无害
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Console.Write("\x1b[?2004l"); } catch { /* 进程退出中，尽力而为 */ }
        };
    }
}

/// <summary>
/// 终端输入行：斜杠命令菜单（**ANSI 原地渲染**：方向键让 ">" 在列表内上下移动，
/// 纯转义文本输出，不依赖控制台光标 API，兼容 Windows Terminal 等现代终端；
/// `tuiAnsi=false` 时退回滚动式）、数字键 1-9 直接执行、↑/↓ 历史、TAB 补全、
/// 退格、Ctrl+L 清屏、Alt+M/U/D/N 快捷键。
/// stdin 被重定向（管道/文件）时自动回退为 Console.ReadLine，保证脚本兼容。
/// </summary>
public static class InputLine
{
    /// <summary>多行输入折叠阈值：超过该行数时折叠为「前 N-1 行 + 提示行」。单一口径源，避免各处硬编码 3 漂移。</summary>
    internal const int FoldThreshold = 3;

    /// <summary>折叠提示行里「末行」预览的最大显示宽度（超出按 CJK 双宽截断）。</summary>
    internal const int FoldTailPreviewWidth = 40;

    /// <summary>末行预览的最小可读列数：不足这么多列时整段省略，而不是切出看不出内容的碎片。</summary>
    internal const int MinFoldTailWidth = 8;

    /// <summary>折叠提示行固定前缀的显示宽度（不含末行预览）。按显示宽度算——
    /// 提示文案几乎全是中文，按字符数算会低估近一倍，窄屏下反而更容易折行。
    /// 前缀**本身**也会随 ASCII 退回变短（⏷ ↓ · 都不是 ASCII），
    /// 所以必须和真正拼出来的那一行用同一个前缀，否则算出的列数和实际不符。</summary>
    internal static string FoldHintPrefix(int lineCount)
    {
        var head = $"{SafeColor.Glyphs.Fold} 共 {lineCount} 行 {SafeColor.Glyphs.Unfold} 展开";
        return SafeColor.Glyphs.AsciiEnabled ? $"{head} - 末行: " : $"{head} · 末行: ";
    }

    internal static int FoldHintPrefixWidth(int lineCount) =>
        TextUtil.DisplayWidth(FoldHintPrefix(lineCount));

    /// <summary>末行预览在该终端宽度下能拿到的列数。
    /// 宽度未知（&lt;= 0）时用固定值（不做猜测性裁剪）；剩余不足 <see cref="MinFoldTailWidth"/> 列时返回 0 ——
    /// 此时整段省略：折行的提示行会破坏折叠块的固定行高，菜单锚点随之错位。</summary>
    internal static int FoldTailBudget(int windowWidth, int lineCount, int minTail = MinFoldTailWidth)
    {
        if (windowWidth <= 0)
            return FoldTailPreviewWidth;
        var remain = windowWidth - FoldHintPrefixWidth(lineCount);
        return remain < minTail ? 0 : Math.Min(FoldTailPreviewWidth, remain);
    }

    /// <summary>
    /// 折叠长文本为「前 threshold-1 行 + 折叠提示行」：行数超过 threshold 时折叠，否则原样。
    /// 折叠提示行显示总行数（⏷ 共 N 行 ↓ 展开）并附末行预览：折叠后中间行不可见，
    /// 此前无法确认「末尾长什么样」，粘贴长段落后只能先展开再看。
    /// <paramref name="windowWidth"/> 传入时按该宽度收敛末行预览，避免提示行折行。
    /// </summary>
    internal static string FoldText(string text, int threshold = FoldThreshold, int windowWidth = 0)
    {
        var lines = DiffUtil.SplitLines(text);
        if (lines.Length <= threshold)
            return text;
        var tail = lines[^1].Trim();
        if (tail.Length == 0 && lines.Length >= 2)
            tail = lines[^2].Trim(); // 末尾空行：回看一行，避免出现空的「末行: 」
        var budget = FoldTailBudget(windowWidth, lines.Length);
        var head = $"{SafeColor.Glyphs.Fold} 共 {lines.Length} 行 {SafeColor.Glyphs.Unfold} 展开";
        var sep = SafeColor.Glyphs.AsciiEnabled ? " - " : " · ";
        var hint = budget == 0 || tail.Length == 0
            ? head
            : $"{head}{sep}末行: {FitToWidth(tail, budget)}";
        return string.Join('\n', lines.Take(threshold - 1)) + "\n" + hint;
    }

    /// <summary>反向搜索时查询串的最大显示宽度（超出截断；长查询曾把草稿整段挤出屏幕）。</summary>
    internal const int SearchQueryDisplayWidth = 24;

    /// <summary>搜索行除查询串外的固定开销列数：`(搜索)` + 两枚反引号 + 命中标记 + 分隔空格。</summary>
    internal const int SearchRowOverhead = 10;

    /// <summary>搜索行必须给**草稿**留出的列数。查询串可以让位，草稿不能丢：
    /// 用户按 Ctrl+R 是为了看自己正在搜什么，但此时正在编辑的草稿同样不能被顶没。
    /// 此前查询预算不预留草稿空间，查询串一路吃到行尾，草稿被压成一两个残字符。</summary>
    internal const int MinSearchDraftWidth = 8;

    /// <summary>反向搜索时查询串在该终端宽度下能拿到的列数。
    /// 此前写死 <see cref="SearchQueryDisplayWidth"/>：窄终端上「提示符 + (搜索) + 24 列查询」
    /// 就能把整行占满，草稿被完全挤出屏幕——用户按 Ctrl+R 反而看不到自己在搜什么。
    /// 宽度未知（&lt;= 0）时返回固定值（不做猜测性裁剪）；放不下时返回 0，
    /// 调用方应整段省略查询而不是显示一两个残缺字符。</summary>
    internal static int SearchQueryBudget(int windowWidth, int promptWidth, int maxQuery = SearchQueryDisplayWidth)
    {
        if (windowWidth <= 0)
            return maxQuery;
        var avail = windowWidth - promptWidth - SearchRowOverhead - MinSearchDraftWidth;
        return avail <= 0 ? 0 : Math.Min(maxQuery, avail);
    }

    /// <summary>历史浏览态里草稿的最小可读列数：不足这么多列时整段省略并标「…」。</summary>
    internal const int MinHistoryDraftWidth = 6;

    /// <summary>历史浏览态的输入行。历史标记与搜索行的优先级**相反**：
    /// 标记是**导航上下文**（我在第几条、共几条），草稿是**内容**（丢了还能重打）。
    /// 所以标记必须保留，必要时截断草稿并标「…」——绝不能把草稿整段挤出行外，
    /// 否则用户以为自己的输入被清空了。宽度未知时原样拼接（不裁剪）。</summary>
    internal static string FormatHistoryRow(
        string prompt, int historyIndex, int historyCount, string draft,
        int windowWidth, int minDraft = MinHistoryDraftWidth)
    {
        var prefix = $"{prompt} (历史 {historyIndex + 1}/{historyCount})";
        if (windowWidth <= 0)
            return prefix + draft;
        // 标记本身就超宽时改用紧凑形式（标签可省，位置信息不能省）
        if (DisplayWidth(prefix) > windowWidth)
            prefix = $"{prompt} ({historyIndex + 1}/{historyCount})";
        var avail = windowWidth - DisplayWidth(prefix);
        if (avail < minDraft)
            // 极窄到连省略号都放不下时只留标记：数字本身就是信息，宁可超宽也不谎报省略
            return avail >= SafeColor.Glyphs.EllipsisWidth ? prefix + SafeColor.Glyphs.Ellipsis : prefix;
        return prefix + FitToWidth(draft, avail);
    }

    /// <summary>把草稿接到已渲染的前缀后面：按整行预算裁剪，绝不把内容挤出屏幕。
    /// 此前只有历史浏览态（<see cref="FormatHistoryRow"/>）有预算，
    /// 搜索态的四个分支与普通态都直接拼 draft——搜索态前面还多了
    /// 「(搜索) `查询串` ✔/未命中」这一大截前缀，窄屏上必然溢出。
    /// <b>前缀本身就超宽时仍然保留整段草稿</b>：草稿是用户正在编辑的内容，
    /// 悄悄删掉它比超宽危险得多（用户会以为输入被清空了），而且此时行宽反正
    /// 已经超了，删掉草稿也换不回预算。windowWidth &lt;= 0 = 宽度未知，原样拼接。</summary>
    internal static string DraftTail(string prefix, string draft, int windowWidth)
    {
        if (windowWidth <= 0)
            return prefix + draft;
        var avail = windowWidth - DisplayWidth(prefix);
        if (avail <= 0)
            return prefix + draft; // 前缀已占满：保留草稿，不静默丢弃用户输入
        return prefix + FitToWidth(draft, avail);
    }

    /// <summary>
    /// 输入行可见文本：普通态、浏览历史态、反向搜索态的统一拼装。
    /// 搜索无命中时显式提示「未命中」：此前命中与未命中外观完全一致（都只显示 `(搜索)`query``），
    /// 用户按 Ctrl+R 后无法判断是「没匹配到」还是「还没继续按」。
    /// <paramref name="windowWidth"/> 传入时按该宽度收敛查询串/草稿，避免把内容挤出屏幕。
    /// </summary>
    internal static string FormatInputText(
        string prompt, bool searching, string searchQuery, bool searchHit,
        int historyIndex, int historyCount, string draft, int queryWidth = SearchQueryDisplayWidth, int windowWidth = 0,
        string? placeholder = null, bool colored = true)
    {
        // 占位提示只在**普通态 + 空输入**时出现；搜索/浏览历史时输入框有别的含义，
        // 挂一条「试试…」只会误导。
        if (ShouldShowPlaceholder(draft, searching, historyIndex, historyCount))
        {
            var hint = BuildPlaceholder(placeholder, windowWidth, DisplayWidth(prompt), colored);
            if (hint.Length > 0)
                return prompt + hint;
        }
        if (searching)
        {
            if (searchQuery.Length == 0)
                return DraftTail($"{prompt} (搜索) ", draft, windowWidth);
            var budget = windowWidth > 0
                ? SearchQueryBudget(windowWidth, DisplayWidth(prompt), queryWidth)
                : queryWidth;
            // 放不下查询串时整段省略：只显示「未命中/命中」状态，草稿优先占剩余空间
            if (budget <= 0)
                return DraftTail($"{prompt} (搜索) {(searchHit ? SafeColor.Glyphs.Hit : "未命中")} ", draft, windowWidth);
            var query = $"`{FitToWidth(searchQuery, budget)}`";
            var mark = searchHit ? $" {SafeColor.Glyphs.Hit}" : " 未命中";
            return DraftTail($"{prompt} (搜索){query}{mark} ", draft, windowWidth);
        }
        return historyIndex >= 0 && historyIndex < historyCount
            ? FormatHistoryRow(prompt, historyIndex, historyCount, draft, windowWidth)
            : DraftTail(prompt, draft, windowWidth);
    }

    /// <summary>命令目录（名称 + 说明），用于菜单展示与补全。</summary>
    public static readonly (string Name, string Desc)[] Commands =
    [
        ("/help", "显示帮助"),
        ("/clear", "清空对话历史"),
        ("/compact", "压缩历史为摘要"),
        ("/cls", "清屏（或 Ctrl+L）"),
        ("/model", "查看或切换模型"),
        ("/provider", "查看或切换供应商（无需重启）"),
        ("/config", "显示当前配置"),
        ("/session", "显示会话日志路径"),
        ("/setup", "运行供应商配置向导"),
        ("/undo", "撤销最近一次文件修改"),
        ("/diff", "查看最近一次修改的 diff"),
        ("/save", "保存会话命名快照"),
        ("/load", "恢复已保存的会话"),
        ("/resume", "恢复历史会话日志"),
        ("/find", "在历史会话日志中搜索"),
        ("/export", "导出会话为 Markdown"),
        ("/copy", "复制最近回复到剪贴板"),
        ("/prompt", "查看当前生效的系统提示"),
        ("/files", "列出本次会话修改过的文件"),
        ("/stats", "显示 token 用量统计"),
        ("/retry", "重新执行上一条请求"),
        ("/tools", "列出可用工具"),
        ("/providers", "列出已配置的 Provider"),
        ("/models", "列出可用模型（可过滤）"),
        ("/diag", "显示终端环境诊断"),
        ("/history", "显示对话历史（N = 最近 N 条）"),
        ("/thinking", "查看或设置思考强度"),
        ("/shell", "查看或切换命令 shell"),
        ("/mode", "查看或切换工作模式"),
        ("/access", "查看或切换文件访问权限"),
        ("/exit", "退出（同 /quit）"),
        ("/quit", "退出"),
    ];

    private static readonly HistoryStore History = new(Path.Combine(Environment.CurrentDirectory, ".codeagent", "history.txt"));
    private const int MenuMaxRows = 9; // 与 header 的 "1-9 run" 及数字键上限一致

    /// <summary>菜单区固定高度（header + 最多 MenuMaxRows 项 + more 行 + 空行）。
    /// 打开期间高度不变（项不足补空行），过滤/选择/滚动全部原位重绘——
    /// 输入行只在开/关时移动一次，期间屏幕零跳动（Claude Code 式稳定面板）。</summary>
    private const int MenuAreaRows = MenuMaxRows + 3;

    /// <summary>菜单块行数：非空固定为 MenuAreaRows，空态（无匹配）缩为 3 行提示。</summary>
    private static int BlockRows(int count) => count == 0 ? 3 : MenuAreaRows;

    /// <summary>ESC 撤回标记：空输入时按 ESC，由 REPL 拦截执行 UndoLastTurn。</summary>
    public const string RecallMarker = "\u001bRECALL";

    /// <summary>上一次可信的终端宽度（读数抖动时沿用）。</summary>
    private static int _lastGoodWidth;

    /// <summary>输入行可接受的最低可信宽度。**不另立标准**：直接引用
    /// <see cref="Program.MinPlausibleColumns"/>，让"可信宽度"在项目里只有一处定义。
    /// 两处各写一个常数再靠测试断言相等，迟早会被某次修改悄悄改歪，
    /// 而症状是"状态栏敢信、输入行不敢信"这种极难定位的分裂行为。</summary>
    internal const int MinPlausibleWidth = Program.MinPlausibleColumns;

    /// <summary>把一次窗口宽度读数解析成「本轮应使用的宽度」。
    /// 读数低于 <see cref="MinPlausibleWidth"/> 视为失效：输入行会据此决定
    /// 是否走 ANSI 原地重绘、按多宽折行 —— 拿到 1~2 列会让整行渲染全面退化。
    /// 读数正常就用它；都没有时返回 0 = 未知（下游约定：0 表示不裁剪）。</summary>
    internal static int ResolveWindowWidth(int measured, int lastGood)
    {
        if (measured >= MinPlausibleWidth)
            return Math.Clamp(measured, 0, 300);
        return lastGood;
    }

    /// <summary>读取终端宽度；失败或读数不可信时沿用上一次的好值，返回 0 表示未知。</summary>
    private static int TryWindowWidth()
    {
        int measured;
        try { measured = Console.WindowWidth; }
        catch { return _lastGoodWidth; }
        var resolved = ResolveWindowWidth(measured, _lastGoodWidth);
        if (resolved >= MinPlausibleWidth)
            _lastGoodWidth = resolved;
        return resolved;
    }

    /// <summary>ANSI 原地渲染的最低终端宽度：低于此值退回滚动式，避免换行破坏 ANSI 行号计算。</summary>
    internal const int AnsiMinWidth = 30;

    /// <summary>
    /// 是否使用 ANSI 原地渲染。宽度未知（0）时保留原地渲染：行号计算不依赖固定宽度，
    /// 而滚动式会丢掉编辑、历史和补全体验；只有明确过窄或输出被重定向才降级。
    /// </summary>
    internal static bool ShouldUseAnsiInPlace(bool requested, bool outputRedirected, int windowWidth) =>
        requested && !outputRedirected && (windowWidth <= 0 || windowWidth >= AnsiMinWidth);

    /// <summary>输入行可用宽度预算：窗口宽度减 4 列余量，下限 10；宽度未知返回 0（表示不截断）。</summary>
    internal static int FitBudget(int windowWidth) =>
        windowWidth > 0 ? Math.Max(10, windowWidth - 4) : 0;

    /// <summary>
    /// 空输入时显示的占位提示（Claude Code 的 <c>❯ Try "how does &lt;filepath&gt; work?"</c> 同款）。
    /// 用户看不到任何"这里可以做什么"的线索，进去以后面对一个空输入框不知道该说什么。
    /// 默认值用中文，与本 REPL 其余文案一致。
    /// </summary>
    internal const string DefaultPlaceholder = "试试 \"怎么实现 <filepath>\"，或直接描述任务";

    /// <summary>
    /// 按可用宽度预算拼装占位提示：**装不下就整段不显示**，绝不让它折行。
    ///
    /// 占位提示紧跟在提示符后面，折行会把输入块撑成两行，光标定位和多行重绘全部错位
    /// （见 <c>lastInputLines</c>/<c>PositionCursor</c>）。所以宽度是硬约束，不是"尽量"。
    ///
    /// <paramref name="budget"/> 是**已扣过余量的可用宽度**（<see cref="FitBudget"/> 的结果），
    /// 与 <see cref="FormatInputText"/> 的同名字段同一口径——在这里再减一次余量会白白少 4 列。
    ///
    /// 截断时补省略号：提示被截断读起来像"还有更多"，而数值被截断会读成一个
    /// 完整但错误的值——两者对误导的容忍度不同。
    /// </summary>
    internal static string BuildPlaceholder(string? placeholder, int budget, int promptWidth, bool colored)
    {
        if (string.IsNullOrEmpty(placeholder))
            return string.Empty;
        // 预算未知（0）时不猜，直接不显示——猜错会让窄终端多出一行。
        if (budget <= 0)
            return string.Empty;
        var room = budget - promptWidth;
        // 至少留 6 列才值得显示：只剩 3~5 列时显示「试试 …」既没信息又会挤掉用户已输入的内容。
        if (room < 6)
            return string.Empty;
        var text = DisplayWidth(placeholder) <= room
            ? placeholder
            : FitToWidth(placeholder, room);
        if (text.Length == 0)
            return string.Empty;
        if (!colored || !SafeColor.Enabled)
            return text;
        return $"\x1b[90m{text}\x1b[0m";
    }

    /// <summary>
    /// 空输入且无菜单/历史/搜索上下文时才显示占位提示。
    /// 只要用户已经输入了哪怕一个字符（哪怕是空格），立刻消失——残留的提示会让人以为
    /// 那是自己输入的内容。
    /// </summary>
    internal static bool ShouldShowPlaceholder(string draft, bool searching, int historyIndex, int historyCount) =>
        !searching && draft.Length == 0 && !(historyIndex >= 0 && historyIndex < historyCount);

    /// <summary>显示宽度：CJK/全角字符按 2 列计算（与 ConsoleRenderer 一致）；emoji 等代理对按 2 列。</summary>
    private static int DisplayWidth(string s) => TextUtil.DisplayWidth(s);

    /// <summary>
    /// 按显示宽度截断文本（CJK/emoji 按 2 列计），超宽处补省略号。
    /// 曾按字符数截断：中文菜单项/描述按 1 字符算但显示 2 列，导致超宽换行破坏 ANSI 布局。
    /// </summary>
    public static string FitToWidth(string s, int maxWidth)
    {
        if (maxWidth <= 0)
            return string.Empty;
        if (DisplayWidth(s) <= maxWidth)
            return s;
        var sb = new StringBuilder();
        // 省略号自身就放不下时**不要**硬塞：那会让结果比 maxWidth 还宽，
        // 而"超宽"正是这个函数存在的理由要避免的事（ASCII 退回时省略号 3 列，
        // 1~2 列的预算下必然放不下）。此时只填内容、不加标记。
        var dots = SafeColor.Glyphs.Ellipsis;
        var dotsWidth = SafeColor.Glyphs.EllipsisWidth;
        var mark = dotsWidth <= maxWidth ? dots : string.Empty;
        var reserve = mark.Length == 0 ? 0 : dotsWidth;
        int w = 0;
        for (int i = 0; i < s.Length;)
        {
            // 列数与簇的字符数都取自 TextUtil 的唯一规则：零宽字符算 0 列、
            // ZWJ emoji 序列整体算 2 列。此前这里内联了一份旧口径，
            // 与 DisplayWidth 不一致——既提前截断浪费列宽，又会把序列劈成半个代理对。
            var (cw, chars) = TextUtil.ClusterAt(s, i);
            if (cw == 0)
            {
                sb.Append(s, i, chars); // 零宽字符不占列，跟随前面的簇一起保留
                i += chars;
                continue;
            }
            // 预留的是省略号**实际的显示列数**，不是写死的 1：
            // ASCII 退回时省略号从 1 列变成 3 列，写死 1 会让每一条截断结果都超宽 2 列。
            if (w + cw + reserve > maxWidth)  // CWK(2) 塞不下则放弃该字符
                break;
            sb.Append(s, i, chars);
            w += cw;
            i += chars;
        }
        return sb.ToString() + mark;
    }

    /// <summary>按显示宽度补空格到指定列宽（CJK/emoji 按 2 列计）；超宽则按显示宽度截断并补省略号。</summary>
    internal static string PadToDisplayWidth(string s, int width)
    {
        var current = DisplayWidth(s);
        if (current == width)
            return s;
        return current < width ? s + new string(' ', width - current) : FitToWidth(s, width);
    }

    /// <summary>描述列的最小可读宽度：不足这么多列时宁可整列省略，
    /// 也不要切出几个看起来像内容的碎片（误导性的残片比没有信息更糟）。</summary>
    internal const int MinMenuDescWidth = 6;

    /// <summary>
    /// 窄屏下的菜单行：**名称列优先**。此前整行一起截断，窄终端下名称列被切掉右补白，
    /// 描述列随之整体错位——<see cref="FormatMenuLine"/> 刻意做的对齐（表格感）就没了。
    /// 现在保证名称列完整对齐，描述列拿剩余宽度；剩余不足 <see cref="MinMenuDescWidth"/> 列时整列省略。
    /// 预算不足以放下编号+名称时返回空串（调用方应隐藏该行，而不是画出半截）。
    /// </summary>
    internal static string FitMenuLine(string name, string desc, int budget, bool modePicker, int visibleRow, int nameWidth = MaxMenuNameWidth)
    {
        if (budget <= 0)
            return string.Empty;
        var num = modePicker ? "  " : $"  {visibleRow + 1}) ";
        var numWidth = DisplayWidth(num);
        // 名称列 + 至少一个分隔空格
        var nameBudget = Math.Min(nameWidth, budget - numWidth - 1);
        if (nameBudget <= 0)
            return string.Empty;
        var head = num + PadToDisplayWidth(FitToWidth(name, nameBudget), nameBudget) + " ";
        var remain = budget - DisplayWidth(head);
        if (remain < MinMenuDescWidth)
            return head.TrimEnd() + " " + SafeColor.Glyphs.Ellipsis;
        return head + FitToWidth(desc, remain);
    }

    /// <summary>名称列的最小/最大宽度：小于下限会让最短的名称也显得拥挤，
    /// 大于上限则把很长的名称整列截断（描述才是用户要读的内容）。</summary>
    internal const int MinMenuNameWidth = 4;
    internal const int MaxMenuNameWidth = 16;

    /// <summary>按菜单里**实际最长的名称**定名称列宽（而不是固定 16 列）。
    /// 此前固定 16：模式菜单的名称都很短（plan/auto），每行白白空出十几列，
    /// 描述列被推到很靠右，宽终端上有一大片空白。宽度不足时再由
    /// <see cref="FitMenuLine"/> 按预算收敛，所以这里只负责「够宽就贴合」。</summary>
    internal static int MenuNameWidth(IEnumerable<string> names, int cap = MaxMenuNameWidth)
    {
        var longest = 0;
        foreach (var name in names)
        {
            var w = DisplayWidth(name);
            if (w > longest)
                longest = w;
        }
        return Math.Clamp(longest, MinMenuNameWidth, cap);
    }

    /// <summary>
    /// 菜单行文本：命令菜单带 1-9 编号（数字键可执行）；模式菜单无编号（数字键是普通输入）。
    /// 名称列按显示宽度对齐而非字符数：中文命令名按 1 字符计却占 2 列，用 char 补齐会让描述列整体错位。
    /// </summary>
    internal static string FormatMenuLine(string name, string desc, int visibleRow, bool modePicker, int nameWidth = MaxMenuNameWidth) =>
        (modePicker ? "  " : $"  {visibleRow + 1}) ") + PadToDisplayWidth(name, nameWidth) + " " + desc;

    /// <summary>
    /// 光标定位偏移：把终端光标从行尾移到 <paramref name="cursor"/> 处需要左移的列数。
    /// 用显示宽度计算（CJK 占 2 列），避免中文行内编辑时光标错位。
    /// </summary>
    public static int CursorLeftOffset(string text, int cursor)
    {
        cursor = Math.Clamp(cursor, 0, text.Length);
        return DisplayWidth(text) - DisplayWidth(text[..cursor]);
    }

    /// <summary>
    /// 光标右移 n 列的 ANSI 序列。
    /// **n = 0 必须返回空串**：ECMA-48 规定 CSI 的参数省略或为 0 都按 1 处理，
    /// 所以 `\x1b[0C` 会把光标**右移 1 列**而不是原地不动。空提示符 + 空内容
    /// （或整行只有零宽字符）时 col 就是 0，直接拼 `\x1b[{col}C` 必然偏一列。
    /// n = 1 用一个空格即可（所有终端都认），不必发转义序列。</summary>
    internal static string CursorForward(int n) =>
        n <= 0 ? string.Empty : n == 1 ? " " : $"\x1b[{n}C";

    /// <summary>
    /// 当前渲染里占位提示占的**显示列数**（未显示时为 0）。
    ///
    /// 光标必须停在占位提示**之前**：重绘把提示写在输入文本后面，不减掉这几列的话
    /// 光标会落在提示末尾，看上去像「这句提示是用户自己打的」。
    /// 刻意用 colored:false 量宽度——ANSI 转义序列不占列，按含转义的串量会算多。
    /// </summary>
    internal static int RenderedPlaceholderWidth(
        string draft, bool searching, int historyIndex, int historyCount,
        string? placeholder, int budget, int promptWidth) =>
        ShouldShowPlaceholder(draft, searching, historyIndex, historyCount)
            ? TextUtil.DisplayWidth(BuildPlaceholder(placeholder, budget, promptWidth, colored: false))
            : 0;

    /// <summary>
    /// 输入行里的 <c>@文件</c> 引用（学自 Claude Code）：<c>@src/Program.cs</c> 把文件
    /// 塞进提示词，省得手打长路径。
    ///
    /// 判定规则刻意保守——只有**前面是空白或行首**的 <c>@</c> 才算数：
    /// <c>foo@bar</c>、<c>git@github.com</c> 里都有 @，把它们当引用会弹出文件菜单，
    /// 用户莫名其妙。宁可少认，不可乱认。
    ///
    /// 只看光标**之前**的文本：光标不在这个 token 里就不激活，
    /// 否则打完 "@src" 又跑回去改前面几个字，菜单会莫名其妙地跟着跳。
    /// </summary>
    internal static (bool Active, string Prefix, int Start) ParseMention(string text, int cursor)
    {
        if (string.IsNullOrEmpty(text))
            return (false, string.Empty, -1);
        var upto = cursor <= 0 ? 0 : Math.Min(cursor, text.Length);
        var at = -1;
        for (var i = upto - 1; i >= 0; i--)
        {
            var ch = text[i];
            if (ch == '@')
            {
                at = i;
                break;
            }
            // token 只能由路径字符组成：碰到空白就说明 @ 那段已经结束
            if (ch == ' ' || ch == '\t' || ch == '\n')
                return (false, string.Empty, -1);
        }
        if (at < 0)
            return (false, string.Empty, -1);
        // @ 必须在行首或空白之后（邮箱、git@host 不算引用）
        if (at > 0)
        {
            var prev = text[at - 1];
            if (prev != ' ' && prev != '\t' && prev != '\n')
                return (false, string.Empty, -1);
        }
        return (true, text[(at + 1)..upto], at);
    }

    /// <summary>选中某个文件后替换整个 token 的文本（@ 到下一个空白为止）。</summary>
    internal static string ApplyMention(string text, int start, string replacement)
    {
        var end = start;
        while (end < text.Length && text[end] != ' ' && text[end] != '\t' && text[end] != '\n')
            end++;
        return text[..start] + replacement + text[end..];
    }

    /// <summary>
    /// 工作区里与前缀匹配的文件（按路径显示，供 @ 菜单挑选）。
    /// 只按**路径后缀**匹配：用户打的是 <c>Prog</c>，要能命中 <c>src/CodeAgent/Program.cs</c>——
    /// 按文件名匹配就得先打完整个 basename，@ 的价值就没有了。
    /// </summary>
    internal static List<(string Name, string Desc)> MentionItems(
        string root, string prefix, int limit, CancellationToken ct)
    {
        var items = new List<(string Name, string Desc)>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return items;
        foreach (var file in SkipDirs.EnumerateFilesPruned(root, 8, includeIgnored: false, ct: ct, followSymlinks: false))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (prefix.Length == 0)
            {
                items.Add((rel, ""));
                continue;
            }
            // 大小写不敏感的后缀匹配；命中位置越靠后（越接近文件名）越相关
            var at = rel.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
                items.Add((rel, at.ToString("D6")));
        }
        // 前缀越长、命中越靠后 → 越靠前
        items.Sort((a, b) =>
        {
            var ka = int.Parse(a.Desc.Length == 0 ? "999999" : a.Desc);
            var kb = int.Parse(b.Desc.Length == 0 ? "999999" : b.Desc);
            return ka != kb ? ka.CompareTo(kb) : string.CompareOrdinal(a.Name, b.Name);
        });
        return items.Take(Math.Max(1, limit)).ToList();
    }

    /// <summary>
    /// 一段文本在给定列宽下占**多少个终端行**——显式换行与自动折行都要算。
    ///
    /// \x1b[2K 只清**一行**。此前重绘块行数只数文本里的显式 \n，
    /// 完全没算终端的自动折行：输入敲过列宽后屏幕已经占了 2 行，
    /// 重绘却按 1 行处理，第一行被清掉、第二行留着上一帧的残字，
    /// 光标列也超出行宽。提示符超宽折行和这里是同一个 bug 的两半。
    ///
    /// <paramref name="promptWidth"/> 是提示符占的列数——它在**同一行**上，
    /// 会把后面内容的可用宽度挤掉（10 列提示 + 15 列内容 = 25 列，占 2 行）。
    /// <paramref name="columns"/> 为 0（宽度未知）时只数显式换行：宁可少算，
    /// 也不猜一个可能清错行的数。
    /// </summary>
    internal static int RenderedRows(string text, int columns, int promptWidth)
    {
        var explicitRows = text.TrimEnd('\n').Split('\n');
        if (columns <= 0)
            return explicitRows.Length;
        var rows = 0;
        foreach (var line in explicitRows)
        {
            var w = DisplayWidth(line) + (rows == 0 ? promptWidth : 0);
            // 恰好等于列宽不折行：终端光标停在最后一列，要再多一个字符才换行
            rows += w <= columns ? 1 : (w + columns - 1) / columns;
        }
        return Math.Max(rows, 1);
    }

    /// <summary>
    /// 光标在重绘块内的**行号**（0-based）与块的**总行数**。
    ///
    /// 清行数对了折行还不够：<c>PositionCursor</c> 若只数显式 \n，
    /// 敲到超过列宽时光标所在行算少，上移行数就不对，光标落在错误的行上——
    /// 屏幕看起来像"光标自己跳走了"。
    ///
    /// <paramref name="blockPrefix"/> 是提示符（与内容同在第 0 行），
    /// 所以传给 <see cref="RenderedRows"/> 的 promptWidth 是 0：它已经在串里了。
    /// </summary>
    internal static (int Row, int Rows) CursorRowInBlock(string blockPrefix, string text, int cursor, int columns)
    {
        var combined = blockPrefix + text;
        var rows = RenderedRows(combined, columns, 0);
        var offset = Math.Clamp(cursor, 0, text.Length) + blockPrefix.Length;
        var row = RenderedRows(combined[..Math.Clamp(offset, 0, combined.Length)], columns, 0) - 1;
        return (Math.Clamp(row, 0, Math.Max(0, rows - 1)), rows);
    }

    /// <summary>
    /// ANSI 原地重绘要写出的**完整字节序列**（不含光标定位，那步要读 buf.Cursor）。
    ///
    /// 抽成纯函数是为了能断言「敲一个字符不新增行」这条不变量：用户报上来的现象
    /// 正是每敲一个字屏幕就多出一行。此前这段逻辑内联在 ScrollInput 里，只能靠肉眼看
    /// 终端，任何改动都无法回归验证。
    ///
    /// <paramref name="newLastInputLines"/> 是重绘后块的行数，调用方据此更新基线。
    /// </summary>
    internal static string BuildRedrawAnsi(string text, int lastInputLines, int lastCursorLine, out int newLastInputLines, int columns = 0, int promptWidth = 0)
    {
        var lines = RenderedRows(text, columns, promptWidth);
        newLastInputLines = lines;
        // 单行：一行清 + 一行写，**不产生任何 \n**。
        // 边框（chrome）不在 text 里，所以这里的单行判定不会被提示符的多行外观污染。
        if (lines <= 1 && lastInputLines <= 1)
            return "\r\x1b[2K" + text;

        // 多行输入（粘贴含换行）：上移到块首后逐行 \x1b[2K 清整行重写。
        // 不依赖 \x1b[J（清屏到末尾）——部分终端对 ED 支持不佳，导致每次重绘
        // 向下追加旧块、刷屏。行数取新旧最大值，多余的旧行清空。
        // 上移基点用 lastCursorLine（终端光标当前行）而非块末尾：
        // 删除文本后光标被 PositionCursor 放在中间行，从中间行上移 rows-1
        // 到不了块首，会覆盖错位、提示符行残留重复。
        var rows = Math.Max(lines, lastInputLines);
        var sb = new StringBuilder();
        // lastCursorLine 为 0 时必须省略 CUU：多数终端（xterm/Windows Terminal/conhost）
        // 把参数 0 按 1 处理，"\x1b[0A" 会真的上移一行，覆盖掉输入块上方的提示符行
        // 同一条规则对光标右移（C）同样成立，见 CursorForward。
        if (lastCursorLine > 0)
            sb.Append($"\x1b[{lastCursorLine}A");
        var textLines = DiffUtil.SplitLines(text);
        for (var i = 0; i < rows; i++)
        {
            sb.Append("\r\x1b[2K");
            if (i < textLines.Length)
                sb.Append(textLines[i]);
            if (i < rows - 1)
                sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>读取一行输入；EOF（重定向输入关闭）时返回 null。modes 用于 Alt+M 模式菜单，ansi 控制菜单渲染方式，initial 为预填文本（取消回合后回填草稿）。</summary>
    public static string? Read(string prompt, IReadOnlyList<(string Name, string Desc)>? modes = null, bool ansi = true, string? initial = null)
        => Read(prompt, modes, ansi, initial, DefaultPlaceholder);

    /// <summary>读取一行输入。placeholder 为 null/空串时不显示占位提示。mentionRoot 非空时启用 @ 文件引用。</summary>
    public static string? Read(string prompt, IReadOnlyList<(string Name, string Desc)>? modes = null, bool ansi = true, string? initial = null, string? placeholder = null, string? mentionRoot = null)
    {
        var root = mentionRoot ?? string.Empty;
        if (Console.IsInputRedirected)
        {
            Console.Write(prompt);
            var line = Console.ReadLine();
            if (line is null)
                return null;
            Remember(line);
            return line;
        }

        var buf = new EditableLine();
        buf.SetInitial(initial); // 预填：取消回合后把上一条输入恢复到输入框
        var session = History.Entries.ToList();
        var idx = session.Count;
        var draft = (string?)null; // 浏览历史前的原始输入草稿（↓ 回到底部时恢复）
        var promptPlain = prompt.TrimStart('\n');
        // 提示符可能占多行（BuildInputFrameTop 的「上边框 + 提示符」）。光标列只能按
        // **最后一行**的宽度算——按整段算会把上边框那几十列也加进去，光标被推到行外，
        // 终端折行后又和下一次重绘叠加，表现为「敲一个字就多出一行」。
        var promptTail = promptPlain[(promptPlain.LastIndexOf('\n') + 1)..];
        var searching = false;      // Ctrl+R 反向搜索模式：输入进 query，输入行显示命中的历史条目
        var searchQuery = new StringBuilder();
        var searchFrom = -1;        // 当前命中的 session 下标（-1 = 无命中）

        // 宽度相关量必须先于 InputText 声明：搜索行要按 fitBudget 收敛查询串（CS0841）
        var winW = TryWindowWidth();
        var ansiOk = ShouldUseAnsiInPlace(ansi, Console.IsOutputRedirected, winW);
        if (ansiOk)
            BracketedPaste.Enable(); // 粘贴边界标记（详见 BracketedPaste 注释）；重定向/窄终端不启用
        var fitBudget = FitBudget(winW);
        string Fit(string s) => fitBudget > 0 ? FitToWidth(s, fitBudget) : s;

        // 菜单行用名称列优先的裁剪（整行 Fit 会破坏名称列对齐，描述列整体错位）。
        // 名称列宽按**实际菜单内容**定，不写死 16：短名称时不必空出十几列。
        var menuNameWidth = MenuNameWidth(modes?.Select(m => m.Name) ?? Array.Empty<string>());
        string FitMenu(string name, string desc, bool mode, int row) =>
            fitBudget > 0
                ? FitMenuLine(name, desc, fitBudget, mode, row, menuNameWidth)
                : FormatMenuLine(name, desc, row, mode, menuNameWidth);

        // 输入行文本：浏览命令历史（↑/↓）时附带位置提示「(历史 N/M）」；
        // Ctrl+R 反向搜索时展示查询串与命中状态（搜索无命中显式提示「未命中」）
        //
        // 关键：用 **promptTail（提示符最后一行）**，不是 promptPlain（整段）。
        // 提示符可能占多行（BuildInputFrameTop 的「上边框 + 提示符」），而重绘是
        // 「\r\x1b[2K + text」——清行只作用一行。带上边框就等于**每次按键都把边框
        // 再打一遍**，多出来的行往下堆，表现为「敲一个字就多出一行 ◈ high · /thinking」。
        // 边框属于 chrome，首次绘制一次即可，不该参与重绘。
        string InputText() =>
            FormatInputText(promptTail, searching, searchQuery.ToString(), searchFrom >= 0, idx, session.Count, buf.Text,
                SearchQueryDisplayWidth, fitBudget, placeholder, ansiOk);

        var menuOpen = false;
        var modePicker = false;
        var menuItems = new List<(string Name, string Desc)>();
        var menuIndex = -1;
        var lastFilter = "";
        var menuShown = 0;   // 可见项数
        var menuOffset = 0;  // 可见窗口在列表中的起点
        var menuRows = 0;    // 已绘制的菜单块行数（擦除用）
        var menuListShown = false;   // 滚动模式下完整列表是否已打印（之后过滤变化只打单行）
        var lastInputLines = 1;      // 上次绘制的输入块行数（多行重绘逐行清残留用）
        var lastCursorLine = 0;      // 终端光标当前所在行（上次重绘后 PositionCursor 放置处，多行重绘上移基点）
        var inputExpanded = false;   // 用户是否展开过折叠的多行输入（展开后不再自动折叠）
        // @ 引用状态：(token 起点, 前缀)。Start >= 0 表示菜单当前列的是文件。
        // 用 (start, prefix) 而不是 bool：选中时要知道把哪一段替换掉。
        var mention = (-1, (string?)null);
        const int MentionLimit = 200;

        Console.Write(prompt);
        // 提示符可能占多行（BuildInputFrameTop 返回「上边框 + 提示符」）。但**边框是 chrome，
        // 不属于重绘块**：它只画一次，之后 InputText() 只重画提示符的最后一行。
        //   · lastInputLines 必须是「提示符最后一行 + 输入文本各行」的行数，
        //     绝不能把边框算进去——ScrollInput 里 `lastInputLines > 1` 会走多行分支，
        //     那样单行输入每次按键都多写一个 \n（正是「敲一个字就多出一行」）。
        //   · lastCursorLine 是光标在**重绘块内**的行号，第 0 行就是提示符那一行。
        if (!string.IsNullOrEmpty(initial))
        {
            Console.Write(initial); // 预填文本也要显示出来
            // 预填可能多行（取消回合回填的多行草稿）：重绘基线必须按实际行数初始化，
            // 否则首个按键重绘时 lastInputLines=1 / lastCursorLine=0，会在预填块下方再画一份重复块
            lastInputLines = 1 + CountNewlines(initial);
            lastCursorLine = CountNewlines(initial); // 光标停在预填末尾（末行）
        }
        else
        {
            // 首次进入就显示占位提示：等用户敲第一个键才出现的话，那时已经没意义了。
            // 首个按键的 RedrawInput 会用 \r\x1b[2K 整行擦掉它，不会残留。
            var initialHint = BuildPlaceholder(placeholder, fitBudget, DisplayWidth(promptTail), ansiOk);
            if (initialHint.Length > 0)
                Console.Write(initialHint);
        }

        // —— 绘制助手（局部函数） ——

        int MenuAbove() => menuRows + DisplayedNewlines(inputExpanded, buf.Text); // 菜单块 + 输入块总行数（光标到块顶的行距，折叠感知）

        string Header() => modePicker
            ? "  Modes (up/down select, Enter switch, Esc close):"
            : $"  Commands (1-9 run, up/down select, {SafeColor.Glyphs.Arrow} fill, {SafeColor.Glyphs.Enter} run, {SafeColor.Glyphs.Escape} close):";

        int CountNewlines(string s)
        {
            // 去掉末尾换行再计数：与 SkipDirs.CountLines / DiffUtil.SplitLines 语义一致，
            // 避免 "a\nb\n" 被当成 3 行导致 PositionCursor 把光标行算到折叠提示行上。
            s = s.TrimEnd('\n');
            int n = 0;
            foreach (var c in s)
                if (c == '\n')
                    n++;
            return n;
        }

        /// <summary>光标所在行距输入块首行多少行（多行输入时菜单锚点计算用）。</summary>
        int CursorLineInBlock() =>
            DisplayedCursorLine(inputExpanded, buf.Text, buf.Cursor); // 折叠时第 2 行及以后都显示在折叠行

        /// <summary>输入块的显示文本：与 ScrollInput 同口径（未展开且 &gt;3 行时折叠）。
        /// 菜单重绘也必须画折叠视图——画原始多行会把块高从 3 行撑回 N 行，与菜单定位的行数口径不符。</summary>
        string DisplayedInputText() =>
            !inputExpanded && SkipDirs.CountLines(buf.Text) > FoldThreshold
                ? FoldText(InputText(), FoldThreshold, fitBudget)
                : InputText();

        void ScrollInput()
        {
            if (ansiOk)
            {
                // 折叠显示：行数 > FoldThreshold 且未展开时，只显示前 N-1 行 + 折叠提示行（减少屏幕占用）
                var fold = !inputExpanded && SkipDirs.CountLines(buf.Text) > FoldThreshold;
                var text = fold ? InputLine.FoldText(InputText()) : InputText();
                // 传入真实列宽：文本敲过列宽时终端会自动折行，
                // 而 \x1b[2K 只清一行——不把折行算进块行数，残字会留在屏幕上
                Console.Write(BuildRedrawAnsi(
                    text, lastInputLines, lastCursorLine, out lastInputLines, winW, DisplayWidth(promptTail)));
                PositionCursor(fold);
                // 记录重绘后终端光标所在行（PositionCursor 已把光标放到 buf.Cursor 处，
                // 折叠时按折叠视图行计），作为下次多行重绘的上移基点。
                // 与 PositionCursor 同口径：折行也算进去，否则下一次上移的基点是错的。
                var (rawCursorLine, _) = CursorRowInBlock(promptTail, buf.Text, buf.Cursor, winW);
                lastCursorLine = fold ? Math.Min(rawCursorLine, 2) : rawCursorLine;
            }
            else
            {
                // 滚动模式无 ANSI 无法原地擦除：多行时只写一次（双写会加倍刷屏），
                // 单行保持原逻辑（\r 重写 + 空格清残留）
                if (1 + CountNewlines(buf.Text) > 1)
                    Console.Write("\r" + InputText());
                else
                {
                    Console.Write("\r" + InputText() + new string(' ', 4));
                    Console.Write("\r" + InputText());
                }
            }
        }

        /// <summary>把终端光标移到 buf.Cursor 对应的列（行内编辑的视觉反馈，支持多行输入与折叠视图）。</summary>
        void PositionCursor(bool folded)
        {
            var cursor = Math.Clamp(buf.Cursor, 0, buf.Text.Length);
            var upTo = buf.Text[..cursor];
            // 行号必须把**终端自动折行**算进去：只数显式 \n 时，敲到超过列宽
            // 光标所在行算少，上移的行数就不对，光标落到错误的行上。
            var (cursorLine, totalLines) = CursorRowInBlock(promptTail, buf.Text, cursor, winW);
            if (folded)
            {
                // 折叠视图：前 2 行 + 折叠行（⏷ 共 N 行）；buf 行 >= 2 都显示在折叠行
                var dispLine = cursorLine < 2 ? cursorLine : 2;
                var up = 3 - 1 - dispLine; // 从块末尾（折叠行末尾）上移到光标显示行
                if (up > 0)
                {
                    string seg;
                    int col;
                    if (dispLine == 2)
                    {
                        var foldLines = DiffUtil.SplitLines(InputLine.FoldText(InputText()));
                        seg = foldLines.Length > 2 ? foldLines[2] : ""; // 折叠行文本，光标显示在末尾
                        col = DisplayWidth(seg); // 折叠行独立成行，无提示符前缀
                    }
                    else
                    {
                        seg = cursorLine == 0 ? upTo : upTo[(upTo.LastIndexOf('\n') + 1)..];
                        // 显示行首是提示符：光标列 = 提示符宽 + 行内内容宽（少算提示符会偏到其左侧）
                        col = DisplayWidth(promptTail) + DisplayWidth(seg);
                    }
                    Console.Write($"\x1b[{up}A\r{CursorForward(col)}");
                }
                // up == 0：光标已在折叠行末尾（重绘后块末尾即折叠行末尾），无需移动
                return;
            }
            var up2 = totalLines - 1 - cursorLine; // 从块末尾（末行行尾）上移到光标行
            if (up2 > 0)
            {
                // 上移不改变列：回到列 1 后右移到光标行的行内偏移。显示行首是提示符
                // （首行带前缀、其余行各自成行），光标列 = 提示符宽 + 该行到 cursor 的内容宽
                var seg = cursorLine == 0 ? upTo : upTo[(upTo.LastIndexOf('\n') + 1)..];
                Console.Write($"\x1b[{up2}A\r{CursorForward(DisplayWidth(promptTail) + DisplayWidth(seg))}");
            }
            else
            {
                // 光标在最后一行：从行尾左移到 cursor（单行输入或光标在末行）
                // 占位提示写在行尾，光标要从它**前面**停住，否则看起来像用户输入的一部分
                var placeholderCols = RenderedPlaceholderWidth(
                    buf.Text, searching, idx, session.Count, placeholder, fitBudget, DisplayWidth(promptTail));
                var offset = CursorLeftOffset(buf.Text, buf.Cursor) + placeholderCols;
                if (offset > 0)
                    Console.Write($"\x1b[{offset}D");
            }
        }

        void RedrawInput()
        {
            // 统一走 ScrollInput：菜单打开时它只重绘输入行（菜单块在上方不动），
            // 多行粘贴用逐行覆盖（\x1b[2K 清整行），避免 \x1b[J 在部分终端无效导致刷屏
            ScrollInput();
        }

        // —— ANSI 原地渲染（默认） ——
        void PrintListAnsi()
        {
            menuShown = Math.Min(menuItems.Count, MenuMaxRows); // 数字键可见窗口（与布局高度解耦）
            if (menuOffset > menuItems.Count - menuShown)
                menuOffset = Math.Max(0, menuItems.Count - menuShown);
            var rows = BlockRows(menuItems.Count);
            var sb = new StringBuilder();
            // 菜单块绘制在输入行正上方（块空间已由 ResizeMenuSpace 用 IL/DL 腾出），
            // 输入行被推到块下方；终端在窗口底部自动滚动，无需逐行重推、无输出放大。
            // 多行输入：块顶在光标行上方 rows + (显示行数-1) 处（折叠视图只占 3 行）
            sb.Append($"\x1b[{rows + DisplayedNewlines(inputExpanded, buf.Text)}A\x1b[1G");
            sb.AppendLine(Fit(Header()) + "\x1b[K");
            if (menuItems.Count == 0)
            {
                sb.AppendLine(Fit("  (no matching item, press Esc to close)") + "\x1b[K");
                sb.AppendLine("\x1b[K"); // 占位到 3 行高，与 BlockRows 一致
            }
            else
            {
                // 固定高度项区：始终画 MenuMaxRows 行，项不足补空行（高度稳定，无跳动）
                for (int i = 0; i < MenuMaxRows; i++)
                {
                    var k = menuOffset + i;
                    if (k >= menuItems.Count)
                    {
                        sb.AppendLine("\x1b[K");
                        continue;
                    }
                    var line = FitMenu(menuItems[k].Name, menuItems[k].Desc, modePicker, i);
                    // 预算不足以放下编号+名称：整行清空（与「本行无内容」同一处理，保持块高稳定）
                    if (line.Length == 0)
                    {
                        sb.AppendLine("\x1b[K");
                        continue;
                    }
                    // 选中项：反显高亮（\x1b[7m）；行尾 \x1b[K 清残留
                    sb.AppendLine((k == menuIndex ? "\x1b[7m" + line + "\x1b[0m" : line) + "\x1b[K");
                }
                // more 计数随窗口滚动更新（下方剩余项数）
                var remaining = menuItems.Count - menuOffset - MenuMaxRows;
                sb.AppendLine(Fit(remaining > 0 ? $"  ... (+{remaining} more)" : "") + "\x1b[K");
            }
            sb.AppendLine(); // 空行；末尾换行后光标已在输入行
            sb.Append("\x1b[1G");
            sb.Append(DisplayedInputText());
            sb.Append("\x1b[K");
            menuRows = rows;
            Console.Write(sb.ToString());
        }

        /// <summary>菜单行文本：命令菜单带 1-9 编号；模式菜单无编号（数字键是普通输入）。
        /// 名称列取**实际最长的菜单项**，短名称的菜单不再空出十几列。</summary>
        string MenuLineText(int listIndex, int visibleRow) =>
            FormatMenuLine(
                menuItems[listIndex].Name,
                menuItems[listIndex].Desc,
                visibleRow,
                modePicker,
                MenuNameWidth(menuItems.Select(m => m.Name)));

        // 关闭菜单块（rows 行）：整块删除（DL），输入行上移回到原位，屏幕不留残影
        void EraseMenuAnsi(int rows)
        {
            var sb = new StringBuilder();
            if (rows > 0)
                // 上移到块顶整块删除；多行输入时块顶在光标行上方 rows + (显示行数-1) 处（折叠感知，与绘制口径一致）
                sb.Append($"\x1b[{rows + DisplayedNewlines(inputExpanded, buf.Text)}A\x1b[{rows}M");
            sb.Append("\x1b[1G");
            sb.Append(DisplayedInputText());
            sb.Append("\x1b[K");
            menuRows = 0;
            Console.Write(sb.ToString());
        }

        void MoveSelectionAnsi(int oldIndex, int newIndex)
        {
            if (newIndex < menuOffset || newIndex >= menuOffset + menuShown)
            {
                // 窗口滚动：显式更新偏移，原地重绘整个块（行高不变，直接覆盖旧内容）
                menuOffset = newIndex < menuOffset
                    ? newIndex
                    : Math.Max(0, newIndex - menuShown + 1);
                menuOffset = Math.Min(menuOffset, Math.Max(0, menuItems.Count - menuShown));
                PrintListAnsi();
                return;
            }
            // 整段拼接后单次写入：擦旧行 → 写新行 → 回到输入行
            var sb = new StringBuilder();
            if (oldIndex >= menuOffset && oldIndex < menuOffset + menuShown && oldIndex != newIndex)
            {
                var up = MenuAbove() - 1 - (oldIndex - menuOffset);
                sb.Append($"\x1b[{up}A\x1b[1G\x1b[K");
                sb.Append(Fit(MenuLineText(oldIndex, oldIndex - menuOffset)));
                sb.Append($"\x1b[{up}B");
            }
            var up2 = MenuAbove() - 1 - (newIndex - menuOffset);
            sb.Append($"\x1b[{up2}A\x1b[1G\x1b[K");
            sb.Append("\x1b[7m" + Fit(MenuLineText(newIndex, newIndex - menuOffset)) + "\x1b[0m");
            sb.Append($"\x1b[{up2}B\x1b[1G");
            sb.Append(DisplayedInputText());
            sb.Append("\x1b[K");
            Console.Write(sb.ToString());
        }

        // —— 滚动式渲染（tuiAnsi=false 时的兜底） ——
        void PrintListScroll()
        {
            menuListShown = true;
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine(Fit(Header()));
            if (menuItems.Count == 0)
            {
                // 过滤无匹配时显示提示而非回退到全部命令（与 PrintFilterScroll 一致）
                sb.AppendLine(Fit("  (no matching item, press Esc to close)"));
            }
            else
            {
                // 紧凑展示：一行列出当前过滤结果（避免 14 行大块渲染导致卡顿）
                var names = menuItems.Select(m => m.Name);
                sb.AppendLine(Fit("  " + string.Join(" ", names)));
            }
            sb.AppendLine();
            sb.Append(InputText());
            Console.Write(sb.ToString());
        }

        // 过滤变化时单行显示带编号的匹配结果（可直接按数字执行）
        void PrintFilterScroll()
        {
            var sb = new StringBuilder();
            if (menuItems.Count == 0)
            {
                sb.AppendLine(Fit("  (no matching item, press Esc to close)"));
            }
            else
            {
                // 与数字键上限一致（1-9 run）：显示前 9 项，超出提示总数
                var parts = menuItems.Take(9).Select((m, i) => $"{i + 1}) {m.Name}");
                var more = menuItems.Count > 9 ? $" {SafeColor.Glyphs.Ellipsis}(共 {menuItems.Count} 项)" : "";
                sb.AppendLine(Fit($"  [{buf}] {string.Join(" ", parts)}{more}"));
            }
            sb.AppendLine();
            sb.Append(InputText());
            Console.Write(sb.ToString());
        }

        void MoveSelectionScroll(int newIndex)
        {
            if (menuItems.Count == 0)
                return;
            menuIndex = newIndex;
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine(Fit($"  {SafeColor.Glyphs.Arrow} {menuItems[menuIndex].Name,-16} {menuItems[menuIndex].Desc}"));
            sb.Append(InputText());
            Console.Write(sb.ToString());
        }

        void PrintMenu()
        {
            if (ansiOk)
                PrintListAnsi();
            else
                PrintListScroll();
        }

        /// <summary>调整菜单块高度（menuRows → newAbove）：在块顶插入/删除行差（IL/DL 序列）。
        /// 多行输入时光标可能停在块中间的行：所有定位都以「输入块首行」为锚，
        /// 菜单始终插在整块上方，不切入输入文本行之间。</summary>
        void ResizeMenuSpace(int newAbove)
        {
            if (newAbove == menuRows)
                return;
            var aboveInput = CursorLineInBlock();             // 光标 → 输入块首行的行距
            var up = aboveInput + menuRows;                   // 光标 → 菜单块顶（首次开菜单时 menuRows=0）
            if (up > 0)
                Console.Write($"\x1b[{up}A");
            Console.Write(newAbove > menuRows
                ? $"\x1b[{newAbove - menuRows}L"              // 扩高：块顶插入空行，下方内容（含输入块）下移
                : $"\x1b[{menuRows - newAbove}M");            // 缩高：块顶删除行差，下方内容上移
            Console.Write($"\x1b[{aboveInput + newAbove}B");  // 回到（移动后的）光标行
            menuRows = newAbove;
        }

        void RefreshMenu()
        {
            // @ 引用：光标停在 @token 里时，菜单列的是工作区文件而不是命令。
            // 与 / 命令菜单互斥——两者的触发字符不可能同时成立。
            var (mentionActive, mentionPrefix, mentionStart) = ParseMention(buf.Text, buf.Cursor);
            mention = mentionActive ? (mentionStart, mentionPrefix) : (-1, null);
            List<(string Name, string Desc)> newItems;
            if (mentionActive)
            {
                newItems = MentionItems(root, mentionPrefix, MentionLimit, CancellationToken.None);
            }
            else
            {
                var src = modePicker ? modes ?? [] : Commands;
                newItems = src
                    .Where(m => m.Name.StartsWith(NormalizeCommandFilter(buf.Text), StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            // 仅当过滤结果真的变化时才重绘，避免 /m→/mo→/mod 每个按键都刷一块菜单
            var same = newItems.Count == menuItems.Count &&
                       newItems.Zip(menuItems, (a, b) => a.Name == b.Name).All(x => x);
            if (!same)
            {
                menuItems = newItems;
                if (menuIndex >= menuItems.Count)
                    menuIndex = menuItems.Count - 1;
                if (ansiOk)
                {
                    // 固定面板：仅「空态 ↔ 非空」切换时调整高度（输入行只在此刻移动一次），
                    // 其余过滤/滚动全部原位重绘，屏幕零跳动
                    var target = BlockRows(menuItems.Count);
                    if (target != menuRows)
                        ResizeMenuSpace(target);
                    PrintListAnsi();
                }
                else if (!menuListShown)
                {
                    PrintListScroll(); // 打开：完整编号列表
                }
                else
                {
                    PrintFilterScroll(); // 过滤变化：单行匹配结果
                }
            }
            else
            {
                // 过滤结果未变（/mo→/mod 仍是同一批命令）：菜单块不重绘，
                // 但输入行必须重画——曾在此直接跳过绘制，导致按下的字符不上屏、
                // 屏幕停在旧输入（/m），直到过滤结果变化（如补一个空格）才一次性刷新
                RedrawInput();
            }
            lastFilter = mentionActive ? mentionPrefix : NormalizeCommandFilter(buf.Text);
        }

        void OpenMenu(bool picker)
        {
            if (menuOpen)
                return;
            menuOpen = true;
            modePicker = picker;
            menuIndex = -1;
            // 打开即按目标高度一次性腾出空间（Claude Code 式稳定面板）：
            // 命令菜单按满高预留（过滤从全量开始），之后高度只在空态边界变
            if (ansiOk)
            {
                var initial = picker ? (modes?.Count ?? 0) : Commands.Length;
                ResizeMenuSpace(BlockRows(initial));
            }
            if (picker)
            {
                menuItems = [.. modes ?? []];
                PrintMenu();
                lastFilter = "/";
            }
            else
            {
                RefreshMenu();
            }
        }

        void CloseMenu()
        {
            menuOpen = false;
            menuItems.Clear();
            menuIndex = -1;
            modePicker = false; // 必须重置，否则后续 / 命令菜单永远打不开
            if (ansiOk)
            {
                EraseMenuAnsi(menuRows);
            }
            menuListShown = false;
        }

        void MoveSelection(int newIndex)
        {
            if (menuItems.Count == 0)
                return;
            var oldIndex = menuIndex;
            menuIndex = newIndex;
            if (ansiOk)
                MoveSelectionAnsi(oldIndex, newIndex);
            else
                MoveSelectionScroll(newIndex);
        }

        void OnTextChanged()
        {
            if (menuOpen && !modePicker && !SlashLike(buf.Text))
            {
                // 输入不再以斜杠开头（如退格删掉 /）：关闭并擦除菜单
                CloseMenu();
                RedrawInput();
                return;
            }
            if (menuOpen && !modePicker)
            {
                var pat = NormalizeCommandFilter(buf.Text);
                if (pat != lastFilter)
                    RefreshMenu();
                else
                    RedrawInput();
            }
            else
            {
                RedrawInput();
            }
        }

        // —— 主输入循环 ——
        var pending = new Queue<ConsoleKeyInfo>(); // 暂存键队列（标记探测误吞时放回、CRLF 携带键等）
        var keySw = new System.Diagnostics.Stopwatch();
        var pasteStream = false; // 最近一次 ReadKey 等待 < 阈值 → 键已缓冲（无括号粘贴终端的回退启发式）
        var pasteActive = false; // 括号粘贴中（终端显式标记了粘贴边界）：Enter/Tab 都是内容
        var lastPasteWasCR = false; // 粘贴中上一个换行来自 \r（CRLF 的 \n 折叠用）
        var pasteEndedWithNewline = false; // 粘贴刚结束时缓冲区末尾有 \n（避免 Enter 重复插入）
        var needsRedraw = false; // 挂起的重绘：粘贴流中逐键整行重画会卡顿，缓冲排空后一次画
        while (true)
        {
            // 粘贴流排空：把挂起的重绘补上（仍键入中则继续攒）
            if (needsRedraw && !Console.KeyAvailable && !pasteActive)
            {
                needsRedraw = false;
                OnTextChanged();
            }
            ConsoleKeyInfo key;
            if (pending.Count > 0)
            {
                key = pending.Dequeue(); // 暂存键可能来自粘贴流，保持 pasteStream
            }
            else
            {
                keySw.Restart();
                key = Console.ReadKey(intercept: true);
                keySw.Stop();
                // 键几乎立即返回（<30ms）说明是缓冲中的粘贴流；手动输入的键间隔通常更慢
                pasteStream = keySw.ElapsedMilliseconds < 30;
            }

            // 括号粘贴标记 ESC[200~/ESC[201~：使能 ESC[?2004h 的终端在粘贴内容外注入边界，
            // 换行判定从计时启发式变成确定性标记（分批注入时 \n 曾被误判为真人按 Enter，
            // 半截草稿被提交）。非标记（用户单按 ESC）时把已消费键放回队列，走正常 ESC 处理
            if (key.Key == ConsoleKey.Escape)
            {
                var detector = new PasteMarkerDetector();
                detector.Feed('\x1b');
                var consumed = new List<ConsoleKeyInfo>();
                var waited = 0;
                while (detector.InProgress)
                {
                    if (!Console.KeyAvailable)
                    {
                        if (waited >= 10)
                            break; // 单按 ESC：等了 10ms 无后续，不是标记
                        System.Threading.Thread.Sleep(2);
                        waited += 2;
                        continue;
                    }
                    var mk = Console.ReadKey(intercept: true);
                    consumed.Add(mk);
                    detector.Feed(mk.KeyChar);
                }
                if (detector.Result == PasteMarkerResult.Start)
                {
                    pasteActive = true;
                    lastPasteWasCR = false;
                    continue;
                }
                if (detector.Result == PasteMarkerResult.End)
                {
                    pasteActive = false;
                    pasteEndedWithNewline = buf.Text.Length > 0 && buf.Text[^1] == '\n';
                    if (needsRedraw)
                    {
                        needsRedraw = false;
                        OnTextChanged();
                    }
                    continue;
                }
                // 不是标记：已消费键按到达顺序放回（先到先出），ESC 本身继续正常处理
                foreach (var ck in consumed)
                    pending.Enqueue(ck);
            }
            if (key.Key != ConsoleKey.Enter)
            {
                lastPasteWasCR = false; // 非换行键到达：下一个 \n 不再是 CRLF 的尾半
                pasteEndedWithNewline = false;
            }

            // 命令菜单：输入不再以斜杠开头 → 关闭；模式选择器不受输入影响
            if (menuOpen && !modePicker && !SlashLike(buf.ToString()))
                CloseMenu();

            switch (key.Key)
            {
                // Shift+Enter：插入换行（手动多行输入）。必须排在无修饰符的 Enter 分支之前；
                // Alt+Enter 不用——conhost 里它是全屏切换，会误导
                case ConsoleKey.Enter when (key.Modifiers & ConsoleModifiers.Shift) != 0:
                    searching = false;
                    buf.Insert((char)10); // Shift+Enter 插入换行
                    if (pasteActive)
                        lastPasteWasCR = false; // 明确换行：中断 CRLF 折叠链
                    break;
                case ConsoleKey.Enter:
                    searching = false; // 搜索结束：提交当前命中的历史条目
                    if (pasteActive)
                    {
                        // 括号粘贴中的换行一律是内容：不提交、不选菜单。
                        // CRLF 成对到达：\r 插入，紧跟的 \n 折叠掉；LF-only 各自插入（空行不塌）
                        if (TryConsumePasteEnter(ref lastPasteWasCR, key.KeyChar))
                            break;
                        buf.Insert('\n');
                        if (Console.KeyAvailable || pending.Count > 0 || pasteActive)
                            needsRedraw = true;
                        else
                            OnTextChanged();
                        break;
                    }
                    // 粘贴刚结束时缓冲区末尾已有 \n：Enter 不再重复插入（避免内容尾换行被翻倍），
                    // 但仍应提交当前输入——break 会导致需要按两次 Enter，体验错误
                    if (pasteEndedWithNewline)
                        pasteEndedWithNewline = false;
                    if (menuOpen && menuItems.Count > 0 && menuIndex >= 0)
                    {
                        var sel = menuItems[menuIndex].Name;
                        CloseMenu();
                        Console.WriteLine();
                        var submit = modePicker ? $"/mode {sel}" : sel;
                        Remember(submit);
                        return submit;
                    }
                    if (menuOpen)
                    {
                        // 菜单打开但未选中任何项：关闭菜单，按原输入提交。
                        // 必须优先于粘贴检测——否则快速输入时 Enter 会被误判为粘贴插入 \n（逻辑冲突）
                        CloseMenu();
                        Console.WriteLine();
                        var raw = buf.Text;
                        Remember(raw);
                        return raw;
                    }
                    // 粘贴多行内容：键快速连续到达（粘贴流）时，换行是内容的一部分，插入而非提交。
                    // Windows 终端粘贴是分批注入，首个 \r 到达时后续字符可能尚未进入缓冲区，
                    // 仅靠 KeyAvailable 不可靠，需结合 ReadKey 等待时间（<30ms=粘贴流）判定。
                    if (pasteStream)
                    {
                        // 与括号粘贴分支一致：CRLF 的 \r 后紧跟的 \n 应被折叠而非插入
                        if (TryConsumePasteEnter(ref lastPasteWasCR, key.KeyChar))
                            break;
                        buf.Insert('\n');
                        lastPasteWasCR = key.KeyChar == '\r';
                        // 仅当本次是 \r（CRLF 首段）时才等待并折叠紧随的 \n；
                        // 纯 LF 粘贴不折叠下一行的 Enter，避免多行粘贴丢换行
                        if (lastPasteWasCR)
                        {
                            if (!Console.KeyAvailable)
                                System.Threading.Thread.Sleep(5);
                            if (Console.KeyAvailable)
                            {
                                var next = Console.ReadKey(intercept: true);
                                pending.Enqueue(next);
                            }
                        }
                        OnTextChanged();
                        break;
                    }
                    if (menuOpen)
                        CloseMenu(); // 未选择任何项：关闭菜单，按原输入提交
                    Console.WriteLine();
                    var line = buf.Text;
                    Remember(line);
                    return line;

                case ConsoleKey.Backspace when pasteActive:
                case ConsoleKey.Delete when pasteActive:
                    break; // 粘贴流中的控制键是内容的一部分（罕见），不当作编辑命令误删已插入文本

                case ConsoleKey.Backspace:
                    if (searching)
                    {
                        // 搜索模式退格：删 query 末字符并重新跳到最新命中
                        if (searchQuery.Length > 0)
                        {
                            searchQuery.Remove(searchQuery.Length - 1, 1);
                            searchFrom = FindHistoryMatch(session, searchQuery.ToString(), session.Count - 1);
                            if (searchFrom >= 0)
                            {
                                SetBuf(session, buf, searchFrom);
                                inputExpanded = false;
                            }
                            RedrawInput();
                        }
                        break;
                    }
                    if (menuOpen && modePicker)
                        CloseMenu();
                    if ((key.Modifiers & ConsoleModifiers.Control) != 0)
                    {
                        if (buf.DeleteWordBackward()) // Ctrl+Backspace：删前一个单词
                            OnTextChanged();
                        break;
                    }
                    if (buf.Backspace())
                        OnTextChanged();
                    break;

                case ConsoleKey.LeftArrow:
                    if (!menuOpen)
                    {
                        if ((key.Modifiers & ConsoleModifiers.Control) != 0)
                            buf.MoveWordLeft(); // Ctrl+←：按单词移动
                        else
                            buf.MoveLeft();
                        RedrawInput();
                    }
                    break;
                case ConsoleKey.RightArrow:
                    if (menuOpen && !modePicker && menuItems.Count > 0 && (key.Modifiers & ConsoleModifiers.Control) == 0)
                    {
                        // → ：把选中的命令填充到输入行（不执行），可继续编辑/加参数；
                        // 无选中时默认填第一项（顶部项即隐式高亮）。Tab 在多匹配时是循环换选，
                        // → 是「就要这个」——补全后关菜单，回车执行或继续输入
                        var picked = menuItems[menuIndex >= 0 ? menuIndex : 0].Name;
                        // @ 引用只替换 @token 那一段，整行其余内容（"看这个 … 谢谢"）必须留下
                        buf.Replace(mention.Item1 >= 0
                            ? ApplyMention(buf.Text, mention.Item1, picked)
                            : picked);
                        draft = null;
                        CloseMenu();
                        RedrawInput();
                    }
                    else if (!menuOpen)
                    {
                        if ((key.Modifiers & ConsoleModifiers.Control) != 0)
                            buf.MoveWordRight(); // Ctrl+→：按单词移动
                        else
                            buf.MoveRight();
                        RedrawInput();
                    }
                    break;
                case ConsoleKey.Home:
                    if (!menuOpen)
                    {
                        buf.LineHome(); // 多行输入：Home 到当前行行首
                        RedrawInput();
                    }
                    break;

                case ConsoleKey.End:
                    if (!menuOpen)
                    {
                        buf.LineEnd();
                        RedrawInput();
                    }
                    break;

                case ConsoleKey.Delete:
                    // 与 Backspace 一致：命令菜单打开时也应删字符并刷新过滤（曾因 !menuOpen 守卫被完全忽略）
                    if (menuOpen && modePicker)
                        CloseMenu();
                    if ((key.Modifiers & ConsoleModifiers.Control) != 0)
                    {
                        if (buf.DeleteWordForward()) // Ctrl+Delete：删光标处一个单词
                            OnTextChanged();
                        break;
                    }
                    if (buf.Delete())
                        OnTextChanged();
                    break;
                case ConsoleKey.UpArrow:
                    searching = false; // 方向键退出搜索，回到普通历史浏览
                    if (menuOpen && menuItems.Count > 0)
                    {
                        MoveSelection(menuIndex < 0 ? menuItems.Count - 1 : (menuIndex - 1 + menuItems.Count) % menuItems.Count);
                    }
                    else if (idx >= session.Count && 1 + CountNewlines(buf.Text) > 1)
                    {
                        // 多行输入且未在浏览历史：↑ 只做行内上移（已在首行行首则不移动）。
                        // 正在浏览历史时（idx < session.Count）必须继续回溯历史，
                        // 即使历史条目是多行文本——否则多行条目会把 ↑ 劫持成光标移动（回归）
                        if (buf.MoveLineUp())
                            RedrawInput();
                    }
                    else
                    {
                        if (menuOpen)
                            break; // 0 匹配的菜单：保持空态提示，不关闭也不浏览历史（Esc 或继续输入关闭）
                        if (idx > 0)
                        {
                            if (draft is null)
                                draft = buf.Text; // 记住浏览历史前的草稿
                            idx--;
                            SetBuf(session, buf, idx);
                            inputExpanded = false; // 新载入的内容重新按行数决定折叠
                            RedrawInput();
                        }
                    }
                    break;

                case ConsoleKey.DownArrow:
                    searching = false; // 方向键退出搜索，回到普通历史浏览
                    if (menuOpen && menuItems.Count > 0)
                    {
                        MoveSelection(menuIndex < 0 ? 0 : (menuIndex + 1) % menuItems.Count);
                    }
                    else if (idx >= session.Count && 1 + CountNewlines(buf.Text) > 1)
                    {
                        // 多行输入且未在浏览历史：↓ 在行内下移光标（不切换历史——历史切换会替换整个输入）。
                        // 正在浏览历史时（idx < session.Count）↓ 一律前进历史，即使条目是多行文本
                        if (!inputExpanded && SkipDirs.CountLines(buf.Text) > 3)
                            inputExpanded = true; // 折叠中按 ↓：先展开（显示全部行），光标保持当前行
                        else
                            buf.MoveLineDown();
                        RedrawInput();
                    }
                    else
                    {
                        if (menuOpen)
                            break; // 0 匹配的菜单：保持空态提示，不关闭也不浏览历史（Esc 或继续输入关闭）
                        if (idx < session.Count)
                        {
                            idx++;
                            if (idx == session.Count && draft is not null)
                            {
                                // 回到草稿（浏览历史前的原始输入）
                                buf.Replace(draft);
                                draft = null;
                                inputExpanded = false; // 回到草稿同样重新按行数决定折叠
                            }
                            else
                            {
                                SetBuf(session, buf, idx);
                                inputExpanded = false;
                            }
                            RedrawInput();
                        }
                    }
                    break;

                case ConsoleKey.Tab when (key.Modifiers & ConsoleModifiers.Shift) != 0:
                    // Shift+Tab：菜单内（多项时）反向循环选择；菜单外或菜单仅 1 项时切换文件访问权限模式
                    // （strict → whitelist → full → strict，由 /access next 处理并显示）。
                    // 仅 1 项时循环无意义，切换权限才是用户意图，避免按键静默失效
                    if (menuOpen && menuItems.Count > 1)
                    {
                        MoveSelection(menuIndex < 0 ? 0 : (menuIndex - 1 + menuItems.Count) % menuItems.Count);
                    }
                    else
                    {
                        CloseMenu();
                        Console.WriteLine();
                        Remember("/access next");
                        return "/access next"; // Shift+Tab：切换文件访问权限模式
                    }
                    break;

                case ConsoleKey.Tab when pasteActive:
                    buf.Insert('\t'); // 粘贴的 Tab 是内容（缩进代码），不触发补全/模式切换
                    lastPasteWasCR = false;
                    needsRedraw = true;
                    break;

                case ConsoleKey.Tab:
                    if (menuOpen && menuItems.Count == 1)
                    {
                        // 唯一匹配：Tab 补全为完整命令（/think + Tab → /thinking）
                        // @ 引用只替换 @token 那一段
                        var only = menuItems[0].Name;
                        buf.Replace(mention.Item1 >= 0 ? ApplyMention(buf.Text, mention.Item1, only) : only);
                        draft = null;
                        CloseMenu();
                        RedrawInput();
                    }
                    else if (!menuOpen && SlashLike(buf.Text))
                    {
                        OpenMenu(false);
                    }
                    else if (!menuOpen && ParseMention(buf.Text, buf.Cursor).Active)
                    {
                        OpenMenu(false); // @ 引用：Tab 打开文件菜单
                    }
                    else if (menuOpen && menuItems.Count > 1)
                    {
                        // 多匹配：先把输入补到候选公共前缀（bash 式），补不动时才循环高亮
                        var typed = NormalizeCommandFilter(buf.Text);
                        var filled = TabCompletion(menuItems.Select(m => m.Name).ToList(), typed);
                        if (filled is not null && filled.Length > typed.Length)
                        {
                            buf.Replace(filled);
                            draft = null;
                            menuIndex = -1;
                            RefreshMenu(); // 候选集随之收窄，重绘菜单
                        }
                        else
                        {
                            MoveSelection(menuIndex < 0 ? 0 : (menuIndex + 1) % menuItems.Count);
                        }
                    }
                    else if (!menuOpen)
                    {
                        // 菜单未开且输入不以 / 开头：Tab 切换下一个工作模式（/mode next）。
                        // 以 / 开头时命中上面的 OpenMenu 分支打开命令菜单，两者不冲突
                        Console.WriteLine();
                        Remember("/mode next");
                        return "/mode next"; // Tab：切换工作模式
                    }
                    break;

                case ConsoleKey.D1 or ConsoleKey.D2 or ConsoleKey.D3 or ConsoleKey.D4 or ConsoleKey.D5
                    or ConsoleKey.D6 or ConsoleKey.D7 or ConsoleKey.D8 or ConsoleKey.D9:
                    // 命令菜单：数字键直接执行（1-9）；模式菜单不拦截数字（关闭并按普通输入，避免误触发切换）。
                    // 输入已完整匹配某命令（如 /model 或全角 ／model）时数字视为参数输入，不再劫持
                    //（要执行直接按 Enter）
                    if (menuOpen && !modePicker && menuItems.Count > 0
                        && !menuItems.Any(m => m.Name.Equals(NormalizeCommandFilter(buf.Text), StringComparison.OrdinalIgnoreCase)))
                    {
                        var n = key.Key - ConsoleKey.D1 + 1;
                        // 只对可见窗口内的项生效：菜单滚动后窗口外（如第 9 项）不可见，不应被数字键触发
                        var selIdx = DigitKeySelection(n, menuOffset, menuShown, menuItems.Count);
                        if (selIdx >= 0)
                        {
                            var sel = menuItems[selIdx].Name;
                            CloseMenu();
                            Console.WriteLine();
                            Remember(sel);
                            return sel;
                        }
                        break;
                    }
                    if (menuOpen && modePicker)
                        CloseMenu(); // 模式菜单：数字按普通输入处理
                    if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                    {
                        buf.Insert(key.KeyChar);
                        draft = null;
                        RedrawInput();
                    }
                    break;

                case ConsoleKey.Escape:
                    if (searching)
                    {
                        // 退出搜索：保留当前命中的文本，继续编辑
                        searching = false;
                        RedrawInput();
                        break;
                    }
                    if (menuOpen)
                    {
                        CloseMenu();
                        if (!ansiOk)
                        {
                            Console.WriteLine("  (menu closed)");
                            Console.Write(InputText());
                        }
                    }
                    else if (buf.Length > 0)
                    {
                        // ESC：清空当前输入
                        buf.Clear();
                        draft = null;
                        inputExpanded = false; // 输入已清空：恢复自动折叠，之后粘贴长文本仍折叠
                        RedrawInput();
                    }
                    else
                    {
                        // ESC（空输入）：撤回最后一条已发送的消息。
                        // 二次确认防误触——连按 Esc 本会从"关菜单/清输入"一路滑到"撤回"（有副作用）
                        var confirm = promptPlain + "(再按 Esc 撤回上一条消息，其他键继续)";
                        if (ansiOk)
                            Console.Write("\r\x1b[2K" + confirm);
                        else
                            Console.Write("\r" + confirm);
                        var confirmKey = Console.ReadKey(intercept: true);
                        if (confirmKey.Key != ConsoleKey.Escape)
                        {
                            // 取消确认：恢复输入行，按键交给主循环继续处理
                            pending.Enqueue(confirmKey);
                            RedrawInput();
                            break;
                        }
                        if (ansiOk)
                            Console.Write("\r\x1b[2K");
                        Console.WriteLine();
                        return RecallMarker;
                    }
                    break;

                case ConsoleKey.M when IsShortcut(key) && modes is { Count: > 0 }:
                    OpenMenu(true); // Alt+M / Ctrl+Shift+M：模式选择菜单
                    break;

                case ConsoleKey.U when IsShortcut(key) && !menuOpen:
                    CloseMenu();
                    Console.WriteLine();
                    Remember("/undo");
                    return "/undo"; // Alt+U / Ctrl+Shift+U：撤销最近一次修改（菜单打开时不触发，避免过滤输入时误触）

                case ConsoleKey.D when IsShortcut(key) && !menuOpen:
                    CloseMenu();
                    Console.WriteLine();
                    Remember("/diff");
                    return "/diff"; // Alt+D / Ctrl+Shift+D：查看最近修改的 diff（菜单打开时不触发）

                case ConsoleKey.N when IsShortcut(key) && !menuOpen:
                    CloseMenu();
                    Console.WriteLine();
                    Remember("/clear");
                    return "/clear"; // Alt+N / Ctrl+Shift+N：新建会话（清空历史）（菜单打开时不触发）

                case ConsoleKey.R when (key.Modifiers & ConsoleModifiers.Control) != 0 && !menuOpen:
                    // Ctrl+R：反向搜索历史（bash 式）。进入搜索；再按跳到更早的下一个命中
                    if (!searching)
                    {
                        searching = true;
                        searchQuery.Clear();
                        searchFrom = -1;
                        RedrawInput();
                    }
                    else
                    {
                        var next = FindHistoryMatch(session, searchQuery.ToString(), searchFrom - 1);
                        if (next >= 0)
                        {
                            searchFrom = next;
                            SetBuf(session, buf, next);
                            inputExpanded = false;
                            RedrawInput();
                        }
                    }
                    break;

                case ConsoleKey.L when (key.Modifiers & ConsoleModifiers.Control) != 0:
                    try { Console.Clear(); } catch { /* 忽略 */ }
                    // 清屏后菜单内容已消失：RefreshMenu 在过滤未变时短路不重绘，
                    // 必须用 PrintMenu 强制整体重绘（菜单 + 输入行）
                    if (menuOpen)
                        PrintMenu();
                    else
                        RedrawInput();
                    break;

                default:
                    if (key.KeyChar != '\0' && key.KeyChar != '\u0003' && !char.IsControl(key.KeyChar))
                    {
                        // 搜索模式：可打印字符进 query 并跳到最新命中（bash 语义），不进输入缓冲
                        if (searching)
                        {
                            searchQuery.Append(key.KeyChar);
                            searchFrom = FindHistoryMatch(session, searchQuery.ToString(), session.Count - 1);
                            if (searchFrom >= 0)
                            {
                                SetBuf(session, buf, searchFrom);
                                inputExpanded = false;
                            }
                            RedrawInput();
                            break;
                        }
                        if (menuOpen && modePicker)
                            CloseMenu();
                        buf.Insert(key.KeyChar);
                        draft = null; // 输入使草稿失效
                        lastPasteWasCR = false;
                        pasteEndedWithNewline = false;
                        // 粘贴流中不逐键整行重绘（大粘贴会卡顿/闪烁）：挂起，缓冲排空或粘贴结束时一次画
                        if (Console.KeyAvailable || pending.Count > 0 || pasteActive)
                            needsRedraw = true;
                        else
                            OnTextChanged();
                        // 任何斜杠输入都弹菜单：无匹配块本身是打字状态的实时反馈（打错字可见），
                        // 菜单常驻到 ESC/Enter 或输入脱离斜杠才关闭
                        if (!modePicker && SlashLike(buf.Text) && !menuOpen)
                            OpenMenu(false);
                    }
                    break;
            }
        }
    }

    private static void SetBuf(List<string> session, EditableLine buf, int idx)
    {
        buf.Replace(idx < session.Count ? session[idx] : "");
    }

    /// <summary>快捷键判定：Alt+键 或 Ctrl+Shift+键（部分终端会吞 Alt，提供 Ctrl+Shift 兜底）。</summary>
    private static bool IsShortcut(ConsoleKeyInfo key)
    {
        var m = key.Modifiers;
        return (m & ConsoleModifiers.Alt) != 0 ||
               ((m & ConsoleModifiers.Control) != 0 && (m & ConsoleModifiers.Shift) != 0);
    }

    /// <summary>输入是否以斜杠开头（兼容中文输入法的全角 ／）。</summary>
    private static bool SlashLike(string s) => s.StartsWith('/') || s.StartsWith('／');

    /// <summary>归一化命令过滤串：前导全角 ／ 转 /（中文输入法兼容）。
    /// 过滤与「数字键视为参数」的完整匹配判定都必须用它，直接用原始文本会漏掉全角输入。</summary>
    internal static string NormalizeCommandFilter(string text) =>
        text.StartsWith('／') ? "/" + text[1..] : text;

    /// <summary>候选集的公共前缀（逐字符比较，长度不超过最短候选；无候选或无公共前缀时返回空串）。</summary>
    internal static string CommonPrefix(IReadOnlyList<string> candidates)
    {
        if (candidates.Count == 0)
            return string.Empty;
        var shortest = candidates.MinBy(c => c.Length) ?? string.Empty;
        var prefix = shortest;
        foreach (var candidate in candidates)
        {
            var i = 0;
            while (i < prefix.Length && i < candidate.Length && prefix[i] == candidate[i])
                i++;
            prefix = prefix[..i];
            if (prefix.Length == 0)
                break;
        }
        return prefix;
    }

    /// <summary>
    /// Tab 补全决策（bash 式）：
    /// 唯一匹配 → 补全为该命令；多匹配 → 补到候选公共前缀（能推进才补，补不动返回 null，由调用方循环高亮）。
    /// 旧实现在多匹配时只高亮第 1 项，输入文本原地不动：用户看不出「还能补到哪」，得连按 Tab 试。
    /// </summary>
    internal static string? TabCompletion(IReadOnlyList<string> candidates, string typed)
    {
        if (candidates.Count == 0)
            return null;
        if (candidates.Count == 1)
            return candidates[0];
        var prefix = CommonPrefix(candidates);
        return prefix.Length > typed.Length ? prefix : null;
    }

    /// <summary>
    /// 数字键 1-9 在命令菜单中对应的列表下标。
    /// 仅对「可见窗口内」的项生效（ANSI 模式 menuShown 为窗口大小；滚动模式 menuShown=0 时
    /// 以数字键上限 9 为可见数，与 PrintFilterScroll 显示项数一致）；
    /// 无效（越界/窗口外）返回 -1，调用方忽略该按键。
    /// </summary>
    internal static int DigitKeySelection(int n, int menuOffset, int menuShown, int menuCount)
    {
        var visible = menuShown > 0 ? menuShown : Math.Min(menuCount, 9); // 滚动模式：最多 9（数字键上限）
        if (n < 1 || n > visible || menuCount <= 0)
            return -1;
        var idx = menuOffset + n - 1;
        return idx < menuCount ? idx : -1;
    }

    /// <summary>反向搜索历史：从 fromIndex 向更旧方向找第一个包含 query（忽略大小写）的条目；无命中返回 -1。
    /// Ctrl+R 进入搜索（query 空 → 不命中），再按 Ctrl+R 跳更早的下一个命中。</summary>
    internal static int FindHistoryMatch(IReadOnlyList<string> history, string query, int fromIndex)
    {
        if (query.Length == 0 || history.Count == 0)
            return -1;
        var normalizedQuery = NormalizeCommandFilter(query);
        for (var i = Math.Min(fromIndex, history.Count - 1); i >= 0; i--)
        {
            if (NormalizeCommandFilter(history[i]).Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>输入块的显示换行数（= 显示行数 - 1，即从块末行上移到块首的行距）。
    /// 折叠视图（未展开且 &gt;3 行）只显示「前 2 行 + 折叠提示行」共 3 行，显示换行数是 2——
    /// 菜单定位必须按显示行数算：按原始换行数上移会越过输入块顶，菜单画到历史输出上（屏幕错位）。</summary>
    internal static int DisplayedNewlines(bool expanded, string text)
    {
        var raw = 0;
        foreach (var c in text)
            if (c == '\n')
                raw++;
        return !expanded && raw + 1 > FoldThreshold ? FoldThreshold - 1 : raw;
    }

    /// <summary>
    /// 粘贴中 Enter 键的 CRLF 折叠决策：\r 后紧跟 \n 时把 \n 折叠掉并复位状态。
    /// </summary>
    /// <param name="lastPasteWasCR">上一次粘贴换行是否为 \r。</param>
    /// <param name="keyChar">当前键字符。</param>
    /// <returns>true 表示该 \n 被 CRLF 折叠，调用方不应再插入换行。</returns>
    internal static bool TryConsumePasteEnter(ref bool lastPasteWasCR, char keyChar)
    {
        if (keyChar == '\n' && lastPasteWasCR)
        {
            lastPasteWasCR = false;
            return true;
        }
        lastPasteWasCR = keyChar == '\r';
        return false;
    }

    /// <summary>光标在输入块内的显示行（0-based，块首为 0）：折叠时第 2 行及以后都落在折叠行上（=2）。
    /// ResizeMenuSpace 的上移距离用它，与 ScrollInput 里 fold 感知的 lastCursorLine 口径一致。</summary>
    internal static int DisplayedCursorLine(bool expanded, string text, int cursor)
    {
        cursor = Math.Clamp(cursor, 0, text.Length);
        var n = 0;
        for (int i = 0; i < cursor; i++)
            if (text[i] == '\n')
                n++;
        return !expanded && n + 1 > 3 ? Math.Min(n, 2) : n;
    }

    /// <summary>记录一条输入到历史（委托给 HistoryStore）。</summary>
    private static void Remember(string line) => History.Remember(line);
}
