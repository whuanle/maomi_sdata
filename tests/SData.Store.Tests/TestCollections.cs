global using Xunit;

namespace SData.Store.Tests;

/// <summary>
/// 依赖 AppConst.ContentRoot（静态全局）与独立 DuckDB 实例的测试统一串行执行，
/// 避免并行类互相覆盖 ContentRoot 导致的库路径竞争（T613 修复：全项目并行时 RumStoreTests/ArchiveTests 偶发互踩）.
/// </summary>
[CollectionDefinition("content-root-serial", DisableParallelization = true)]
public sealed class ContentRootSerialCollection
{
}
