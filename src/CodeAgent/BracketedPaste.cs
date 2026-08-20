namespace CodeAgent;

/// <summary>括号粘贴（bracketed paste）标记探测状态机。
/// 终端开启 ESC[?2004h 后，粘贴内容被 ESC[200~ … ESC[201~ 包裹，边界是确定性的——
/// 现行的「按键间隔 &lt;30ms = 粘贴流」启发式在注入分批（CRLF 的 \r\n 跨批次）时会把
/// \n 误判为真人按 Enter，半截草稿被提交。逐键喂入即可探测标记；中途不匹配即 Failed，
/// 调用方须把已消费的键放回输入队列（不能吞用户的按键）。</summary>
internal sealed class PasteMarkerDetector
{
    private enum Step { NeedEsc, NeedBracket, Need2, Need0, NeedDigit, NeedTilde, Done, Failed }

    private Step _step = Step.NeedEsc;
    private char _digit; // '0' = 粘贴开始（ESC[200~），'1' = 结束（ESC[201~）

    /// <summary>喂入下一个键的字符（Escape 键传 '\x1b'）。任何一步不匹配即 Failed。</summary>
    public void Feed(char ch)
    {
        switch (_step)
        {
            case Step.NeedEsc when ch == '\x1b':
                _step = Step.NeedBracket;
                return;
            case Step.NeedBracket when ch == '[':
                _step = Step.Need2;
                return;
            case Step.Need2 when ch == '2':
                _step = Step.Need0;
                return;
            case Step.Need0 when ch == '0':
                _step = Step.NeedDigit;
                return;
            case Step.NeedDigit when ch is '0' or '1':
                _digit = ch;
                _step = Step.NeedTilde;
                return;
            case Step.NeedTilde when ch == '~':
                _step = Step.Done;
                return;
            default:
                _step = Step.Failed;
                return;
        }
    }

    /// <summary>序列中断，不是标记：已消费的键需全部放回。</summary>
    public bool Failed => _step == Step.Failed;

    /// <summary>还没到终态（Done/Failed），可以继续喂。</summary>
    public bool InProgress => _step is Step.NeedEsc or Step.NeedBracket or Step.Need2
        or Step.Need0 or Step.NeedDigit or Step.NeedTilde;

    /// <summary>终态结果：Start = ESC[200~（粘贴开始）；End = ESC[201~（粘贴结束）；None = 未完成或失败。</summary>
    public PasteMarkerResult Result => _step == Step.Done
        ? (_digit == '0' ? PasteMarkerResult.Start : PasteMarkerResult.End)
        : PasteMarkerResult.None;
}

/// <summary>括号粘贴标记探测结果。</summary>
internal enum PasteMarkerResult { None, Start, End }
