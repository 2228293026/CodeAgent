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
    /// <summary>
    /// 折叠长文本为「前 threshold-1 行 + 折叠提示行」：行数超过 threshold 时折叠，否则原样。
    /// 折叠提示行显示总行数（⏷ 共 N 行 ↓ 展开），供多行输入框减少屏幕占用。
    /// </summary>
    internal static string FoldText(string text, int threshold = 3)
    {
        var lines = text.Split('\n');
        if (lines.Length <= threshold)
            return text;
        return string.Join('\n', lines.Take(threshold - 1)) + "\n⏷ 共 " + lines.Length + " 行 ↓ 展开";
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

    /// <summary>读取终端宽度；失败返回 0（未知）。</summary>
    private static int TryWindowWidth()
    {
        try { return Math.Clamp(Console.WindowWidth, 0, 300); } catch { return 0; }
    }

    /// <summary>显示宽度：CJK/全角字符按 2 列计算（与 ConsoleRenderer 一致）；emoji 等代理对按 2 列。</summary>
    private static int DisplayWidth(string s) => TextUtil.DisplayWidth(s);

    /// <summary>
    /// 按显示宽度截断文本（CJK/emoji 按 2 列计），超宽处补省略号。
    /// 曾按字符数截断：中文菜单项/描述按 1 字符算但显示 2 列，导致超宽换行破坏 ANSI 布局。
    /// </summary>
    public static string FitToWidth(string s, int maxWidth)
    {
        if (DisplayWidth(s) <= maxWidth)
            return s;
        var sb = new StringBuilder();
        int w = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            int cw;
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                cw = 2;
                if (w + cw > maxWidth - 1)
                    break; // 预留省略号的一列
                sb.Append(c);
                sb.Append(s[i + 1]);
                i++;
            }
            else
            {
                cw = !char.IsSurrogate(c) && c > 0x2E7F ? 2 : 1; // 孤立代理按 1 列（与 DisplayWidth 口径一致）
                if (w + cw > maxWidth - 1)
                    break; // 预留省略号的一列
                sb.Append(c);
            }
            w += cw;
        }
        return sb.ToString() + "…";
    }

    /// <summary>
    /// 光标定位偏移：把终端光标从行尾移到 <paramref name="cursor"/> 处需要左移的列数。
    /// 用显示宽度计算（CJK 占 2 列），避免中文行内编辑时光标错位。
    /// </summary>
    public static int CursorLeftOffset(string text, int cursor)
    {
        cursor = Math.Clamp(cursor, 0, text.Length);
        return DisplayWidth(text) - DisplayWidth(text[..cursor]);
    }

    /// <summary>读取一行输入；EOF（重定向输入关闭）时返回 null。modes 用于 Alt+M 模式菜单，ansi 控制菜单渲染方式，initial 为预填文本（取消回合后回填草稿）。</summary>
    public static string? Read(string prompt, IReadOnlyList<(string Name, string Desc)>? modes = null, bool ansi = true, string? initial = null)
    {
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
        var searching = false;      // Ctrl+R 反向搜索模式：输入进 query，输入行显示命中的历史条目
        var searchQuery = new StringBuilder();
        var searchFrom = -1;        // 当前命中的 session 下标（-1 = 无命中）

        // 输入行文本：浏览命令历史（↑/↓）时附带位置提示「(历史 N/M)」
        string InputText() =>
            searching
                ? $"{promptPlain} (搜索)`{searchQuery}` {buf.Text}"
                : idx < session.Count
                    ? $"{promptPlain} (历史 {idx + 1}/{session.Count}){buf.Text}"
                    : promptPlain + buf.Text;

        var winW = TryWindowWidth();
        var ansiOk = ansi && winW >= 30; // 宽度未知或太窄时退回滚动式，避免换行破坏 ANSI 行号计算
        if (ansiOk)
            BracketedPaste.Enable(); // 粘贴边界标记（详见 BracketedPaste 注释）；重定向/窄终端不启用
        string Fit(string s) => FitToWidth(s, Math.Max(10, winW - 4));

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

        Console.Write(prompt);
        if (!string.IsNullOrEmpty(initial))
        {
            Console.Write(initial); // 预填文本也要显示出来
            // 预填可能多行（取消回合回填的多行草稿）：重绘基线必须按实际行数初始化，
            // 否则首个按键重绘时 lastInputLines=1 / lastCursorLine=0，会在预填块下方再画一份重复块
            lastInputLines = 1 + CountNewlines(initial);
            lastCursorLine = CountNewlines(initial); // 光标停在预填末尾（末行）
        }

        // —— 绘制助手（局部函数） ——

        int MenuAbove() => menuRows + DisplayedNewlines(inputExpanded, buf.Text); // 菜单块 + 输入块总行数（光标到块顶的行距，折叠感知）

        string Header() => modePicker
            ? "  Modes (up/down select, Enter switch, Esc close):"
            : "  Commands (1-9 run, up/down select, → fill, Enter run, Esc close):";

        int CountNewlines(string s)
        {
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
            !inputExpanded && 1 + CountNewlines(buf.Text) > 3 ? FoldText(InputText()) : InputText();

        void ScrollInput()
        {
            if (ansiOk)
            {
                // 折叠显示：行数 > 3 且未展开时，只显示前 2 行 + 折叠提示行（减少屏幕占用）
                var fold = !inputExpanded && 1 + CountNewlines(buf.Text) > 3;
                var text = fold ? InputLine.FoldText(InputText()) : InputText();
                var lines = 1 + CountNewlines(text); // 显示块总行数（折叠时 = 3）
                if (lines > 1 || lastInputLines > 1)
                {
                    // 多行输入（粘贴含换行）：上移到块首后逐行 \x1b[2K 清整行重写。
                    // 不依赖 \x1b[J（清屏到末尾）——部分终端对 ED 支持不佳，导致每次重绘
                    // 向下追加旧块、刷屏。行数取新旧最大值，多余的旧行清空。
                    // 上移基点用 lastCursorLine（终端光标当前行）而非块末尾：
                    // 删除文本后光标被 PositionCursor 放在中间行，从中间行上移 rows-1
                    // 到不了块首，会覆盖错位、提示符行残留重复。
                    var rows = Math.Max(lines, lastInputLines);
                    // lastCursorLine 为 0 时必须省略 CUU：多数终端（xterm/Windows Terminal/conhost）
                    // 把参数 0 按 1 处理，"\x1b[0A" 会真的上移一行，覆盖掉输入块上方的提示符行
                    if (lastCursorLine > 0)
                        Console.Write($"\x1b[{lastCursorLine}A");
                    var textLines = text.Split('\n');
                    for (int i = 0; i < rows; i++)
                    {
                        Console.Write("\r\x1b[2K");
                        if (i < textLines.Length)
                            Console.Write(textLines[i]);
                        if (i < rows - 1)
                            Console.Write("\n");
                    }
                    lastInputLines = lines;
                }
                else
                {
                    Console.Write("\r\x1b[2K" + text);
                }
                PositionCursor(fold);
                // 记录重绘后终端光标所在行（PositionCursor 已把光标放到 buf.Cursor 处，
                // 折叠时按折叠视图行计），作为下次多行重绘的上移基点
                var rawCursorLine = CountNewlines(buf.Text[..Math.Clamp(buf.Cursor, 0, buf.Text.Length)]);
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
            var cursorLine = CountNewlines(upTo); // 光标所在行（0-based）
            var totalLines = 1 + CountNewlines(buf.Text);
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
                        var foldLines = InputLine.FoldText(InputText()).Split('\n');
                        seg = foldLines.Length > 2 ? foldLines[2] : ""; // 折叠行文本，光标显示在末尾
                        col = DisplayWidth(seg); // 折叠行独立成行，无提示符前缀
                    }
                    else
                    {
                        seg = cursorLine == 0 ? upTo : upTo[(upTo.LastIndexOf('\n') + 1)..];
                        // 显示行首是提示符：光标列 = 提示符宽 + 行内内容宽（少算提示符会偏到其左侧）
                        col = DisplayWidth(promptPlain) + DisplayWidth(seg);
                    }
                    Console.Write($"\x1b[{up}A\r\x1b[{col}C");
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
                Console.Write($"\x1b[{up2}A\r\x1b[{DisplayWidth(promptPlain) + DisplayWidth(seg)}C");
            }
            else
            {
                // 光标在最后一行：从行尾左移到 cursor（单行输入或光标在末行）
                var offset = CursorLeftOffset(buf.Text, buf.Cursor);
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
                    var line = Fit(MenuLineText(k, i));
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

        /// <summary>菜单行文本：命令菜单带 1-9 编号（数字键可执行）；模式菜单无编号（数字键是普通输入）。</summary>
        string MenuLineText(int listIndex, int visibleRow) =>
            modePicker
                ? $"  {menuItems[listIndex].Name,-16} {menuItems[listIndex].Desc}"
                : $"  {visibleRow + 1}) {menuItems[listIndex].Name,-16} {menuItems[listIndex].Desc}";

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
                var more = menuItems.Count > 9 ? $" …(共 {menuItems.Count} 项)" : "";
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
            sb.AppendLine(Fit($"  → {menuItems[menuIndex].Name,-16} {menuItems[menuIndex].Desc}"));
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
            var pat = NormalizeCommandFilter(buf.Text);
            var newItems = Commands
                .Where(c => c.Name.StartsWith(pat, StringComparison.OrdinalIgnoreCase))
                .ToList();
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
            lastFilter = pat;
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
                lastPasteWasCR = false; // 非换行键到达：下一个 \n 不再是 CRLF 的尾半

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
                    break;
                case ConsoleKey.Enter:
                    searching = false; // 搜索结束：提交当前命中的历史条目
                    if (pasteActive)
                    {
                        // 括号粘贴中的换行一律是内容：不提交、不选菜单。
                        // CRLF 成对到达：\r 插入，紧跟的 \n 折叠掉；LF-only 各自插入（空行不塌）
                        if (key.KeyChar == '\n' && lastPasteWasCR)
                            break;
                        buf.Insert('\n');
                        lastPasteWasCR = key.KeyChar == '\r';
                        if (Console.KeyAvailable || pending.Count > 0 || pasteActive)
                            needsRedraw = true;
                        else
                            OnTextChanged();
                        break;
                    }
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
                    // 粘贴多行内容：缓冲未空或键快速连续到达（粘贴流）时，换行是内容的一部分，插入而非提交。
                    // Windows 终端粘贴是分批注入，首个 \r 到达时后续字符可能尚未进入缓冲区，
                    // 仅靠 KeyAvailable 不可靠，需结合 ReadKey 等待时间（<30ms=粘贴流）判定。
                    if (pasteStream || Console.KeyAvailable)
                    {
                        buf.Insert('\n');
                        // 分批注入竞态：CRLF 的 \n 可能在 \r 读走后才到（此刻 KeyAvailable=false）。
                        // 粘贴流的下一批通常 <5ms 到达，而真人不会在 5ms 内紧跟按键——小睡再查一次，
                        // 堵住「半截草稿被当成独立提交」的旧竞态
                        if (pasteStream && !Console.KeyAvailable)
                            System.Threading.Thread.Sleep(5);
                        // CRLF 粘贴的 \r\n 中 \r 触发本分支后 \n 还会再触发一次 Enter：
                        // 读取并丢弃；若下一个是普通字符（LF-only 粘贴的下一行内容）则放回暂存
                        if (Console.KeyAvailable)
                        {
                            var next = Console.ReadKey(intercept: true);
                            if (next.Key != ConsoleKey.Enter)
                                pending.Enqueue(next);
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
                        buf.Replace(menuItems[menuIndex >= 0 ? menuIndex : 0].Name);
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
                        if (!inputExpanded && 1 + CountNewlines(buf.Text) > 3)
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
                        buf.Replace(menuItems[0].Name);
                        draft = null;
                        CloseMenu();
                        RedrawInput();
                    }
                    else if (!menuOpen && SlashLike(buf.Text))
                    {
                        OpenMenu(false);
                    }
                    else if (menuOpen && menuItems.Count > 1)
                    {
                        // 多个匹配：循环选择
                        MoveSelection(menuIndex < 0 ? 0 : (menuIndex + 1) % menuItems.Count);
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
        for (var i = Math.Min(fromIndex, history.Count - 1); i >= 0; i--)
        {
            if (history[i].Contains(query, StringComparison.OrdinalIgnoreCase))
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
        return !expanded && raw + 1 > 3 ? 2 : raw;
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
