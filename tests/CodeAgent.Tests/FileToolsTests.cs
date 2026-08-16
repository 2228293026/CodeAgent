using System;
using System.IO;
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
}
