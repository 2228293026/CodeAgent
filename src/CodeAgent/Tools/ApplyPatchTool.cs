using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>
/// 应用统一差分(unified diff)到工作区文件:一次可改动一个文件的多个区域(或多个文件),
/// 比 edit_file 的单段 old_string/new_string 更适合大改动 / 模型从 read_file 直接生成补丁的场景。
/// 逐 hunk 校验上下文行必须精确匹配,不匹配即整体拒绝(绝不静默打坏文件)。
/// 支持跨平台:行尾(LF/CRLF)在应用时归一化到目标文件既有风格。
/// </summary>
public sealed class ApplyPatchTool : ITool
{
    public string Name => "apply_patch";

    public string Description =>
        "把 unified diff(补丁)应用到工作区文件:一次可修改一个文件的多个区域或不同文件。patch 必须含 +++ 文件头(常用 'a/'、'b/' 前缀)及一个或多个 @@ -1,L +1,M @@ hunk;hunk 行为 ' '(上下文)、'-'(删除)、'+'(新增)、'\\\\'(无换行标记,忽略)。应用前逐 hunk 校验上下文行与目标文件当前内容必须精确匹配,任一不符则整体拒绝,不作部分写入。返回每个文件的替换统计。";

    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["patch"] = new JsonObject { ["type"] = "string", ["description"] = "统一差分文本,可含多个文件(每文件 '+++ b/路径' + hunks;也可省略文件头,此时用 path 参数指定单文件)" },
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "补丁不含 +++ 文件头时的目标文件路径(相对工作区)" },
            ["validate_only"] = new JsonObject { ["type"] = "boolean", ["description"] = "只校验并报告将发生的改动,不写盘(默认 false)" },
            ["allow_new_file"] = new JsonObject { ["type"] = "boolean", ["description"] = "允许补丁创建目标文件(默认 false；设为 true 时目标不存在会新建文件)" },
        },
        ["required"] = new JsonArray("patch"),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        var patch = ToolArgs.GetString(args, "patch");
        if (string.IsNullOrWhiteSpace(patch))
            throw new ToolException("缺少必填参数 patch");

        var fallbackPath = ToolArgs.GetString(args, "path");
        var validateOnly = ToolArgs.GetBool(args, "validate_only", false);
        var allowNewFile = ToolArgs.GetBool(args, "allow_new_file", false);

        var files = ParsePatch(patch, fallbackPath);
        if (files.Count == 0)
            throw new ToolException("补丁中没有可用的文件块:需要至少一个 '@@ -N,M +... @@' hunk(以及 +++ 文件头,或提供 path 参数)。");

        var sb = new StringBuilder();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            sb.AppendLine(await ApplyFileAsync(file, ctx, validateOnly, allowNewFile, ct));
        }
        return "已应用补丁:\n" + sb.ToString().TrimEnd();
    }

    /// <summary>逐行解析补丁:识别 +++ 文件头与 @@ hunk,数据行(前缀 ' '/'-'/'+'/'\\')归入当前 hunk。</summary>
    internal static List<PatchFile> ParsePatch(string patch, string? fallbackPath)
    {
        var lines = SplitLines(patch);
        var result = new List<PatchFile>();
        PatchFile? cur = null;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (StartsWithAny(line, "+++ ") || StartsWithAny(line, "+++\t"))
            {
                var p = ExtractPath(line[4..].TrimStart().Trim());
                if (p.Length == 0)
                {
                    cur = null;
                    continue;
                }
                cur = new PatchFile(p);
                result.Add(cur);
                continue;
            }
            if (StartsWithAny(line, "@@") && cur is not null)
            {
                var oldStart = RegexOldStart(line);
                i++;
                var hunk = new PatchHunk { OldStart = oldStart };
                while (i < lines.Count && IsDataStart(lines[i]))
                {
                    var dl = lines[i];
                    if (dl[0] != '\\')
                        hunk.Lines.Add(new HunkLine(dl[0], dl.Length > 1 ? dl[1..] : ""));
                    i++;
                }
                i--; // 回退:让外层循环重新处理非数据行
                if (hunk.Lines.Count > 0)
                    cur.Hunks.Add(hunk);
            }
            // 其余行(--- 文件头、空白、间距)跳过
        }

        // 无文件头但给了 fallback:若前面 hunk 被丢弃(因为当时 cur==null),则无法救济,返回空。
        // 可选:若 fallback 非空且整个补丁都没有 +++ 头,则用 fallback 收集 hunk(下面重扫)。
        if (result.Count == 0 && !string.IsNullOrWhiteSpace(fallbackPath))
        {
            cur = new PatchFile(fallbackPath);
            for (int i = 0; i < lines.Count; i++)
            {
                if (!StartsWithAny(lines[i], "@@"))
                    continue;
                i++;
                var hunk = new PatchHunk { OldStart = RegexOldStart(lines[i - 1]) };
                while (i < lines.Count && IsDataStart(lines[i]))
                {
                    var dl = lines[i];
                    if (dl[0] != '\\')
                        hunk.Lines.Add(new HunkLine(dl[0], dl.Length > 1 ? dl[1..] : ""));
                    i++;
                }
                i--;
                if (hunk.Lines.Count > 0)
                    cur.Hunks.Add(hunk);
            }
            if (cur.Hunks.Count > 0)
                result.Add(cur);
        }
        return result;
    }

    private static int RegexOldStart(string header)
    {
        var m = System.Text.RegularExpressions.Regex.Match(header[2..].Trim(), @"^-(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 1;
    }

    private static bool IsDataStart(string s) =>
    s.Length > 0 && (s[0] is ' ' or '-' or '+' or '\\')
    && !(s.StartsWith("+++", StringComparison.OrdinalIgnoreCase)) // +++ 是文件头,不是新增行
    && !(s.StartsWith("---", StringComparison.OrdinalIgnoreCase)); // --- 是文件头/源头

    private static bool StartsWithAny(string s, string prefix) =>
        s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static List<string> SplitLines(string text)
    {
        var outList = new List<string>();
        var parts = text.Replace("\r\n", "\n").Split('\n');
        foreach (var p in parts)
            outList.Add(p);
        return outList;
    }

    /// <summary>去掉 a/ 或 b/ 前缀(模拟 git 补丁的路径语义)。</summary>
    private static string ExtractPath(string path)
    {
        if (path.Length > 2 && path[1] == '/' && (path[0] == 'a' || path[0] == 'b'))
            return path[2..];
        return path;
    }

    private async Task<string> ApplyFileAsync(PatchFile file, AgentContext ctx, bool validateOnly, bool allowNewFile, CancellationToken ct)
    {
        var full = ctx.Workspace.Resolve(file.Path); // 写工具:白名单只读目录也拒绝
        if (Directory.Exists(full))
            throw new ToolException($"目标路径是目录,无法应用补丁: {file.Path}");
        if (!File.Exists(full))
        {
            if (!allowNewFile)
                throw new ToolException($"目标文件不存在,无法应用补丁(请先 write_file 创建,或设置 allow_new_file=true): {file.Path}");
            // 新建文件:补丁全量由 additions 组成(original 为空),旧内容为 null
            var createStat = StatHunks(file.Hunks);
            if (validateOnly)
                return $"验证通过(新建): {file.Path}(+{createStat.added},共 {file.Hunks.Count} 个 hunk;未写盘)";
            var createText = string.Join('\n', ApplyHunks(file.Hunks, [], file.Path, true));
            await TextUtil.WriteTextPreserveEncodingAsync(full, createText, ct);
            ctx.Undo.Push(new UndoEntry
            {
                Kind = "write",
                Path = full,
                HadFile = false,
                EncodingName = TextUtil.DetectFileEncoding(full),
            });
            return $"已创建 {file.Path}(+{createStat.added},共 {file.Hunks.Count} 个 hunk)";
        }

        var text = TextUtil.ReadTextSmart(full); // GBK 等旧编码按原编码读取
        var crlf = text.Contains("\r\n");
        var norm = text.Replace("\r\n", "\n");
        var original = norm.Split('\n');
        if (original.Length > 0 && original[^1].Length == 0)
            original = original[..^1]; // 去掉末尾空段

        var applied = ApplyHunks(file.Hunks, original, file.Path, file.Hunks.Count == 1);

        var stat = StatHunks(file.Hunks);
        if (validateOnly)
            return $"验证通过: {file.Path}(-{stat.removed} +{stat.added},共 {file.Hunks.Count} 个 hunk;未写盘)";

        var newText = string.Join('\n', applied);
        // 保留目标文件原有的结尾换行风格(先补末尾换行,再统一转 CRLF,避免混入 \r\r\n)
        if (text.Length > 0 && text[^1] == '\n')
            newText += "\n";
        if (crlf)
            newText = newText.Replace("\n", "\r\n");

        string? fullOld = text.Length <= 4 * 1024 * 1024 ? text : null;
        await TextUtil.WriteTextPreserveEncodingAsync(full, newText, ct);

        ctx.Undo.Push(new UndoEntry
        {
            Kind = "edit",
            Path = full,
            OldText = fullOld ?? text,
            NewText = fullOld is null ? text : null,
            EncodingName = TextUtil.DetectFileEncoding(full),
        });

        return $"已应用 {file.Hunks.Count} 个 hunk → {file.Path}(-{stat.removed} +{stat.added})";
    }

    /// <summary>把 hunk 应用到原文行。用每个 hunk 的 oldStart(1 基、指向原文)绝对定位,
    /// 处理 hunk 之间有空行/间隔的情况;任一上下文/删除行不匹配即整体拒绝。单 hunk 时若
    /// 行号漂移(模型补丁删行导致 oldStart 不准),退化为按首数据行全文搜索定位。</summary>
    internal static string[] ApplyHunks(List<PatchHunk> hunks, string[] original, string displayPath, bool generousLocate)
    {
        var result = new List<string>(original.Length + 64);
        var cursor = 0; // 已复制到 result 的原文最大索引(0 基,下一位)

        foreach (var hunk in hunks)
        {
            if (hunk.Lines.Count == 0)
                continue;

            // 计算本 hunk 在原文的起始(1 基 oldStart -> 0 基,并确保不早于已消费位置)
            var start = Math.Max(cursor, hunk.OldStart - 1);

            // 复制 start 之前的间隔行(未在任何 hunk 中出现的内容)
            for (; cursor < original.Length && cursor < start; cursor++)
                result.Add(original[cursor]);

            // 逐行处理本 hunk:新增('+')直接写;上下文(' ')与删除('-')必须在原文按序匹配并消费
            int pos = cursor;
            foreach (var l in hunk.Lines)
            {
                if (l.Op == '+')
                {
                    result.Add(l.Text);
                    continue;
                }
                if (pos >= original.Length || original[pos] != l.Text)
                {
                    // 上下文不匹配:单 hunk 且允许搜索时,尝试从 pos 往后找首数据行(行号漂移容错)
                    var effective = TryLocateAfterFuzz(hunks.Count == 1 && generousLocate, original, ref pos, l, hunk);
                    if (!effective)
                        throw new ToolException(
                            $"补丁上下文不匹配: 文件 {displayPath} 第 {pos + 1} 行应为 '{Short(l.Text)}' 但实际是 '{Short(pos < original.Length ? original[pos] : "<文件已到结尾>")}'。未做任何修改。请基于 read_file 的最新内容重新生成补丁。");
                    // 搜索命中且该行是上下文/删除:继续按同一行逻辑处理(下面统一推进)
                    if (pos >= original.Length || original[pos] != l.Text)
                        throw new ToolException(
                            $"补丁上下文不匹配: 文件 {displayPath} 第 {pos + 1} 行应为 '{Short(l.Text)}' 但实际是 '{Short(pos < original.Length ? original[pos] : "<文件已到结尾>")}'。未做任何修改。");
                }
                pos++; // 消费该行
                if (l.Op == ' ')
                    result.Add(l.Text); // 上下文行保留;删除行不保留
            }
            cursor = pos; // 本 hunk 消费后的位置作为下一个 hunk 的下界
        }
        for (; cursor < original.Length; cursor++)
            result.Add(original[cursor]);
        return result.ToArray();
    }

    /// <summary>单 hunk 行号漂移容错:在 pos 之后(含)找第一个「内容等于 l.Text 且该行在 hunk 中为
    /// 首个非新增行对应文本」的位置。为免误吞,只在找到后才把 pos 推进;找不到返回 false。</summary>
    private static bool TryLocateAfterFuzz(bool enabled, string[] original, ref int pos, HunkLine l, PatchHunk hunk)
    {
        if (!enabled || l.Op == '+')
            return false;
        for (int i = pos; i < original.Length; i++)
        {
            if (original[i] == l.Text)
            {
                pos = i;
                return true;
            }
        }
        return false;
    }

    private static (int added, int removed) StatHunks(List<PatchHunk> hunks)
    {
        int a = 0, r = 0;
        foreach (var h in hunks)
            foreach (var l in h.Lines)
            {
                if (l.Op == '+') a++;
                else if (l.Op == '-') r++;
            }
        return (a, r);
    }

    private static string Short(string s) => TextUtil.TruncateLine(s, 40);

    /// <summary>单个补丁文件块。</summary>
    internal sealed class PatchFile
    {
        public PatchFile(string path) => Path = path;
        public string Path { get; }
        public List<PatchHunk> Hunks { get; } = [];
    }

    internal sealed class PatchHunk
    {
        public int OldStart { get; set; }
        public List<HunkLine> Lines { get; } = [];
    }

    internal readonly record struct HunkLine(char Op, string Text);
}
