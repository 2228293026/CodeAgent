using System.Text;
using System.Text.Json.Nodes;

namespace CodeAgent.Tools;

/// <summary>读取文件内容（带行号，支持 offset/limit）。</summary>
public sealed class ReadFileTool : ITool
{
    public string Name => "read_file";
    public string Description => "读取文件内容（带行号）。用 offset/limit 只读需要的部分，tail 读末尾 N 行（日志排查），避免一次性读取大文件。";
    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "文件路径，相对工作区根目录" },
            ["offset"] = new JsonObject { ["type"] = "integer", ["description"] = "起始行号（1 起，默认 1）" },
            ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "最多读取行数（默认 300，最大 5000）" },
            ["tail"] = new JsonObject { ["type"] = "integer", ["description"] = "读取末尾 N 行（1-5000；与 offset 同时给出时优先）" },
            ["head"] = new JsonObject { ["type"] = "integer", ["description"] = "读取开头 N 行（1-5000，limit 的便捷写法：等价于 offset=1&limit=N，tail 优先）" },
            ["no_line_numbers"] = new JsonObject { ["type"] = "boolean", ["description"] = "不带行号输出原文（默认 false）" },
        },
        ["required"] = new JsonArray("path"),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        var path = ToolArgs.GetString(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            throw new ToolException("缺少必填参数 path");

        var full = ctx.Workspace.ResolveRead(path);
        if (Directory.Exists(full))
            throw new ToolException($"'{path}' 是目录，请用 list_directory 查看目录内容。");
        if (!File.Exists(full))
            throw new ToolException($"文件不存在: {path}");

        var info = new FileInfo(full);
        if (info.Length > 20 * 1024 * 1024)
            throw new ToolException($"文件过大（{info.Length / 1024 / 1024} MB），请用 offset/limit 分段读取。");

        var offset = Math.Max(1, ToolArgs.GetInt(args, "offset", 1));
        var limit = Math.Clamp(ToolArgs.GetInt(args, "limit", 300), 1, 5000);
        var tail = Math.Clamp(ToolArgs.GetInt(args, "tail", 0), 0, 5000);
        var headCount = Math.Clamp(ToolArgs.GetInt(args, "head", 0), 0, 5000);
        // head 是 limit 的便捷写法（读开头 N 行）：未给 tail 时优先于 limit 生效
        if (headCount > 0 && tail == 0)
        {
            offset = 1;
            limit = headCount;
        }
        var noLineNumbers = ToolArgs.GetBool(args, "no_line_numbers", false);

        var text = await TextUtil.ReadTextSmartAsync(full, ct);
        if (SkipDirs.LooksBinary(text))
            throw new ToolException($"文件疑似二进制（含 NUL 字节），无法作为文本读取: {path}");

        var lines = text.Split('\n');
        // 去掉末尾换行产生的空段（与 ReadAllLinesAsync 语义一致），避免幽灵空行
        if (lines.Length > 0 && lines[^1].Length == 0)
            lines = lines[..^1];
        if (lines.Length == 0)
            return $"(文件 {path} 为空)";

        int start, count;
        if (tail > 0)
        {
            // tail 模式：读末尾 N 行（排查日志尾部最常用），同样受 5000 行上限约束
            start = Math.Max(0, lines.Length - tail);
            count = lines.Length - start;
            if (count > 5000)
            {
                start += count - 5000;
                count = 5000;
            }
        }
        else
        {
            start = Math.Min(offset - 1, lines.Length);
            if (start >= lines.Length)
                return $"(文件 {path} 共 {lines.Length} 行，offset={offset} 超出范围，无需读取)";
            count = Math.Min(limit, lines.Length - start);
        }
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            var line = lines[start + i].TrimEnd('\r');
            // 单行截断保护：压缩 JSON / base64 等超长行会撑爆上下文（默认 2000 字符/行）
            line = TextUtil.TruncateLine(line, 2000);
            sb.AppendLine(noLineNumbers ? line : $"{start + i + 1}\t{line}");
        }

        var range = count < lines.Length ? $"，已显示 {start + 1}-{start + count}" : "";
        var head = $"（{path} 共 {lines.Length} 行{range}）\n";

        // 编码提示：非纯 UTF-8（带 BOM 或 GBK/ANSI 旧编码）时显式标注，避免模型误判文件为 UTF-8 去改写
        var enc = TextUtil.DetectFileEncoding(full);
        var encNote = enc switch
        {
            "utf8-bom" => "（编码: UTF-8 BOM）",
            "gb18030" => "（编码: GBK/GB18030）",
            _ => "",
        };
        return (encNote.Length > 0 ? encNote + "\n" : "") + head + sb.ToString().TrimEnd();
    }
}

/// <summary>创建/覆盖写入文件。</summary>
public sealed class WriteFileTool : ITool
{
    public string Name => "write_file";
    public string Description => "创建新文件或整体覆盖已有文件。写完整内容，包括所有行。";
    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "目标文件路径，相对工作区根目录" },
            ["content"] = new JsonObject { ["type"] = "string", ["description"] = "文件完整内容" },
            ["create_dirs"] = new JsonObject { ["type"] = "boolean", ["description"] = "自动创建父目录（默认 true）" },
        },
        ["required"] = new JsonArray("path", "content"),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        var path = ToolArgs.GetString(args, "path");
        if (string.IsNullOrWhiteSpace(path))
            throw new ToolException("缺少必填参数 path");
        if (args?["content"] is null)
            throw new ToolException("缺少必填参数 content");

        var content = ToolArgs.GetString(args, "content");
        var full = ctx.Workspace.Resolve(path);
        if (Directory.Exists(full))
            throw new ToolException($"'{path}' 是目录，不能作为文件写入（目标应为文件路径）。");
        if (ToolArgs.GetBool(args, "create_dirs", true))
        {
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }
        else
        {
            // create_dirs=false 且父目录不存在：提前给清晰错误，而不是让 WriteAllText 抛笼统的「系统找不到指定的路径」
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                throw new ToolException($"父目录不存在: {dir}（如需自动创建请设置 create_dirs=true）");
        }

        // 记录撤销信息（大文件不记录，避免内存占用）；先写入成功再入栈，失败不污染撤销历史
        var hadFile = File.Exists(full);
        string? old = null;
        if (hadFile)
        {
            var info = new FileInfo(full);
            if (info.Length <= 4 * 1024 * 1024)
                old = await TextUtil.ReadTextSmartAsync(full, ct);
        }

        // 内容与现状完全一致：跳过写入（不刷 mtime、不污染撤销栈）
        if (hadFile && old == content)
            return $"内容未变化，跳过写入: {path}（{TextUtil.TruncateLine(content, 60)}）";

        try
        {
            await TextUtil.WriteTextPreserveEncodingAsync(full, content, ct); // 保原编码：GBK 文件不被动转 UTF-8
        }
        catch (IOException ex)
        {
            throw new ToolException($"写入失败: {ex.Message}");
        }
        ctx.Undo.Push(new UndoEntry
        {
            Kind = "write",
            Path = full,
            OldText = old,
            HadFile = hadFile,
            EncodingName = hadFile ? TextUtil.DetectFileEncoding(full) : null, // 撤销按原编码写回
        });

        var bytes = Encoding.UTF8.GetByteCount(content);
        // 行数（按 \n 计；纯空白/空内容记为 0）：让模型快速知道写了多少，便于与预期对照
        var lineCount = content.Length == 0 ? 0 : content.Split('\n').Length;
        return $"已写入 {bytes:N0} 字节（{lineCount} 行）→ {path}";
    }
}

/// <summary>在已有文件中做精确文本替换（类似补丁）。</summary>
public sealed class EditFileTool : ITool
{
    public string Name => "edit_file";
    public string Description => "在已有文件中精确替换一段文本（old_string 必须逐字匹配，含缩进与空白）。改动前必须先 read_file 确认原文。";
    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "文件路径，相对工作区根目录" },
            ["old_string"] = new JsonObject { ["type"] = "string", ["description"] = "要替换的原文（精确匹配）" },
            ["new_string"] = new JsonObject { ["type"] = "string", ["description"] = "替换后的文本" },
            ["replace_all"] = new JsonObject { ["type"] = "boolean", ["description"] = "出现多次时是否全部替换（默认 false，重复会报错）" },
        },
        ["required"] = new JsonArray("path", "old_string", "new_string"),
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        var path = ToolArgs.GetString(args, "path");
        var oldString = ToolArgs.GetString(args, "old_string");
        var newString = ToolArgs.GetString(args, "new_string");
        if (string.IsNullOrWhiteSpace(path))
            throw new ToolException("缺少必填参数 path");
        if (string.IsNullOrEmpty(oldString))
            throw new ToolException("缺少必填参数 old_string");
        // 相同字符串的替换是无操作：明确报错让模型自查，而不是返回误导性的「已替换 N 处」
        if (oldString == newString)
            throw new ToolException("old_string 与 new_string 相同，无需修改（如需调整请给出不同的新文本）。");

        var full = ctx.Workspace.Resolve(path);
        if (Directory.Exists(full))
            throw new ToolException($"'{path}' 是目录，edit_file 只能修改文件。");
        if (!File.Exists(full))
            throw new ToolException($"文件不存在: {path}");

        var text = await TextUtil.ReadTextSmartAsync(full, ct);
        var replaceAll = ToolArgs.GetBool(args, "replace_all", false);

        // 精确匹配优先；未命中时做换行风格容错：old_string 用 LF、文件是 CRLF（或反过来）时
        // 逐字匹配必失败（模型输出几乎总是 LF，Windows 工程常是 CRLF）。归一化到 LF 匹配，
        // 替换片段（new_string）同步转成文件的换行风格——CRLF 文件不混入 LF 行。
        var workText = text;
        var workOld = oldString;
        var workNew = newString;
        var normalized = false;
        var firstIdx = workText.IndexOf(workOld, StringComparison.Ordinal);
        if (firstIdx < 0)
        {
            var lfText = text.Replace("\r\n", "\n");
            var lfOld = oldString.Replace("\r\n", "\n");
            if (lfText.Contains(lfOld, StringComparison.Ordinal))
            {
                workText = lfText;
                workOld = lfOld;
                workNew = newString.Replace("\r\n", "\n");
                normalized = true;
                firstIdx = workText.IndexOf(workOld, StringComparison.Ordinal);
            }
        }
        // 先统一做未命中检查：replace_all 模式下未命中也不允许静默写回原文件
        // 并报「已替换 0 处」（曾让模型误以为修改成功，还往撤销栈里塞了无效条目）
        if (firstIdx < 0)
        {
            // 空白归一化后能命中 → 差异只在缩进/行尾空白，给出可行动的提示而不是让模型盲试
            var hint = TextUtil.NormalizeWhitespace(text).Contains(TextUtil.NormalizeWhitespace(oldString), StringComparison.Ordinal)
                ? "\n提示：文件中存在仅空白/缩进差异的相似内容——请从 read_file 输出逐字复制 old_string（注意行首缩进与行尾空白）。"
                : "";
            throw new ToolException(
                $"未找到 old_string（必须逐字精确匹配，包括缩进与换行）。old_string 为:\n---\n{oldString}\n---{hint}");
        }
        int count = TextUtil.CountOccurrences(workText, workOld);

        string result;
        if (replaceAll)
        {
            result = workText.Replace(workOld, workNew);
        }
        else
        {
            if (count > 1)
                throw new ToolException(
                    $"old_string 在文件中出现 {count} 次，请扩大上下文使其唯一，或设置 replace_all=true。");
            result = workText.Remove(firstIdx, workOld.Length).Insert(firstIdx, workNew);
        }
        if (normalized && text.Contains("\r\n"))
        {
            // 归一化路径的落盘前收尾：先把替换片段可能带入的 CRLF 压平，再统一按文件的
            // CRLF 风格还原（此刻 result 只含 LF，二次替换不会产生 \r\r\n）
            result = result.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        // 记录撤销信息：小文件记录完整原文（撤销可精确恢复），大文件退化为 old/new 对；
        // 先写入成功再入栈，失败不污染撤销历史
        string? fullOld = text.Length <= 4 * 1024 * 1024 ? text : null;
        await TextUtil.WriteTextPreserveEncodingAsync(full, result, ct);

        ctx.Undo.Push(new UndoEntry
        {
            Kind = "edit",
            Path = full,
            OldText = fullOld ?? oldString, // 完整原文（小文件）或修改前的原文片段（大文件）
            EncodingName = TextUtil.DetectFileEncoding(full), // 撤销按原编码写回
            NewText = fullOld is null ? newString : null, // 仅大文件退化时使用
        });

        // startLine 基于 workText（归一化后）计算：firstIdx 是 workText 的下标，用原文会错位
        var startLine = workText.AsSpan(0, Math.Max(0, firstIdx)).Count('\n') + 1;
        // 归一化命中且原文件是 CRLF：提示换行风格被保留（避免模型以为改成了 LF）
        var crlfNote = normalized && text.Contains("\r\n") ? "，保留原 CRLF 换行" : "";
        return $"已替换 {count} 处 → {path}（修改起始行 {startLine}{crlfNote}）";
    }
}

/// <summary>列出目录树。</summary>
public sealed class ListDirectoryTool : ITool
{
    public string Name => "list_directory";
    public string Description => "列出目录结构（目录带 / 后缀）。跳过构建/缓存/版本控制目录。";
    public JsonObject Parameters { get; } = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["path"] = new JsonObject { ["type"] = "string", ["description"] = "目录路径，默认工作区根目录" },
            ["depth"] = new JsonObject { ["type"] = "integer", ["description"] = "递归深度（默认 2，最大 5）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        var path = ToolArgs.GetString(args, "path");
        var depth = Math.Clamp(ToolArgs.GetInt(args, "depth", 2), 0, 5);

        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(path) ? null : path);
        if (File.Exists(root))
            throw new ToolException($"'{path}' 是文件，请用 read_file 查看内容。");
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {path}");

        var sb = new StringBuilder();
        var emitted = 0;
        var dirCount = 0;
        var fileCount = 0;
        const int cap = 800;

        void Walk(string dir, int level)
        {
            if (level > depth || emitted >= cap)
                return;
            var indent = new string(' ', level * 2);
            try
            {
                foreach (var d in Directory.EnumerateDirectories(dir)
                             .OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
                {
                    if (emitted >= cap)
                        break; // 上限在循环内也生效：平铺大目录不再把 cap 之后的行全部输出
                    var name = Path.GetFileName(d);
                    if (SkipDirs.IsSkipped(name))
                        continue;
                    sb.AppendLine(indent + name + "/");
                    emitted++;
                    dirCount++;
                    Walk(d, level + 1);
                }
                foreach (var f in Directory.EnumerateFiles(dir)
                             .OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
                {
                    if (emitted >= cap)
                        break;
                    sb.AppendLine(indent + Path.GetFileName(f));
                    emitted++;
                    fileCount++;
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        await Task.Yield();
        Walk(root, 0);

        if (emitted == 0)
            return $"(目录为空或全部被跳过: {path})";
        var head = string.IsNullOrWhiteSpace(path) ? $"工作区根目录 {ctx.Workspace.Root}\n" : $"目录 {path}\n";
        // 统计摘要：模型与用户都能一眼看出规模（截断时尤其重要——cap 后的未计入）
        var summary = $"\n（共 {dirCount} 个目录、{fileCount} 个文件" + (emitted >= cap ? "，已达显示上限，可能未列全）" : "）");
        return head + sb.ToString().TrimEnd() + (emitted >= cap ? "\n…(条目过多，已截断)" : "") + summary;
    }

}
