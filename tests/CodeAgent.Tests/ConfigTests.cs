using System;
using System.IO;
using CodeAgent;
using Xunit;

namespace CodeAgent.Tests;

public class ConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "codeagent-config-" + Guid.NewGuid().ToString("N"));

    public ConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 忽略 */ }
    }

    [Fact]
    public void Load_ClampsInvalidBounds()
    {
        // 回归：maxToolIterations=0 曾导致 Agent 空转一轮；加载时应收敛到合法值
        var path = Path.Combine(_dir, "codeagent.json");
        File.WriteAllText(path, """
            {
              "provider": "openai",
              "providers": { "openai": { "type": "openai", "model": "gpt-4o" } },
              "maxToolIterations": 0,
              "maxHistoryChars": 10
            }
            """);

        var cfg = AgentConfig.Load(path);
        Assert.Equal(1, cfg.MaxToolIterations);   // 0 → 1
        Assert.Equal(1_000, cfg.MaxHistoryChars); // 10 → 1000
    }

    [Fact]
    public void Load_MissingFile_Throws()
    {
        var path = Path.Combine(_dir, "nope.json");
        Assert.Throws<FileNotFoundException>(() => AgentConfig.Load(path));
    }

    [Fact]
    public void Load_InvalidJson_ThrowsInvalidData()
    {
        var path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, "{ not json !!");
        Assert.Throws<InvalidDataException>(() => AgentConfig.Load(path));
    }
}
