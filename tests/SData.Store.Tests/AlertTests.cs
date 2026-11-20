using global::Xunit;
using SData.Alerts;
using SData.Database.Entities;
using SData.Infra.Exceptions;
using SData.Store.Models;

namespace SData.Store.Tests;

public class AlertTests
{
    [Theory]
    [InlineData("body IN (SELECT body FROM logs)")]
    [InlineData("read_parquet('secret')")]
    [InlineData("body = $start")]
    [InlineData("body = 'x'; SELECT 1")]
    [InlineData("body = 'x' -- comment")]
    [InlineData("body = other.body")]
    public void Condition_RejectsUnsafeSyntax(string condition)
        => Assert.Throws<BusinessException>(() => AlertConditionCompiler.Compile("logs", condition, new Dictionary<string, object?>()));

    [Fact]
    public void Condition_BindsLiteralsAndParameters()
    {
        var compiled = AlertConditionCompiler.Compile("logs", "severity = 'ERROR' AND body LIKE $p0", new Dictionary<string, object?> { ["p0"] = "%timeout%" });
        Assert.DoesNotContain("'ERROR'", compiled.Sql);
        Assert.Contains("ERROR", compiled.Parameters.Values);
        Assert.Equal("%timeout%", compiled.Parameters["p0"]);
    }

    [Fact]
    public void Windows_AlignAndDelay()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1000);
        Assert.Equal(900, AlertState.WindowEnd(now, 5, 1));
        Assert.Equal(600, AlertState.WindowEnd(now, 5, 2));
    }

    [Fact]
    public void State_DeduplicatesAndRecovers()
    {
        var rule = new AlertRuleEntity { Threshold = 2, ConsecutiveWindows = 2, WindowMinutes = 1 };
        Assert.Null(AlertState.Apply(rule, 60, 2));
        Assert.Equal("firing", AlertState.Apply(rule, 120, 2));
        Assert.Null(AlertState.Apply(rule, 180, 3));
        Assert.Equal("resolved", AlertState.Apply(rule, 240, 0));
        Assert.Null(AlertState.Apply(rule, 300, 0));
    }

    [Fact]
    public void State_GapAndErrorResetConsecutiveHits()
    {
        var rule = new AlertRuleEntity { Threshold = 1, ConsecutiveWindows = 2, WindowMinutes = 1 };
        Assert.Null(AlertState.Apply(rule, 60, 1));
        Assert.Null(AlertState.Apply(rule, 180, 1));
        rule.Failures = 1;
        Assert.Null(AlertState.Apply(rule, 240, 1));
        Assert.Equal("firing", AlertState.Apply(rule, 300, 1));
    }
}
