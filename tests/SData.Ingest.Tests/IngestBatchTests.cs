using SData.Ingest.Services;
using SData.Infra.Configuration;
using SData.Store;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace SData.Ingest.Tests;

public class IngestBatchTests
{
    [Fact]
    public void SimpleJsonAllTypesReturnBatchAcceptedCounts()
    {
        var options = new SystemOptions();
        options.Ingest.BufferCapacity = 32;
        var buffer = new IngestBuffer(options);
        var ingestor = new IngestService(buffer);

        var logs = ingestor.IngestLogs([
            new IngestLogItem { Body = "one" },
            new IngestLogItem { Body = "two" },
        ]);
        var spans = ingestor.IngestSpans([
            new IngestSpanItem { TraceId = "trace", SpanId = "span", Name = "operation" },
        ]);
        var metrics = ingestor.IngestMetrics([
            new IngestMetricItem { Name = "cpu", Value = 42 },
        ]);
        var telemetry = ingestor.IngestTelemetry([
            new IngestTelemetryItem { DeviceKey = "device", Signal = "temperature", NumValue = 30 },
        ]);

        Assert.Equal(2, logs.Accepted);
        Assert.Equal(1, spans.Accepted);
        Assert.Equal(1, metrics.Accepted);
        Assert.Equal(1, telemetry.Accepted);
        Assert.Equal(0, logs.Dropped + spans.Dropped + metrics.Dropped + telemetry.Dropped);
        Assert.Equal(5, buffer.Reader.Count);
    }

    [Fact]
    public void SimpleJsonPreflightReturns429WithRetryAfter()
    {
        var options = new SystemOptions();
        options.Ingest.BufferCapacity = 32;
        options.Ingest.BufferByteBudgetBytes = 63;
        var buffer = new IngestBuffer(options);
        var ingestor = new IngestService(buffer);
        var controller = new SData.Ingest.Api.IngestController(ingestor, null!, null!);
        var context = new DefaultHttpContext();
        context.Request.ContentLength = 64;
        controller.ControllerContext = new ControllerContext { HttpContext = context };

        var result = controller.Logs([
            new IngestLogItem { Body = "must not enqueue" },
        ]);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(429, objectResult.StatusCode);
        Assert.Equal("1", context.Response.Headers["Retry-After"]);
        Assert.Equal(0, buffer.Reader.Count);
    }
}
