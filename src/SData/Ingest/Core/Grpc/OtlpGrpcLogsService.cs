using Google.Protobuf;
using Grpc.Core;
using OpenTelemetry.Proto.Collector.Logs.V1;
using SData.Ingest;
using SData.Store.Models;

namespace SData.Ingest.OtlpGrpc;

/// <summary>
/// OTLP gRPC logs 服务（/opentelemetry.proto.collector.logs.v1.LogsService/Export）.
/// 复用 OTLP/HTTP 同一条解析与摄取链（T111 的 OtlpProtoParser），保证两种通道语义一致.
/// 落位 Core：proto 生成物与 Grpc 包在 Core（T110），且 Api→Shared 单向分层下 Api 无法引用生成类型.
/// </summary>
public class OtlpGrpcLogsService : LogsService.LogsServiceBase
{
    private readonly IIngestIngestor _ingestor;
    private readonly IOtlpProtoParser _protoParser;

    /// <summary>
    /// Initializes a new instance of the <see cref="OtlpGrpcLogsService"/> class.
    /// </summary>
    /// <param name="ingestor">接入服务.</param>
    /// <param name="protoParser">OTLP proto 解析器.</param>
    public OtlpGrpcLogsService(IIngestIngestor ingestor, IOtlpProtoParser protoParser)
    {
        _ingestor = ingestor;
        _protoParser = protoParser;
    }

    /// <inheritdoc/>
    public override Task<ExportLogsServiceResponse> Export(ExportLogsServiceRequest request, ServerCallContext context)
    {
        var records = _protoParser.ParseLogs(request.ToByteArray());
        var rejected = 0;
        foreach (var record in records)
        {
            if (!_ingestor.EnqueueLog(record))
            {
                rejected++;
            }
        }

        var response = new ExportLogsServiceResponse();
        if (rejected > 0)
        {
            response.PartialSuccess ??= new ExportLogsPartialSuccess();
            response.PartialSuccess.RejectedLogRecords = rejected;
        }

        return Task.FromResult(response);
    }
}
