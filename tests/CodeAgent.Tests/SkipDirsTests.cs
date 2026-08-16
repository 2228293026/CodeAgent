using System;
using System.IO;
using System.Linq;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

public class SkipDirsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-skips-" + Guid.NewGuid().ToString("N"));

    public SkipDirsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
    }

    [Fact]
    public void EnumerateFilesPruned_SkipsBuildAndCacheDirs()
    {
        // 布局：src/a.cs、src/deep/b.cs、node_modules/x.cs、bin/y.cs（都应被剪枝跳过）
        Directory.CreateDirectory(Path.Combine(_dir, "src", "deep"));
        Directory.CreateDirectory(Path.Combine(_dir, "node_modules"));
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "src", "a.cs"), "a");
        File.WriteAllText(Path.Combine(_dir, "src", "deep", "b.cs"), "b");
        File.WriteAllText(Path.Combine(_dir, "node_modules", "x.cs"), "x");
        File.WriteAllText(Path.Combine(_dir, "bin", "y.cs"), "y");

        var files = SkipDirs.EnumerateFilesPruned(_dir)
            .Select(f => Path.GetRelativePath(_dir, f).Replace('\\', '/'))
            .OrderBy(x => x)
            .ToList();

        Assert.Contains("src/a.cs", files);
        Assert.Contains("src/deep/b.cs", files);
        Assert.DoesNotContain(files, f => f.Contains("node_modules") || f.Contains("/bin/"));
    }

    [Fact]
    public void EnumerateFilesPruned_SkipsVenvAndTerraform()
    {
        // 回归：常见语言的缓存目录也应剪枝
        Directory.CreateDirectory(Path.Combine(_dir, ".venv", "lib"));
        Directory.CreateDirectory(Path.Combine(_dir, ".terraform", "modules"));
        Directory.CreateDirectory(Path.Combine(_dir, ".pytest_cache"));
        File.WriteAllText(Path.Combine(_dir, ".venv", "lib", "x.py"), "x");
        File.WriteAllText(Path.Combine(_dir, ".terraform", "modules", "m.tf"), "m");
        File.WriteAllText(Path.Combine(_dir, ".pytest_cache", "c.py"), "c");
        File.WriteAllText(Path.Combine(_dir, "real.py"), "r");

        var files = SkipDirs.EnumerateFilesPruned(_dir)
            .Select(f => Path.GetRelativePath(_dir, f).Replace('\\', '/'))
            .ToList();

        Assert.Contains("real.py", files);
        Assert.DoesNotContain(files, f => f.Contains(".venv") || f.Contains(".terraform") || f.Contains(".pytest_cache"));
    }
}
