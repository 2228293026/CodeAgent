using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CodeAgent.Tools;
using Xunit;

namespace CodeAgent.Tests;

public class FileToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-tools-" + Guid.NewGuid().ToString("N"));

    public FileToolsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
    }

    private static AgentContext MakeContext(string dir) => new()
    {
        Config = new AgentConfig(),
        Workspace = new Workspace(dir),
    };

    [Fact]
    public async Task WriteFile_MissingContent_Throws()
    {
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);
        var args = new JsonObject { ["path"] = "a.txt" };

        var ex = await Assert.ThrowsAsync<ToolException>(
            () => tool.ExecuteAsync(args, ctx, CancellationToken.None));
        Assert.Contains("content", ex.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "a.txt"))); // 不应静默写空文件
    }

    [Fact]
    public async Task WriteFile_EmptyContent_IsAllowed()
    {
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);
        var args = new JsonObject { ["path"] = "b.txt", ["content"] = "" };

        await tool.ExecuteAsync(args, ctx, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(_dir, "b.txt"))); // 显式空串是合法写入
    }

    [Fact]
    public async Task ReadFile_DefaultsToLineNumbers()
    {
        var path = Path.Combine(_dir, "r1.txt");
        File.WriteAllText(path, "第一行\nsecond");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(new JsonObject { ["path"] = "r1.txt" }, ctx, CancellationToken.None);
        Assert.Contains("1\t第一行", output);
        Assert.Contains("2\tsecond", output);
    }

    [Fact]
    public async Task ReadFile_NoLineNumbers_OutputsRawText()
    {
        var path = Path.Combine(_dir, "r2.txt");
        File.WriteAllText(path, "第一行\nsecond");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "r2.txt", ["no_line_numbers"] = true }, ctx, CancellationToken.None);
        Assert.Contains("第一行", output);
        Assert.Contains("second", output);
        Assert.DoesNotContain("1\t", output); // 不应出现行号前缀
    }

    [Fact]
    public async Task ReadFile_BinaryFile_ThrowsInsteadOfGarbage()
    {
        // 回归：二进制文件（含 NUL 字节）不应输出乱码行，应明确报错
        var path = Path.Combine(_dir, "bin.dat");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x41, 0x00]);
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(
            () => tool.ExecuteAsync(new JsonObject { ["path"] = "bin.dat" }, ctx, CancellationToken.None));
        Assert.Contains("二进制", ex.Message);
    }

    [Fact]
    public async Task ReadFile_CrlfLineEndings_DoNotPolluteLineContent()
    {
        var path = Path.Combine(_dir, "r3.txt");
        File.WriteAllText(path, "aaa\r\nbbb\r\n");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(new JsonObject { ["path"] = "r3.txt" }, ctx, CancellationToken.None);
        Assert.Contains("1\taaa", output);
        Assert.Contains("2\tbbb", output);
        Assert.DoesNotContain("3\t", output); // 末尾换行不应产生幽灵空行
        Assert.DoesNotContain("bbb\r", output); // 行尾 \r 不应残留在内容里
    }

    [Fact]
    public async Task ReadFile_StringNumberParams_AreParsedTolerantly()
    {
        // 模型常把数字参数序列化为字符串（"2" / "true"），应正确解析而非回退默认值
        var path = Path.Combine(_dir, "r4.txt");
        File.WriteAllText(path, "line1\nline2\nline3");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "r4.txt", ["offset"] = "2", ["limit"] = "1", ["no_line_numbers"] = "true" },
            ctx, CancellationToken.None);
        Assert.Contains("line2", output); // offset=2, limit=1 → 只有第二行
        Assert.DoesNotContain("line1", output);
        Assert.DoesNotContain("line3", output);
    }

    [Fact]
    public async Task ReadFile_Directory_ReturnsHelpfulHint()
    {
        // 回归：对目录调用 read_file 应提示用 list_directory，而非误报"文件不存在"
        var sub = Path.Combine(_dir, "subdir");
        Directory.CreateDirectory(sub);
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(
            () => tool.ExecuteAsync(new JsonObject { ["path"] = "subdir" }, ctx, CancellationToken.None));
        Assert.Contains("list_directory", ex.Message);
        Assert.DoesNotContain("文件不存在", ex.Message);
    }

    [Fact]
    public async Task WriteFile_DirectoryPath_ThrowsHint()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "adir"));
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(
            () => tool.ExecuteAsync(new JsonObject { ["path"] = "adir", ["content"] = "x" }, ctx, CancellationToken.None));
        Assert.Contains("是目录", ex.Message);
    }

    [Fact]
    public async Task EditFile_DirectoryPath_ThrowsHint()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "edir"));
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(
            () => tool.ExecuteAsync(
                new JsonObject { ["path"] = "edir", ["old_string"] = "a", ["new_string"] = "b" },
                ctx, CancellationToken.None));
        Assert.Contains("是目录", ex.Message);
    }

    [Fact]
    public async Task ListDirectory_FilePath_ThrowsHint()
    {
        File.WriteAllText(Path.Combine(_dir, "afile.txt"), "x");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(
            () => tool.ExecuteAsync(new JsonObject { ["path"] = "afile.txt" }, ctx, CancellationToken.None));
        Assert.Contains("read_file", ex.Message);
    }

    [Fact]
    public async Task ListDirectory_ListsEntries_SkipsBuildDirs()
    {
        // 目录条目应列出；bin/obj/node_modules 等构建目录应跳过
        Directory.CreateDirectory(Path.Combine(_dir, "src"));
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        Directory.CreateDirectory(Path.Combine(_dir, "node_modules"));
        File.WriteAllText(Path.Combine(_dir, "README.md"), "x");
        File.WriteAllText(Path.Combine(_dir, "src", "Program.cs"), "x");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(new JsonObject(), ctx, CancellationToken.None);

        Assert.Contains("src/", output);
        Assert.Contains("README.md", output);
        Assert.Contains("Program.cs", output);
        Assert.DoesNotContain("bin/", output);
        Assert.DoesNotContain("node_modules", output);
    }

    [Fact]
    public async Task ListDirectory_DepthLimit_ControlsRecursion()
    {
        // depth=0 只列直接子项，不深入任何子目录
        Directory.CreateDirectory(Path.Combine(_dir, "a", "b", "c"));
        File.WriteAllText(Path.Combine(_dir, "a", "top.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "a", "b", "deep.txt"), "x");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "a", ["depth"] = 0 }, ctx, CancellationToken.None);

        Assert.Contains("top.txt", output);
        Assert.Contains("b/", output);
        Assert.DoesNotContain("deep.txt", output); // 深度 0 不进入 b
        Assert.DoesNotContain("c/", output);       // 也不显示 b 的子目录
    }

    [Fact]
    public async Task ListDirectory_FlatDirWithManyFiles_RespectsCap()
    {
        // 回归：cap 曾只在 Walk 入口检查，平铺目录里 foreach 会把 cap 之后的文件行全部输出
        for (int i = 0; i < 900; i++)
            File.WriteAllText(Path.Combine(_dir, $"f{i:000}.txt"), "x");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var outText = await tool.ExecuteAsync(new JsonObject { ["path"] = "." }, ctx, CancellationToken.None);

        var lines = outText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("已截断", outText);
        Assert.True(lines.Length <= 803, $"输出行数 {lines.Length} 应被 cap 截断在 ~800 行");
        Assert.Contains("f000.txt", outText); // 头部条目保留
        Assert.DoesNotContain("f899.txt", outText); // 超出 cap 的尾部条目被截掉
    }

    [Fact]
    public async Task ListDirectory_EmptyDir_ReportsHelpfully()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "empty"));
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "empty" }, ctx, CancellationToken.None);

        Assert.Contains("空", output);
    }

    [Fact]
    public async Task ListDirectory_Entries_AreSorted()
    {
        // 输出确定性：目录组与文件组各自按名称排序（目录先于文件）
        Directory.CreateDirectory(Path.Combine(_dir, "zeta"));
        Directory.CreateDirectory(Path.Combine(_dir, "alpha"));
        File.WriteAllText(Path.Combine(_dir, "mid.txt"), "x");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(new JsonObject(), ctx, CancellationToken.None);

        var idxAlpha = output.IndexOf("alpha/", StringComparison.Ordinal);
        var idxZeta = output.IndexOf("zeta/", StringComparison.Ordinal);
        var idxMid = output.IndexOf("mid.txt", StringComparison.Ordinal);
        Assert.True(idxAlpha >= 0 && idxZeta > idxAlpha, $"目录组应按字母序排列:\n{output}");
        Assert.True(idxMid > idxZeta, $"文件组应列在目录之后:\n{output}");
    }

    [Fact]
    public async Task WriteFile_NumericContent_IsCoercedToString()
    {
        // 回归：模型偶尔把字符串参数序列化为数字，GetString 应容错转换而非抛 InvalidOperationException
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "n.txt", ["content"] = 123 }, ctx, CancellationToken.None);
        Assert.Equal("123", File.ReadAllText(Path.Combine(_dir, "n.txt")));
    }

    [Fact]
    public async Task ReadFile_OffsetBeyondEnd_ReportsRange()
    {
        // 回归：offset 超出文件行数时曾显示错误范围（如"已显示 11-10"），应友好说明
        var path = Path.Combine(_dir, "r5.txt");
        File.WriteAllText(path, "a\nb\nc");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "r5.txt", ["offset"] = 99 }, ctx, CancellationToken.None);
        Assert.Contains("共 3 行", output);
        Assert.Contains("超出范围", output);
        Assert.DoesNotContain("99-99", output); // 不应出现倒退的行号区间
    }

    [Fact]
    public async Task ReadFile_VeryLongLine_IsTruncated()
    {
        // 回归：压缩 JSON / base64 等超长行曾整行输出撑爆上下文，现应按 2000 字符/行截断
        var path = Path.Combine(_dir, "long.txt");
        File.WriteAllText(path, new string('x', 5000) + "\nshort");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(new JsonObject { ["path"] = "long.txt" }, ctx, CancellationToken.None);
        Assert.Contains("…", output); // 有截断标记
        Assert.True(output.Length < 2500, $"超长行应被截断（当前输出 {output.Length} 字符）");
        Assert.Contains("short", output); // 后续正常行不受影响
    }

    [Fact]
    public async Task WriteFile_CreateDirsFalseString_IsRespected()
    {
        // 回归：create_dirs 默认 true，模型传字符串 "0" 时应解析为 false（不自动建目录），
        // 而非回退默认值 true 导致父目录被静默创建。
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);
        var parent = Path.Combine(_dir, "not-created");

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(
                new JsonObject { ["path"] = "not-created/sub/f.txt", ["content"] = "x", ["create_dirs"] = "0" },
                ctx, CancellationToken.None));
        Assert.Contains("写入失败", ex.Message);
        Assert.False(Directory.Exists(parent)); // 父目录不应被自动创建
    }

    [Fact]
    public async Task WriteFile_FailedWrite_DoesNotPolluteUndoStack()
    {
        // 回归：写入失败时撤销栈不应残留条目（否则 /undo 会撤销一个从未生效的写入）
        var tool = new WriteFileTool();
        var ctx = MakeContext(_dir);

        await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(
                new JsonObject { ["path"] = "missing-dir/f.txt", ["content"] = "x", ["create_dirs"] = false },
                ctx, CancellationToken.None));

        Assert.Equal(0, ctx.Undo.Count); // 失败写入不入撤销栈
    }

    [Fact]
    public async Task EditFile_ReplaceAll_ReplacesEveryOccurrence()
    {
        File.WriteAllText(Path.Combine(_dir, "ra.txt"), "foo foo bar foo");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        await tool.ExecuteAsync(
            new JsonObject { ["path"] = "ra.txt", ["old_string"] = "foo", ["new_string"] = "baz", ["replace_all"] = true },
            ctx, CancellationToken.None);

        Assert.Equal("baz baz bar baz", File.ReadAllText(Path.Combine(_dir, "ra.txt")));
        Assert.Equal(1, ctx.Undo.Count); // 一次编辑只记录一条撤销
    }

    [Fact]
    public async Task EditFile_AmbiguousMatch_WithoutReplaceAll_Throws()
    {
        // old_string 出现多次且未指定 replace_all 时应报错，提示扩大上下文或设 replace_all
        File.WriteAllText(Path.Combine(_dir, "amb.txt"), "x y x");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(
                new JsonObject { ["path"] = "amb.txt", ["old_string"] = "x", ["new_string"] = "z" },
                ctx, CancellationToken.None));
        Assert.Contains("出现 2 次", ex.Message);
        Assert.Equal("x y x", File.ReadAllText(Path.Combine(_dir, "amb.txt"))); // 文件未被改动
        Assert.Equal(0, ctx.Undo.Count); // 失败编辑不入撤销栈
    }

    [Fact]
    public async Task EditFile_ReplaceAll_NoMatch_Throws()
    {
        // 回归：replace_all 未命中时曾静默写回原文件并报「已替换 0 处」，
        // 还往撤销栈塞了无效条目；应与单次替换一样明确报错
        File.WriteAllText(Path.Combine(_dir, "rano.txt"), "aaa bbb");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(
                new JsonObject { ["path"] = "rano.txt", ["old_string"] = "zzz", ["new_string"] = "y", ["replace_all"] = true },
                ctx, CancellationToken.None));
        Assert.Contains("未找到 old_string", ex.Message);
        Assert.Equal("aaa bbb", File.ReadAllText(Path.Combine(_dir, "rano.txt"))); // 文件未被改动
        Assert.Equal(0, ctx.Undo.Count); // 失败不入撤销栈
    }

    [Fact]
    public async Task ReadFile_OffsetBeyondEnd_ReturnsEmpty()
    {
        // offset 超过文件行数时应友好提示而非崩溃
        File.WriteAllText(Path.Combine(_dir, "short.txt"), "only one line");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "short.txt", ["offset"] = 999 }, ctx, CancellationToken.None);
        Assert.Contains("超出范围", output); // 提示超出范围
    }

    [Fact]
    public async Task ReadFile_OffsetZero_ClampsToOne()
    {
        // offset=0 或负数应收敛为 1（第 1 行）
        File.WriteAllText(Path.Combine(_dir, "clamp.txt"), "a\nb\nc");
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "clamp.txt", ["offset"] = 0 }, ctx, CancellationToken.None);
        Assert.Contains("1\ta", output);
    }

    [Fact]
    public async Task ReadFile_LimitTooLarge_ClampsTo5000()
    {
        // limit 超过 5000 应收敛到 5000（最多输出 5000 行），不崩溃
        var content = string.Join('\n', Enumerable.Range(1, 6000).Select(i => $"line{i}"));
        File.WriteAllText(Path.Combine(_dir, "big.txt"), content);
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["path"] = "big.txt", ["limit"] = 99999 }, ctx, CancellationToken.None);
        Assert.Contains("line5000", output); // 前 5000 行在内
        Assert.DoesNotContain("line6000", output); // 超出 limit 的行不输出
    }

    [Fact]
    public async Task EditFile_MissingOldString_ThrowsHelpfulError()
    {
        File.WriteAllText(Path.Combine(_dir, "nomatch.txt"), "hello world");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(
                new JsonObject { ["path"] = "nomatch.txt", ["old_string"] = "zzz", ["new_string"] = "x" },
                ctx, CancellationToken.None));
        Assert.Contains("未找到 old_string", ex.Message);
        Assert.Equal(0, ctx.Undo.Count); // 失败编辑不入撤销栈
    }

    [Fact]
    public async Task EditFile_MissingPath_Throws()
    {
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(
                new JsonObject { ["old_string"] = "a", ["new_string"] = "b" },
                ctx, CancellationToken.None));
        Assert.Contains("path", ex.Message);
    }

    [Fact]
    public async Task EditFile_MissingOldStringParam_Throws()
    {
        File.WriteAllText(Path.Combine(_dir, "e.txt"), "abc");
        var tool = new EditFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(
                new JsonObject { ["path"] = "e.txt", ["new_string"] = "b" },
                ctx, CancellationToken.None));
        Assert.Contains("old_string", ex.Message);
    }

    [Fact]
    public async Task ListDirectory_DepthZero_OnlyRoot()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "top.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "sub", "deep.txt"), "y");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var output = await tool.ExecuteAsync(
            new JsonObject { ["depth"] = 0 }, ctx, CancellationToken.None);
        Assert.Contains("sub/", output);
        Assert.DoesNotContain("deep.txt", output); // 深度 0 不进入子目录
    }

    [Fact]
    public async Task ListDirectory_FileTarget_Throws()
    {
        File.WriteAllText(Path.Combine(_dir, "f.txt"), "x");
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(new JsonObject { ["path"] = "f.txt" }, ctx, CancellationToken.None));
        Assert.Contains("是文件", ex.Message);
    }

    [Fact]
    public async Task ListDirectory_MissingDir_Throws()
    {
        var tool = new ListDirectoryTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(new JsonObject { ["path"] = "no-such" }, ctx, CancellationToken.None));
        Assert.Contains("目录不存在", ex.Message);
    }

    [Fact]
    public async Task ReadFile_MissingFile_Throws()
    {
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(new JsonObject { ["path"] = "nope.txt" }, ctx, CancellationToken.None));
        Assert.Contains("文件不存在", ex.Message);
    }

    [Fact]
    public async Task ReadFile_DirectoryTarget_Throws()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "adir"));
        var tool = new ReadFileTool();
        var ctx = MakeContext(_dir);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            tool.ExecuteAsync(new JsonObject { ["path"] = "adir" }, ctx, CancellationToken.None));
        Assert.Contains("是目录", ex.Message);
    }
}
