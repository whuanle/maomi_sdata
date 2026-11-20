using global::Xunit;
using SData.Metrics.Promql;

namespace SData.Metrics.Tests;

/// <summary>
/// PromQL 子集解析测试（T710）：≥20 例（任务卡验收），覆盖选择器/匹配器/函数/聚合/offset/非法拒绝.
/// </summary>
public class PromqlParserTests
{
    private static PromqlNode Parse(string expr) => PromqlParser.Parse(expr);

    // ---------- 合法表达式 ----------

    [Theory]
    [InlineData("http_requests_total")]
    [InlineData("node_cpu_seconds_total")]
    [InlineData("_private:rate5m")]
    [InlineData("up{job=\"node\"}")]
    [InlineData("up{job=\"node\",instance!=\"localhost:9100\"}")]
    [InlineData("up{job=~\"node|gateway\",env!~\"prod.*\"}")]
    [InlineData("rate(http_requests_total[5m])")]
    [InlineData("rate(up{job=\"api\"}[1h])")]
    [InlineData("rate(http_requests_total[30s] offset 5m)")]
    [InlineData("sum(http_requests_total)")]
    [InlineData("sum by (service) (rate(http_requests_total[5m]))")]
    [InlineData("sum without (instance) (rate(http_requests_total[5m]))")]
    [InlineData("avg by (service, route) (rate(http_requests_total[10m]))")]
    [InlineData("min(rate(node_memory_free_bytes[5m]))")]
    [InlineData("max(rate(node_memory_free_bytes[5m]))")]
    [InlineData("count(up)")]
    [InlineData("topk(3, rate(http_requests_total[5m]))")]
    [InlineData("topk by (service) (5, rate(http_requests_total[5m]))")]
    [InlineData("bottomk(2, rate(http_requests_total[5m]))")]
    [InlineData("count by (job) (up{env=\"prod\"})")]
    [InlineData("sum(rate(http_requests_total[1d] offset 1w))")]
    [InlineData("42")]
    [InlineData("rate(http_requests_total[1.5h])")]
    [InlineData("up{job=\"spaces in \\\"value\\\"\"}")]
    public void Parse_ValidExpressions_ReturnsExpectedShape(string expr)
    {
        var node = Parse(expr); // 不抛即通过

        // 关键形状抽查
        switch (expr)
        {
            case "http_requests_total":
                var selector = Assert.IsType<PromqlVectorSelector>(node);
                Assert.Equal("http_requests_total", selector.MetricName);
                Assert.Null(selector.Range);
                break;
            case "rate(http_requests_total[5m])":
                var call = Assert.IsType<PromqlCall>(node);
                Assert.Equal("rate", call.Function);
                var range = Assert.IsType<PromqlVectorSelector>(call.Argument);
                Assert.Equal(TimeSpan.FromMinutes(5), range.Range);
                break;
            case "topk(3, rate(http_requests_total[5m]))":
                var topk = Assert.IsType<PromqlAggregate>(node);
                Assert.Equal(3, topk.Param);
                Assert.Null(topk.GroupingLabels);
                break;
            case "sum by (service) (rate(http_requests_total[5m]))":
                var agg = Assert.IsType<PromqlAggregate>(node);
                Assert.Equal("sum", agg.Operator);
                Assert.Equal(["service"], agg.GroupingLabels);
                Assert.False(agg.Without);
                break;
            case "sum without (instance) (rate(http_requests_total[5m]))":
                var without = Assert.IsType<PromqlAggregate>(node);
                Assert.True(without.Without);
                Assert.Equal(["instance"], without.GroupingLabels);
                break;
            case "42":
                Assert.Equal(42, Assert.IsType<PromqlNumber>(node).Value);
                break;
        }
    }

    [Fact]
    public void Parse_LabelMatchers_PreserveOperators()
    {
        var node = Assert.IsType<PromqlVectorSelector>(Parse("m{a=\"1\",b!=\"2\",c=~\"r\",d!~\"n\"}"));
        Assert.Equal(4, node.Matchers.Count);
        Assert.Equal("=", node.Matchers[0].Operator);
        Assert.Equal("!=", node.Matchers[1].Operator);
        Assert.Equal("=~", node.Matchers[2].Operator);
        Assert.Equal("!~", node.Matchers[3].Operator);
    }

    [Fact]
    public void Parse_Offset_PreservedOnSelector()
    {
        var node = Assert.IsType<PromqlVectorSelector>(Parse("up offset 10m"));
        Assert.Equal(TimeSpan.FromMinutes(10), node.Offset);
        Assert.Null(node.Range);
    }

    // ---------- 非法表达式拒绝 ----------

    [Theory]
    [InlineData("", "表达式为空")]
    [InlineData("   ", "表达式为空")]
    [InlineData("sum", "缺少参数括号")]
    [InlineData("sum(", "表达式为空")]
    [InlineData("rate(http_requests_total{job=}[5m])", "带引号字符串")]
    [InlineData("rate(http_requests_total[5m)", "范围向量缺少右方括号")]
    [InlineData("http_requests_total{job=node}", "带引号字符串")]
    [InlineData("topk(rate(x[5m]))", "topk/bottomk 需要数量参数")]
    [InlineData("up offset", "时长必须是数字")]
    [InlineData("up[5x]", "未知时长单位")]
    [InlineData("up + 1", "非法字符")]
    [InlineData("up{job=\"unterminated}", "字符串未闭合")]
    [InlineData("(up", "缺少右括号")]
    [InlineData("\"only a string\"", "意外的符号")]
    [InlineData("sum by (a b) (up)", "标签列表缺少右括号")]
    public void Parse_InvalidExpressions_ThrowsWithChineseMessage(string expr, string expectedFragment)
    {
        var ex = Assert.Throws<PromqlParseException>(() => Parse(expr));
        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_SumByEmptyLabels_StillParsesLabelList()
    {
        // 空标签列表 by () 是合法 PromQL（按无标签分组）
        var agg = Assert.IsType<PromqlAggregate>(Parse("sum by () (up)"));
        Assert.Empty(agg.GroupingLabels!);
    }
}
