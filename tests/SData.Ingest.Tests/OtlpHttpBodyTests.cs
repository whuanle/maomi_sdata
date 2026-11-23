using System.IO.Compression;
using System.Text;
using global::Xunit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SData.Ingest.Api;

namespace SData.Ingest.Tests;

public class OtlpHttpBodyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportAsync_JsonBodySupportsPlainAndGzip(bool compressed)
    {
        const string json = """{"resourceSpans":[]}""";
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(compressed ? Gzip(Encoding.UTF8.GetBytes(json)) : Encoding.UTF8.GetBytes(json));
        if (compressed)
        {
            context.Request.Headers.ContentEncoding = "gzip";
        }

        string? parsed = null;
        var result = await OtlpV1Controller.ExportAsync(
            context.Request,
            (_, _, _) => throw new InvalidOperationException("JSON request was parsed as protobuf"),
            text => { parsed = text; return new List<string> { text }; },
            records => ((long)records.Count, 0L));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(json, parsed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExportAsync_ProtoBodySupportsPlainAndGzip(bool compressed)
    {
        var bytes = new byte[] { 0x0A, 0x01, 0x7B };
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/x-protobuf";
        context.Request.Body = new MemoryStream(compressed ? Gzip(bytes) : bytes);
        if (compressed)
        {
            context.Request.Headers.ContentEncoding = "gzip";
        }

        byte[]? parsed = null;
        var result = await OtlpV1Controller.ExportAsync(
            context.Request,
            (buffer, offset, count) => { parsed = buffer.Skip(offset).Take(count).ToArray(); return new List<string> { "parsed" }; },
            _ => throw new InvalidOperationException("Protobuf request was parsed as JSON"),
            records => ((long)records.Count, 0L));

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(bytes, parsed);
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            gzip.Write(bytes);
        }

        return output.ToArray();
    }
}
