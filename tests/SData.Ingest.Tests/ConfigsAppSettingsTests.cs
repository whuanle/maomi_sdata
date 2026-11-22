using Xunit;

namespace SData.Ingest.Tests;

/// <summary>
/// 首次启动补齐 configs/appsettings.json：缺失时复制内容根的默认配置，已存在的覆盖配置保持不变.
/// </summary>
public class ConfigsAppSettingsTests : IDisposable
{
    private const string DefaultSettings = "{\"SData\":{\"Port\":5300}}";
    private const string UserSettings = "{\"SData\":{\"Port\":5301}}";

    private readonly string _contentRoot = Path.Combine(Path.GetTempPath(), "sdata-configs-" + Guid.NewGuid().ToString("N"));

    public ConfigsAppSettingsTests()
    {
        Directory.CreateDirectory(_contentRoot);
    }

    public void Dispose()
    {
        Directory.Delete(_contentRoot, recursive: true);
    }

    /// <summary>configs 目录不存在时创建目录并复制默认配置.</summary>
    [Fact]
    public void CreatesConfigsDirectoryAndCopiesDefaultAppSettings()
    {
        File.WriteAllText(Path.Combine(_contentRoot, "appsettings.json"), DefaultSettings);

        MainExtensions.EnsureConfigsAppSettings(_contentRoot);

        Assert.Equal(DefaultSettings, File.ReadAllText(Path.Combine(_contentRoot, "configs", "appsettings.json")));
    }

    /// <summary>Docker 挂载的空 configs 目录同样复制默认配置.</summary>
    [Fact]
    public void CopiesDefaultAppSettingsIntoEmptyConfigsDirectory()
    {
        File.WriteAllText(Path.Combine(_contentRoot, "appsettings.json"), DefaultSettings);
        Directory.CreateDirectory(Path.Combine(_contentRoot, "configs"));

        MainExtensions.EnsureConfigsAppSettings(_contentRoot);

        Assert.Equal(DefaultSettings, File.ReadAllText(Path.Combine(_contentRoot, "configs", "appsettings.json")));
    }

    /// <summary>已有覆盖配置时不覆盖，用户修改在重启后保留.</summary>
    [Fact]
    public void DoesNotOverwriteExistingConfigsAppSettings()
    {
        File.WriteAllText(Path.Combine(_contentRoot, "appsettings.json"), DefaultSettings);
        var configs = Path.Combine(_contentRoot, "configs");
        Directory.CreateDirectory(configs);
        File.WriteAllText(Path.Combine(configs, "appsettings.json"), UserSettings);

        MainExtensions.EnsureConfigsAppSettings(_contentRoot);

        Assert.Equal(UserSettings, File.ReadAllText(Path.Combine(configs, "appsettings.json")));
    }

    /// <summary>内容根没有默认配置（如从其它目录启动）时不做任何事，也不创建 configs 目录.</summary>
    [Fact]
    public void DoesNothingWithoutDefaultAppSettings()
    {
        MainExtensions.EnsureConfigsAppSettings(_contentRoot);

        Assert.False(Directory.Exists(Path.Combine(_contentRoot, "configs")));
    }
}
