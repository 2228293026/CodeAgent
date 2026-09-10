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
            ["limit"] = new JsonObject { ["type"] = "integer", ["description"] = "最多读取行数（0=不使用, 默认 300，最大 5000）" },
            ["tail"] = new JsonObject { ["type"] = "integer", ["description"] = "读取末尾 N 行（1-5000；与 offset 同时给出时优先）" },
            ["head"] = new JsonObject { ["type"] = "integer", ["description"] = "读取开头 N 行（0=不使用, 1-5000；limit 的便捷写法：等价于 offset=1&limit=N，tail 优先）" },
            ["skip"] = new JsonObject { ["type"] = "integer", ["description"] = "跳过前 N 行（0=不跳过，默认 0；与 offset 互斥，skip 优先）" },
            ["range"] = new JsonObject { ["type"] = "string", ["description"] = "行号范围（如 \"10-20\" 或 \"10:20\"，与 offset/limit 互斥，range 优先）" },
            ["no_line_numbers"] = new JsonObject { ["type"] = "boolean", ["description"] = "不带行号输出原文（默认 false）" },
            ["max_line_length"] = new JsonObject { ["type"] = "integer", ["description"] = "单行截断阈值（0=不截断，默认 2000，最大 50000）" },
            ["no_encoding_note"] = new JsonObject { ["type"] = "boolean", ["description"] = "不显示编码提示（默认 false；已知编码时可减少输出噪音）" },
            ["show_encoding"] = new JsonObject { ["type"] = "boolean", ["description"] = "强制显示编码信息（默认 false；设为 true 时在输出中显式标注文件编码，便于排查乱码问题）" },
            ["skip_empty"] = new JsonObject { ["type"] = "boolean", ["description"] = "跳过空行（默认 false）" },
            ["trim"] = new JsonObject { ["type"] = "boolean", ["description"] = "去掉每行首尾空白（默认 false）" },
            ["no_header"] = new JsonObject { ["type"] = "boolean", ["description"] = "不显示头部范围提示（默认 false）" },
            ["raw"] = new JsonObject { ["type"] = "boolean", ["description"] = "原始输出：不带行号、不截断、不显示编码提示（默认 false）" },
            ["hash"] = new JsonObject { ["type"] = "boolean", ["description"] = "在输出末尾附加 SHA256 哈希（默认 false；可用于校验文件完整性）" },
            ["stats"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示文件统计信息（默认 false；设为 true 时在输出中附加行数、单词数、字符数、字节数、MIME 类型）" },
            ["encoding"] = new JsonObject { ["type"] = "string", ["description"] = "强制指定编码：utf8、utf8-bom、gbk、gb18030、ascii（默认自动检测）" },
            ["strip_bom"] = new JsonObject { ["type"] = "boolean", ["description"] = "去掉 UTF-8 BOM 头（默认 false；输出内容不带 BOM，方便复制粘贴）" },
            ["byte_offset"] = new JsonObject { ["type"] = "integer", ["description"] = "字节偏移（0 起，默认 0；与 byte_limit 配合使用，按字节范围读取而非按行）" },
            ["byte_limit"] = new JsonObject { ["type"] = "integer", ["description"] = "最多读取字节数（0=不限，默认 0，最大 2000000；与 byte_offset 配合使用）" },
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
        var skip = Math.Clamp(ToolArgs.GetInt(args, "skip", 0), 0, 5000);
        if (skip > 0)
            offset = skip + 1; // skip 优先于 offset：跳过前 N 行后从第 N+1 行开始
        var rangeArg = ToolArgs.GetString(args, "range");
        if (!string.IsNullOrWhiteSpace(rangeArg))
        {
            // range 格式："10-20" 或 "10:20"，与 offset/limit 互斥，range 优先
            var parts = rangeArg.Split('-', ':');
            if (parts.Length == 2 && int.TryParse(parts[0], out var lineStart) && int.TryParse(parts[1], out var lineEnd) && lineStart > 0 && lineEnd >= lineStart)
            {
                offset = lineStart;
                limit = lineEnd - lineStart + 1;
            }
            else
            {
                throw new ToolException($"range 格式无效: {rangeArg}（应为 \"start-end\" 或 \"start:end\"，如 \"10-20\"）");
            }
        }
        var noLineNumbers = ToolArgs.GetBool(args, "no_line_numbers", false);
        var maxLineLength = ToolArgs.GetInt(args, "max_line_length", 2000);
        if (maxLineLength < 0) maxLineLength = 2000; // 负值回退默认值，避免误伤
        if (maxLineLength > 50000) maxLineLength = 50000;
        var noEncodingNote = ToolArgs.GetBool(args, "no_encoding_note", false);
        var showEncoding = ToolArgs.GetBool(args, "show_encoding", false);
        var skipEmpty = ToolArgs.GetBool(args, "skip_empty", false);
        var trim = ToolArgs.GetBool(args, "trim", false);
        var noHeader = ToolArgs.GetBool(args, "no_header", false);
        var isRaw = ToolArgs.GetBool(args, "raw", false);
        var includeHash = ToolArgs.GetBool(args, "hash", false);
        var showStats = ToolArgs.GetBool(args, "stats", false);
        var encoding = ToolArgs.GetString(args, "encoding");
        if (isRaw)
        {
            noLineNumbers = true;
            maxLineLength = 0;
            noEncodingNote = true;
            noHeader = true;
        }

        var stripBom = ToolArgs.GetBool(args, "strip_bom", false);
        var byteOffset = Math.Clamp(ToolArgs.GetInt(args, "byte_offset", 0), 0, int.MaxValue);
        var byteLimit = Math.Clamp(ToolArgs.GetInt(args, "byte_limit", 0), 0, 2_000_000);
        string text;
        // 先检测文件是否带 UTF-8 BOM（ReadTextSmart 内部已去掉，这里只探测头部）
        bool hadBom = false;
        if (!string.IsNullOrEmpty(encoding))
        {
            // 显式指定编码：绕过自动检测，直接按指定编码读取
            var enc = encoding.ToLowerInvariant() switch
            {
                "utf8-bom" or "utf-8-bom" => new System.Text.UTF8Encoding(true),
                "utf8" or "utf-8" => new System.Text.UTF8Encoding(false),
                "gbk" or "gb18030" => System.Text.Encoding.GetEncoding("GB18030"),
                "ascii" => System.Text.Encoding.ASCII,
                _ => throw new ToolException($"不支持的编码: {encoding}（支持: utf8, utf8-bom, gbk, gb18030, ascii）"),
            };
            text = await File.ReadAllTextAsync(full, enc, ct);
        }
        else
        {
            // 探测文件头部是否有 UTF-8 BOM（仅探测前 3 字节）
            try
            {
                await using var fs = File.OpenRead(full);
                var bomHead = new byte[3];
                var n = await fs.ReadAsync(bomHead.AsMemory(0, 3), ct);
                hadBom = n == 3 && bomHead[0] == 0xEF && bomHead[1] == 0xBB && bomHead[2] == 0xBF;
            }
            catch { }
            text = await TextUtil.ReadTextSmartAsync(full, ct);
        }

        // strip_bom=false:若文件原带 UTF-8 BOM，在输出内容前补回 BOM 字符
        if (!stripBom && hadBom && text.Length > 0 && text[0] != '\uFEFF')
            text = '\uFEFF' + text;
        // strip_bom=true:ReadTextSmart 已默认去掉 BOM，无需额外处理
        if (SkipDirs.LooksBinary(text))
            throw new ToolException($"文件疑似二进制（含 NUL 字节），无法作为文本读取: {path}");

        // byte_offset/byte_limit:按字节范围读取（用于二进制文件或大文件分段）
        if (byteOffset > 0 || byteLimit > 0)
        {
            var bytes = await File.ReadAllBytesAsync(full, ct);
            var byteStart = Math.Min(byteOffset, bytes.Length);
            var byteEnd = byteLimit > 0 ? Math.Min(byteStart + byteLimit, bytes.Length) : bytes.Length;
            var slice = bytes[byteStart..byteEnd];
            text = System.Text.Encoding.UTF8.GetString(slice);
        }

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
        int displayedCount = 0;
        int lastDisplayedLine = start;
        for (int i = 0; i < count; i++)
        {
            var raw = lines[start + i].TrimEnd('\r');
            if (skipEmpty && string.IsNullOrWhiteSpace(raw))
                continue; // 跳过空行/空白行：行号仍按原文件保持（不重排）
            // 单行截断保护：压缩 JSON / base64 等超长行会撑爆上下文（默认 2000 字符/行）
            var line = maxLineLength == 0 ? raw : TextUtil.TruncateLine(raw, maxLineLength);
            if (trim)
                line = line.Trim(); // 去掉首尾空白（缩进、尾随空格）
            sb.AppendLine(noLineNumbers ? line : $"{start + i + 1}\t{line}");
            displayedCount++;
            lastDisplayedLine = start + i;
        }

        var range = displayedCount > 0 && displayedCount < lines.Length
            ? $"，已显示 {start + 1}-{lastDisplayedLine + 1}"
            : "";
        var head = noHeader ? "" : $"（{path} 共 {lines.Length} 行{range}）\n";

        // 编码提示：非纯 UTF-8（带 BOM 或 GBK/ANSI 旧编码）时显式标注，避免模型误判文件为 UTF-8 去改写
        var detectedEnc = TextUtil.DetectFileEncoding(full);
        var encNote = noEncodingNote ? "" : showEncoding || detectedEnc is "utf8-bom" or "gb18030"
            ? $"（编码: {detectedEnc switch { "utf8-bom" => "UTF-8 BOM", "gb18030" => "GBK/GB18030", _ => detectedEnc ?? "UTF-8" }})"
            : "";
        var output = (encNote.Length > 0 ? encNote + "\n" : "") + head + sb.ToString().TrimEnd();
        if (includeHash)
        {
            var hash = SkipDirs.ComputeFileSha256(full); // 流式：不整读进内存
            if (hash is not null)
                output += $"\nsha256:{hash}";
        }
        if (showStats)
        {
            var lineCount = text.Split('\n').Length;
            var words = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
            var bytes = Encoding.UTF8.GetByteCount(text);
            var mimeType = SkipDirs.GetMimeType(full);
            output += $"\n[stats] {lineCount} 行，{words} 词，{text.Length} 字符，{bytes:N0} 字节，MIME: {mimeType}";
        }
        return output;
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
            ["append"] = new JsonObject { ["type"] = "boolean", ["description"] = "追加模式：在文件末尾添加内容（默认 false；与 content 配合使用）" },
            ["bom"] = new JsonObject { ["type"] = "boolean", ["description"] = "写入 UTF-8 BOM（默认 false；新建文件时生效，已有文件保留原编码）" },
            ["line_ending"] = new JsonObject { ["type"] = "string", ["description"] = "换行符模式：preserve（默认，保留原文件风格；新建文件用 LF）、lf（强制 LF）、crlf（强制 CRLF）" },
            ["backup"] = new JsonObject { ["type"] = "boolean", ["description"] = "覆盖已有文件前先创建 .bak 备份（默认 false）" },
            ["preserve_trailing_newline"] = new JsonObject { ["type"] = "boolean", ["description"] = "保留末尾换行（默认 true；设为 false 时自动去掉 content 末尾的换行符）" },
            ["encoding"] = new JsonObject { ["type"] = "string", ["description"] = "写入编码：utf8（默认，无 BOM）、utf8-bom（带 BOM）、gbk、gb18030、ascii；已有文件默认保留原编码，除非显式指定" },
            ["if_exists"] = new JsonObject { ["type"] = "string", ["description"] = "文件已存在时的处理方式：overwrite（默认，覆盖）、skip（跳过，不修改）、error（报错拒绝写入）" },
            ["preserve_timestamp"] = new JsonObject { ["type"] = "boolean", ["description"] = "保留原文件修改时间（默认 false；设为 true 时覆盖后保持最后修改时间不变，便于增量构建/缓存）" },
            ["atomic"] = new JsonObject { ["type"] = "boolean", ["description"] = "原子写入（默认 false；设为 true 时先写临时文件再 rename，避免写入过程中断导致文件损坏）" },
            ["dry_run"] = new JsonObject { ["type"] = "boolean", ["description"] = "仅预览改动（返回将发生的变更摘要，不写盘），默认 false" },
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
        var append = ToolArgs.GetBool(args, "append", false);
        var bom = ToolArgs.GetBool(args, "bom", false);
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
        // 编码必须在写盘「之前」探测：写盘后再探测拿到的是新文件的编码。
        // encoding=gbk 覆盖 UTF-8 文件时，撤销会把旧文本按 GBK 写回，字节与原文件不符。
        var originalEncoding = hadFile ? TextUtil.DetectFileEncoding(full) : null;
        if (hadFile)
        {
            var info = new FileInfo(full);
            if (info.Length <= 4 * 1024 * 1024)
                old = await TextUtil.ReadTextSmartAsync(full, ct);
        }

        // backup=true:覆盖前创建 .bak 副本
        if (hadFile && ToolArgs.GetBool(args, "backup", false))
        {
            var bak = full + ".bak";
            File.Copy(full, bak, overwrite: true);
        }

        var lineEnding = ToolArgs.GetString(args, "line_ending");
        string? targetEnding = null;
        if (!string.IsNullOrEmpty(lineEnding))
        {
            if (lineEnding == "preserve" && hadFile && old is not null)
            {
                // preserve:检测已有文件的换行风格
                targetEnding = old.Contains("\r\n") ? "crlf" : "lf";
            }
            else if (lineEnding == "lf" || lineEnding == "crlf")
            {
                targetEnding = lineEnding;
            }
        }

        // if_exists 检查：文件已存在时的处理方式
        var ifExists = ToolArgs.GetString(args, "if_exists");
        if (!string.IsNullOrEmpty(ifExists) && File.Exists(full))
        {
            if (ifExists == "skip")
                return $"文件已存在，已跳过写入: {path}（如需覆盖请移除 if_exists=skip 或设为 overwrite）";
            if (ifExists == "error")
                throw new ToolException($"文件已存在，拒绝写入: {path}（如需覆盖请移除 if_exists=error 或设为 overwrite）");
        }

        var preserveTimestamp = ToolArgs.GetBool(args, "preserve_timestamp", false);
        var oldTimestamp = hadFile ? File.GetLastWriteTime(full) : default;
        var atomic = ToolArgs.GetBool(args, "atomic", false);
        var dryRun = ToolArgs.GetBool(args, "dry_run", false);

        string finalContent;
        if (targetEnding is not null)
        {
            string NormalizeLineEndings(string text, string target)
            {
                if (target == "crlf")
                    return text.Replace("\r\n", "\n").Replace("\n", "\r\n");
                return text.Replace("\r\n", "\n");
            }
            var normalized = NormalizeLineEndings(content, targetEnding);
            if (append && hadFile && old is not null)
            {
                var sep = targetEnding == "crlf" ? "\r\n" : "\n";
                finalContent = (old.EndsWith("\n") || old.EndsWith("\r\n")) ? old + normalized : old + sep + normalized;
            }
            else
            {
                finalContent = normalized;
            }
        }
        else if (append && hadFile && old is not null)
        {
            // 未指定 line_ending:追加模式保留原行为
            finalContent = old.EndsWith("\n") ? old + content : old + "\n" + content;
        }
        else
        {
            finalContent = content;
        }

        // preserve_trailing_newline=false:去掉末尾换行符
        if (!ToolArgs.GetBool(args, "preserve_trailing_newline", true) && !string.IsNullOrEmpty(finalContent))
        {
            if (finalContent.EndsWith("\r\n"))
                finalContent = finalContent[..^2];
            else if (finalContent.EndsWith('\n'))
                finalContent = finalContent[..^1];
        }

        // 内容与现状完全一致：跳过写入（不刷 mtime、不污染撤销栈）
        if (hadFile && old == finalContent)
            return $"内容未变化，跳过写入: {path}（{TextUtil.TruncateLine(finalContent, 60)}）";

        try
        {
            if (dryRun)
            {
                var dryRunBytes = Encoding.UTF8.GetByteCount(finalContent);
                var dryRunLineCount = finalContent.Length == 0 ? 0 : finalContent.Split('\n').Length;
                return $"[dry_run] 将写入 {dryRunBytes:N0} 字节（{dryRunLineCount} 行）→ {path}（{(hadFile ? "覆盖已有文件" : "新建文件")}）。未写盘。";
            }
            var encoding = ToolArgs.GetString(args, "encoding");
            if (!string.IsNullOrEmpty(encoding))
            {
                // 显式指定编码：绕过原编码保留，直接按指定编码写入
                var enc = encoding.ToLowerInvariant() switch
                {
                    "utf8-bom" or "utf-8-bom" => new System.Text.UTF8Encoding(true),
                    "utf8" or "utf-8" => new System.Text.UTF8Encoding(false),
                    "gbk" or "gb18030" => System.Text.Encoding.GetEncoding("GB18030"),
                    "ascii" => System.Text.Encoding.ASCII,
                    _ => throw new ToolException($"不支持的编码: {encoding}（支持: utf8, utf8-bom, gbk, gb18030, ascii）"),
                };
                await File.WriteAllTextAsync(full, finalContent, enc, ct);
            }
            else if (bom && !hadFile)
            {
                // 新建文件 + bom=true:原子写 UTF-8 BOM（临时文件同目录确保同卷，rename 原子）
                // 驱动器根/UNC 根下 GetDirectoryName 返回 null，统一走 TempPathFor 兜底
                Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
                var tmp = SkipDirs.TempPathFor(full);
                try
                {
                    await File.WriteAllTextAsync(tmp, finalContent, new System.Text.UTF8Encoding(true), ct);
                    File.Move(tmp, full, overwrite: true);
                }
                catch
                {
                    // Move 失败（目标被占用/只读/无权限）不得把临时文件留在用户目录
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    throw;
                }
            }
            else if (atomic)
            {
                // atomic=true:写临时文件再 rename，避免写入过程中断导致文件损坏
                // 同 bom 分支：统一走 TempPathFor（内含 null 兜底）
                Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
                var tmp = SkipDirs.TempPathFor(full);
                try
                {
                    await TextUtil.WriteTextPreserveEncodingAsync(tmp, finalContent, ct);
                    File.Move(tmp, full, overwrite: true);
                }
                catch
                {
                    // 同上：失败路径清理临时文件
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    throw;
                }
            }
            else
            {
                await TextUtil.WriteTextPreserveEncodingAsync(full, finalContent, ct); // 保原编码：GBK 文件不被动转 UTF-8
            }
        }
        catch (IOException ex)
        {
            throw new ToolException($"写入失败: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            // UnauthorizedAccessException 不是 IOException：目标只读/被占用/无权限时
            // 此前会漏出裸异常给模型，而不是可读的工具错误
            throw new ToolException($"写入失败（无权限或文件被占用）: {ex.Message}");
        }
        if (preserveTimestamp && hadFile && oldTimestamp != default)
        {
            File.SetLastWriteTime(full, oldTimestamp);
        }
        ctx.Undo.Push(new UndoEntry
        {
            Kind = "write",
            Path = full,
            OldText = old,
            HadFile = hadFile,
            EncodingName = originalEncoding, // 写盘前探测的原编码（不是写盘后的新编码）
        });

        var bytes = Encoding.UTF8.GetByteCount(finalContent);
        // 行数（按 \n 计；纯空白/空内容记为 0）：让模型快速知道写了多少，便于与预期对照
        var lineCount = finalContent.Length == 0 ? 0 : finalContent.Split('\n').Length;
        var action = append ? "追加" : "写入";
        return $"{action} {bytes:N0} 字节（{lineCount} 行）→ {path}";
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
            ["case_insensitive"] = new JsonObject { ["type"] = "boolean", ["description"] = "忽略大小写匹配（默认 false；匹配后仍按 new_string 原样写入）" },
            ["dry_run"] = new JsonObject { ["type"] = "boolean", ["description"] = "仅预览改动（返回将发生的变更摘要，不写盘、不污染撤销栈），默认 false" },
            ["backup"] = new JsonObject { ["type"] = "boolean", ["description"] = "覆盖已有文件前先创建 .bak 备份（默认 false）" },
            ["preserve_timestamp"] = new JsonObject { ["type"] = "boolean", ["description"] = "保留原文件修改时间（默认 false；设为 true 时编辑后保持最后修改时间不变）" },
            ["show_diff"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示差异（默认 false；设为 true 时在返回结果中附加 unified diff，便于确认改动内容）" },
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
        var caseInsensitive = ToolArgs.GetBool(args, "case_insensitive", false);
        var dryRun = ToolArgs.GetBool(args, "dry_run", false);
        var allowMultiple = ToolArgs.GetBool(args, "allow_multiple", false);
        var backup = ToolArgs.GetBool(args, "backup", false);
        var preserveTimestamp = ToolArgs.GetBool(args, "preserve_timestamp", false);
        var showDiff = ToolArgs.GetBool(args, "show_diff", false);
        var oldTimestamp = preserveTimestamp && File.Exists(full) ? File.GetLastWriteTime(full) : default;
        var cmp = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        // 精确匹配优先；未命中时做换行风格容错：old_string 用 LF、文件是 CRLF（或反过来）时
        // 逐字匹配必失败（模型输出几乎总是 LF，Windows 工程常是 CRLF）。归一化到 LF 匹配，
        // 替换片段（new_string）同步转成文件的换行风格——CRLF 文件不混入 LF 行。
        var workText = text;
        var workOld = oldString;
        var workNew = newString;
        var normalized = false;
        var firstIdx = workText.IndexOf(workOld, cmp);
        if (firstIdx < 0)
        {
            var lfText = text.Replace("\r\n", "\n");
            var lfOld = oldString.Replace("\r\n", "\n");
            if (lfText.Contains(lfOld, cmp))
            {
                workText = lfText;
                workOld = lfOld;
                workNew = newString.Replace("\r\n", "\n");
                normalized = true;
                firstIdx = workText.IndexOf(workOld, cmp);
            }
        }
        // 先统一做未命中检查：replace_all 模式下未命中也不允许静默写回原文件
        // 并报「已替换 0 处」（曾让模型误以为修改成功，还往撤销栈里塞了无效条目）
        if (firstIdx < 0)
        {
            // 空白归一化后能命中 → 差异只在缩进/行尾空白，给出可行动的提示而不是让模型盲试
            var hint = TextUtil.NormalizeWhitespace(text).Contains(TextUtil.NormalizeWhitespace(oldString), cmp)
                ? "\n提示：文件中存在仅空白/缩进差异的相似内容——请从 read_file 输出逐字复制 old_string（注意行首缩进与行尾空白）。"
                : "";
            throw new ToolException(
                $"未找到 old_string（必须逐字精确匹配，包括缩进与换行）。old_string 为:\n---\n{oldString}\n---{hint}");
        }
        int count = TextUtil.CountOccurrences(workText, workOld, cmp);
        if (count > 1 && !replaceAll && !allowMultiple)
            throw new ToolException($"old_string 出现 {count} 次（非唯一匹配）。如需替换全部请设置 replace_all=true；如需允许多处匹配请设置 allow_multiple=true。");

        string result;
        if (replaceAll)
        {
            // replace_all + case_insensitive:需要自定义不区分大小写的替换
            if (caseInsensitive)
            {
                result = workText;
                int start = 0;
                while (true)
                {
                    var idx = result.IndexOf(workOld, start, cmp);
                    if (idx < 0) break;
                    result = result.Remove(idx, workOld.Length).Insert(idx, workNew);
                    start = idx + workNew.Length;
                }
            }
            else
            {
                result = workText.Replace(workOld, workNew);
            }
        }
        else
        {
            if (count > 1 && !allowMultiple)
                throw new ToolException(
                    $"old_string 在文件中出现 {count} 次，请扩大上下文使其唯一，或设置 replace_all=true / allow_multiple=true。");
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
        // 编码必须在写盘「之前」探测：写盘后再探测拿到的是新文件的编码，
        // 撤销会把旧文本按新编码写回（GBK 文件被 UTF-8 化后撤销回来就成了乱码）
        var originalEncoding = TextUtil.DetectFileEncoding(full);

        // 生成 unified diff（show_diff 时附加到返回结果）
        string? diff = null;
        if (showDiff)
        {
            var oldLines = workOld.Split('\n');
            var newLines = workNew.Split('\n');
            var sb = new StringBuilder();
            sb.AppendLine("--- a/" + Path.GetFileName(full));
            sb.AppendLine("+++ b/" + Path.GetFileName(full));
            int maxLines = Math.Max(oldLines.Length, newLines.Length);
            for (int i = 0; i < maxLines; i++)
            {
                var oldLine = i < oldLines.Length ? oldLines[i] : null;
                var newLine = i < newLines.Length ? newLines[i] : null;
                if (oldLine == newLine)
                {
                    if (oldLine != null)
                        sb.AppendLine("  " + oldLine);
                }
                else
                {
                    if (oldLine != null)
                        sb.AppendLine("- " + oldLine);
                    if (newLine != null)
                        sb.AppendLine("+ " + newLine);
                }
            }
            diff = sb.ToString();
        }

        if (dryRun)
        {
            var dryStartLine = workText.AsSpan(0, Math.Max(0, firstIdx)).Count('\n') + 1;
            var dryCrlfNote = normalized && text.Contains("\r\n") ? "，保留原 CRLF 换行" : "";
            var dryRunMsg = $"[dry_run] 将替换 {count} 处 → {path}（修改起始行 {dryStartLine}{dryCrlfNote}）。未写盘。";
            return showDiff && diff != null ? dryRunMsg + "\n\n" + diff : dryRunMsg;
        }

        if (backup)
        {
            var bak = full + ".bak";
            File.Copy(full, bak, overwrite: true);
        }
        await TextUtil.WriteTextPreserveEncodingAsync(full, result, ct);

        if (preserveTimestamp && oldTimestamp != default)
        {
            File.SetLastWriteTime(full, oldTimestamp);
        }

        ctx.Undo.Push(new UndoEntry
        {
            Kind = "edit",
            Path = full,
            OldText = fullOld ?? oldString, // 完整原文（小文件）或修改前的原文片段（大文件）
            EncodingName = originalEncoding, // 写盘前探测的原编码
            NewText = fullOld is null ? newString : null, // 仅大文件退化时使用
        });

        // startLine 基于 workText（归一化后）计算：firstIdx 是 workText 的下标，用原文会错位
        var startLine = workText.AsSpan(0, Math.Max(0, firstIdx)).Count('\n') + 1;
        // 归一化命中且原文件是 CRLF：提示换行风格被保留（避免模型以为改成了 LF）
        var crlfNote = normalized && text.Contains("\r\n") ? "，保留原 CRLF 换行" : "";
        var msg = $"已替换 {count} 处 → {path}（修改起始行 {startLine}{crlfNote}）";
        return showDiff && diff != null ? msg + "\n\n" + diff : msg;
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
            ["depth"] = new JsonObject { ["type"] = "integer", ["description"] = "递归深度（默认 2，最大 5；recursive=true 时自动设为 5）" },
            ["recursive"] = new JsonObject { ["type"] = "boolean", ["description"] = "递归列出所有子目录（默认 false；设为 true 时 depth 自动设为最大值 5）" },
            ["ignore"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "跳过这些目录名（大小写不敏感），如 [\"node_modules\", \"vendor\"]；在 SkipDirs 之外额外排除" },
            ["max_items"] = new JsonObject { ["type"] = "integer", ["description"] = "最多列出条目数（默认 800，最大 5000）" },
            ["files_only"] = new JsonObject { ["type"] = "boolean", ["description"] = "只列出文件（跳过目录），默认 false" },
            ["dirs_only"] = new JsonObject { ["type"] = "boolean", ["description"] = "只列出目录（跳过文件），默认 false" },
            ["sort_by"] = new JsonObject { ["type"] = "string", ["description"] = "排序方式：name（默认，按名称）、size（按文件大小降序）、modified（按修改时间降序）" },
            ["reverse"] = new JsonObject { ["type"] = "boolean", ["description"] = "反向排序（默认 false；设为 true 时反转排序结果）" },
            ["show_hidden"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示隐藏文件/目录（默认 false；Unix 以 . 开头，Windows 带 Hidden/System 属性）" },
            ["show_size"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示文件大小（默认 false；设为 true 时在文件名后附加大小，如 file.txt (1.2 KB)）" },
            ["show_modified"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示修改时间（默认 false；设为 true 时在文件名后附加最后修改时间，如 file.txt (2025-01-15 10:30)）" },
            ["show_file_count"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示每个目录的文件数（默认 false；设为 true 时在目录名后附加文件数，如 src/ (5 个文件)）" },
            ["show_encoding"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示文件编码（默认 false；设为 true 时在文件名后附加编码信息，如 file.txt [UTF-8]）" },
            ["show_line_count"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示文件行数（默认 false；设为 true 时在文件名后附加行数，如 file.txt [42 lines]）" },
            ["skip_empty_dirs"] = new JsonObject { ["type"] = "boolean", ["description"] = "跳过空目录（默认 false；设为 true 时只列出包含文件的目录）" },
            ["show_hash"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示 SHA256 哈希（默认 false；设为 true 时在文件名后附加哈希值，如 file.txt (sha256:abc123...)）" },
            ["show_extension"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示文件扩展名（默认 false；设为 true 时在文件名后附加扩展名，如 file.txt [.txt]）" },
            ["show_type"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示文件类型（默认 false；设为 true 时在文件名后附加类型标记，如 file.txt <file> 或 dir/ <dir>）" },
            ["show_mime_type"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示 MIME 类型（默认 false；设为 true 时在文件名后附加 MIME 类型，如 file.txt (text/plain)）" },
            ["show_owner"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示文件所有者（默认 false；设为 true 时在文件名后附加所有者信息，如 file.txt (owner:user)）" },
            ["show_group"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示文件组（默认 false；设为 true 时在文件名后附加组信息，如 file.txt (group:users)）" },
            ["show_permissions"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示文件权限（默认 false；设为 true 时在文件名后附加权限信息，如 file.txt (-rw-r--r--)）" },
            ["show_symlink"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示符号链接（默认 false；设为 true 时在文件名后附加链接信息，如 link.txt -> target.txt）" },
            ["show_hardlinks"] = new JsonObject { ["type"] = "boolean", ["description"] = "显示硬链接数（默认 false；设为 true 时在文件名后附加硬链接数，如 file.txt (2 hard links)）" },
        },
    };

    public async Task<string> ExecuteAsync(JsonObject? args, AgentContext ctx, CancellationToken ct)
    {
        var path = ToolArgs.GetString(args, "path");
        var recursive = ToolArgs.GetBool(args, "recursive", false);
        var depth = recursive ? 5 : Math.Clamp(ToolArgs.GetInt(args, "depth", 2), 0, 5);
        var maxItems = Math.Clamp(ToolArgs.GetInt(args, "max_items", 800), 1, 5000);
        var filesOnly = ToolArgs.GetBool(args, "files_only", false);
        var dirsOnly = ToolArgs.GetBool(args, "dirs_only", false);
        var ignoreSet = ToolArgs.GetStringSet(args, "ignore");
        var sortBy = ToolArgs.GetString(args, "sort_by");
        var reverse = ToolArgs.GetBool(args, "reverse", false);
        var showHidden = ToolArgs.GetBool(args, "show_hidden", false);
        var showSize = ToolArgs.GetBool(args, "show_size", false);
        var showModified = ToolArgs.GetBool(args, "show_modified", false);
        var showFileCount = ToolArgs.GetBool(args, "show_file_count", false);
        var showEncoding = ToolArgs.GetBool(args, "show_encoding", false);
        var showLineCount = ToolArgs.GetBool(args, "show_line_count", false);
        var skipEmptyDirs = ToolArgs.GetBool(args, "skip_empty_dirs", false);
        var showHash = ToolArgs.GetBool(args, "show_hash", false);
        var showExtension = ToolArgs.GetBool(args, "show_extension", false);
        var showType = ToolArgs.GetBool(args, "show_type", false);
        var showMimeType = ToolArgs.GetBool(args, "show_mime_type", false);
        var showOwner = ToolArgs.GetBool(args, "show_owner", false);
        var showGroup = ToolArgs.GetBool(args, "show_group", false);
        var showPermissions = ToolArgs.GetBool(args, "show_permissions", false);
        var showSymlink = ToolArgs.GetBool(args, "show_symlink", false);
        var showHardlinks = ToolArgs.GetBool(args, "show_hardlinks", false);

        var root = ctx.Workspace.ResolveRead(string.IsNullOrWhiteSpace(path) ? null : path);
        if (File.Exists(root))
            throw new ToolException($"'{path}' 是文件，请用 read_file 查看内容。");
        if (!Directory.Exists(root))
            throw new ToolException($"目录不存在: {path}");

        var sb = new StringBuilder();
        var emitted = 0;
        var dirCount = 0;
        var fileCount = 0;

        void Walk(string dir, int level)
        {
            if (level > depth || emitted >= maxItems)
                return;
            var indent = new string(' ', level * 2);
            try
            {
                foreach (var d in OrderEntries(Directory.EnumerateDirectories(dir), sortBy, reverse))
                {
                    if (emitted >= maxItems)
                        break; // 上限在循环内也生效：平铺大目录不再把 max_items 之后的行全部输出
                    var name = Path.GetFileName(d);
                    if (!showHidden && SkipDirs.IsHidden(d))
                        continue; // 跳过隐藏目录
                    if (SkipDirs.IsSkipped(name) || (ignoreSet is not null && ignoreSet.Contains(name)))
                        continue;
                    if (!filesOnly)
                    {
                        // skip_empty_dirs=true:跳过没有文件的目录（递归检查）
                        if (skipEmptyDirs && !Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Any())
                            continue;
                        var suffix = "";
                        if (showModified)
                        {
                            var dirModified = Directory.GetLastWriteTime(d).ToString("yyyy-MM-dd HH:mm");
                            suffix += $" ({dirModified})";
                        }
                        if (showFileCount)
                        {
                            var fileInDir = Directory.EnumerateFiles(d).Count();
                            suffix += $" ({fileInDir} 个文件)";
                        }
                        if (showType)
                            suffix += " <dir>";
                        sb.AppendLine($"{indent}{name}/{suffix}");
                        emitted++;
                        dirCount++;
                    }
                    // filesOnly=true 且 recursive=false（默认）:不递归子目录，只列出当前层文件
                    if (!filesOnly || recursive)
                        Walk(d, level + 1);
                }
                if (!dirsOnly)
                {
                    foreach (var f in OrderEntries(Directory.EnumerateFiles(dir), sortBy, reverse))
                    {
                        if (emitted >= maxItems)
                            break;
                        if (!showHidden && SkipDirs.IsHidden(f))
                            continue; // 跳过隐藏文件
                        var fileName = Path.GetFileName(f);
                        var suffix = new List<string>();
                        var parenParts = new List<string>();
                        var bracketParts = new List<string>();
                        if (showSize)
                        {
                            var size = new FileInfo(f).Length;
                            var sizeStr = size < 1024 ? $"{size} B" : size < 1024 * 1024 ? $"{size / 1024.0:F1} KB" : $"{size / 1024.0 / 1024.0:F1} MB";
                            parenParts.Add(sizeStr);
                        }
                        if (showModified)
                        {
                            var modified = File.GetLastWriteTime(f).ToString("yyyy-MM-dd HH:mm");
                            parenParts.Add(modified);
                        }
                        if (showHash)
                        {
                            // 流式计算：File.ReadAllBytes 会把大文件整个读进内存（GB 级直接 OOM）
                            var hash = SkipDirs.ComputeFileSha256(f);
                            if (hash is not null)
                                parenParts.Add($"sha256:{hash[..8]}...");
                        }
                        if (showMimeType)
                        {
                            parenParts.Add(SkipDirs.GetMimeType(f));
                        }
                        if (showOwner)
                        {
                            // 非 Windows 无跨平台 API：不显示，而不是拿当前登录用户冒充文件属主
                            var owner = SkipDirs.GetFileOwner(f);
                            if (owner is not null)
                                parenParts.Add($"owner:{owner}");
                        }
                        if (showGroup)
                        {
                            var group = SkipDirs.GetFileGroup(f);
                            if (group is not null)
                                parenParts.Add($"group:{group}");
                        }
                        if (showPermissions)
                        {
                            parenParts.Add(SkipDirs.GetPermissions(f));
                        }
                        if (showSymlink)
                        {
                            try
                            {
                                // 必须用 LinkTarget（链接指向的路径）：File.ReadAllText 会读成
                                // 目标文件的内容，把链接显示成 "-> 文件正文" 而不是 "-> 路径"
                                var linkTarget = new FileInfo(f).LinkTarget;
                                if (!string.IsNullOrEmpty(linkTarget))
                                    parenParts.Add($"-> {linkTarget}");
                            }
                            catch
                            {
                                // 符号链接读取失败，忽略
                            }
                        }
                        if (showHardlinks)
                        {
                            var links = SkipDirs.GetHardLinkCount(f);
                            if (links is int n)
                                parenParts.Add(n == 1 ? "1 hard link" : $"{n} hard links");
                            // 非 Windows 无跨平台 API：不显示，避免给出错误数字
                        }
                        if (showEncoding)
                        {
                            var enc = TextUtil.DetectFileEncoding(f) ?? "UTF-8";
                            var encLabel = enc switch { "utf8-bom" => "UTF-8 BOM", "gb18030" => "GBK/GB18030", _ => enc };
                            bracketParts.Add(encLabel);
                        }
                        if (showLineCount)
                        {
                            // 流式统计：File.ReadAllLines 会把整个文件读进内存（大文件 OOM）
                            var lineCount = SkipDirs.CountFileLines(f);
                            if (lineCount is long n)
                                bracketParts.Add($"{n} lines");
                        }
                        if (showExtension)
                        {
                            bracketParts.Add(Path.GetExtension(fileName));
                        }
                        var typeSuffix = showType ? " <file>" : "";
                        if (parenParts.Count > 0 || bracketParts.Count > 0)
                        {
                            var parts = new List<string>();
                            if (parenParts.Count > 0) parts.Add($"({string.Join(", ", parenParts)})");
                            if (bracketParts.Count > 0) parts.Add($"[{string.Join(", ", bracketParts)}]");
                            sb.AppendLine($"{indent}{fileName} {string.Join(" ", parts)}{typeSuffix}");
                        }
                        else if (showType)
                        {
                            sb.AppendLine($"{indent}{fileName} <file>");
                        }
                        else
                        {
                            sb.AppendLine(indent + fileName);
                        }
                        emitted++;
                        fileCount++;
                    }
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
        var summary = $"\n（共 {dirCount} 个目录、{fileCount} 个文件" + (emitted >= maxItems ? "，已达显示上限，可能未列全）" : "）");
        return head + sb.ToString().TrimEnd() + (emitted >= maxItems ? "\n…(条目过多，已截断)" : "") + summary;
    }

    /// <summary>对目录/文件列表做排序（按名称、大小或修改时间）。</summary>
    private static IEnumerable<string> OrderEntries(IEnumerable<string> entries, string? sortBy, bool reverse = false)
    {
        IEnumerable<string> ordered = sortBy switch
        {
            "size" => entries.OrderByDescending(x => new FileInfo(x).Length),
            "modified" => entries.OrderByDescending(x => File.GetLastWriteTimeUtc(x)),
            _ => entries.OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase),
        };
        return reverse ? ordered.Reverse() : ordered;
    }
}
