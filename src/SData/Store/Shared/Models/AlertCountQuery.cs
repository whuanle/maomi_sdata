namespace SData.Store.Models;

/// <summary>单域、受限热数据告警计数；Count 达到 Cap 时仅代表下界.</summary>
public record AlertCountQuery(string Domain, string Condition, IReadOnlyDictionary<string, object?> Parameters, DateTimeOffset From, DateTimeOffset To, long Cap);

public record AlertCountResult(long Count, bool LowerBound);
