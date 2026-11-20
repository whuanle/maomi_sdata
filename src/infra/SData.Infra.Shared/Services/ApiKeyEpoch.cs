namespace SData.Infra.Services;

/// <summary>单机凭据撤销版本；提交成功后递增，避免在途校验回填旧缓存.</summary>
public static class ApiKeyEpoch
{
    private static long _version;
    public static long Version => Interlocked.Read(ref _version);
    public static void Invalidate() => Interlocked.Increment(ref _version);
}
