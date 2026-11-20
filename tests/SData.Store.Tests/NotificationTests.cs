using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Database;
using SData.Infra.Configuration;
using SData.Infra.Exceptions;
using SData.Infra.Services;
using SData.Settings;
using SData.Settings.Handlers;
using SData.Settings.Services;
using Xunit;

namespace SData.Store.Tests;

public class NotificationTests
{
    public static IEnumerable<object[]> Channels => NotificationTypes.Fields.Keys.Select(type => new object[] { type });

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task Protocols_BuildTypedRequests_AndCheckBusinessResult(string type)
    {
        var config = Config(type);
        using var request = NotificationProtocols.Build(type, config, new NotificationMessage { Title = "标题", Body = "正文" });
        Assert.Equal(type == "matrix" ? HttpMethod.Put : HttpMethod.Post, request.Method);
        Assert.NotNull(request.RequestUri);
        Assert.NotEmpty(await request.Content!.ReadAsStringAsync());
        var response = type switch
        {
            "telegram" => """{"ok":true}""",
            "discord" or "webhook" => "",
            "feishu" or "serverchan" or "pushdeer" => """{"code":0}""",
            "dingtalk" or "wecom" => """{"errcode":0}""",
            "bark" => """{"code":200}""",
            "gotify" => """{"id":123}""",
            "matrix" => """{"event_id":"$test"}""",
            _ => throw new InvalidOperationException(),
        };
        Assert.True(NotificationProtocols.IsSuccess(type, 200, response));
        Assert.False(NotificationProtocols.IsSuccess(type, 429, response));
        if (type is not ("discord" or "webhook"))
        {
            Assert.False(NotificationProtocols.IsSuccess(type, 200, """{"code":400,"errcode":400,"ok":false}"""));
            Assert.False(NotificationProtocols.IsSuccess(type, 200, "not-json"));
            Assert.False(NotificationProtocols.IsSuccess(type, 200, "[]"));
            Assert.False(NotificationProtocols.IsSuccess(type, 200, """{"code":"invalid","errcode":"invalid"}"""));
        }

        if (type == "discord") Assert.Contains("allowed_mentions", await request.Content.ReadAsStringAsync());
        if (type == "matrix") Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        if (type == "gotify") Assert.True(request.Headers.Contains("X-Gotify-Key"));
    }

    [Fact]
    public async Task CustomTemplate_EscapesValues_WithoutRecursiveExpansion()
    {
        var config = Config("webhook");
        config["template"] = """{"message":"{{title}}","items":["{{body}}"],"count":2}""";
        config["headers"] = """{"Authorization":"Bearer example"}""";
        using var request = NotificationProtocols.Build("webhook", config, new NotificationMessage { Title = "{{body}}", Body = "\"quoted\"\ntext" });
        using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert.Equal("{{body}}", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal("\"quoted\"\ntext", doc.RootElement.GetProperty("items")[0].GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public void Configuration_ProtectsSecrets_AndEnforcesExplicitKeep()
    {
        var configuration = new NotificationConfiguration(new EphemeralDataProtectionProvider());
        var config = Config("feishu");
        var protectedValue = configuration.Protect(config);
        Assert.DoesNotContain(config["webhook"], protectedValue);
        Assert.Equal(config, configuration.Unprotect(protectedValue));
        var command = new SaveNotificationChannelCommand { Type = "feishu", KeepSecrets = ["webhook"], Config = new() { ["secret"] = "new-secret" } };
        Assert.Equal(config["webhook"], configuration.Merge(command, config)["webhook"]);
        Assert.Equal("new-secret", configuration.Merge(command, config)["secret"]);
        Assert.Throws<BusinessException>(() => configuration.Merge(command, null));
        command.Config["webhook"] = "conflicting";
        Assert.Throws<BusinessException>(() => configuration.Merge(command, config));
        command.Type = "webhook";
        command.KeepSecrets = [];
        command.Config = new() { ["webhook"] = "https://hooks.example/test", ["headers"] = "{invalid" };
        Assert.Throws<BusinessException>(() => configuration.Merge(command, null));
        command.Config["headers"] = """{"Host":"internal"}""";
        Assert.Throws<BusinessException>(() => configuration.Merge(command, null));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("168.63.129.16", true)]
    [InlineData("fd00:ec2::254", true)]
    [InlineData("10.1.2.3", false)]
    [InlineData("fd00::123", false)]
    [InlineData("8.8.8.8", false)]
    public void Transport_RejectsMetadataAndLoopback_EvenWhenAllowed(string address, bool blocked)
    {
        Assert.Equal(blocked, NotificationTransport.IsForbidden(IPAddress.Parse(address), true));
        if (address is "10.1.2.3" or "fd00::123") Assert.True(NotificationTransport.IsForbidden(IPAddress.Parse(address), false));
    }

    [Fact]
    public void Transport_RequiresExactOrigin_AndOfficialHttps()
    {
        var transport = new NotificationTransport(new SystemOptions { NotificationAllowedOrigins = ["http://gotify.internal:8080"] });
        transport.Validate("gotify", new Uri("http://gotify.internal:8080/message"));
        transport.Validate("telegram", new Uri("https://api.telegram.org/bot123:fake/sendMessage"));
        Assert.Throws<BusinessException>(() => transport.Validate("telegram", new Uri("http://api.telegram.org/bot123:fake/sendMessage")));
        Assert.Throws<BusinessException>(() => transport.Validate("webhook", new Uri("http://gotify.internal:8081/message")));
        Assert.Throws<BusinessException>(() => transport.Validate("webhook", new Uri("http://gotify.internal.evil:8080/message")));
        Assert.Throws<BusinessException>(() => transport.Validate("webhook", new Uri("http://user:pass@gotify.internal:8080/message")));
    }

    [Fact]
    public async Task Management_PersistsEncryptedConfig_Versions_SoftDeletes_AndSendsThroughSharedContract()
    {
        await using var database = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseSqlite("Data Source=:memory:").Options);
        await database.Database.OpenConnectionAsync();
        await database.Database.EnsureCreatedAsync();
        var configuration = new NotificationConfiguration(new EphemeralDataProtectionProvider());
        var transport = new FakeTransport();
        var service = new NotificationService(database, new DefaultIdProvider(1), configuration, transport, NullLogger<NotificationService>.Instance);
        var command = new SaveNotificationChannelCommand { Name = "测试渠道", Type = "telegram", Enabled = true, Config = Config("telegram") };
        var item = await service.SaveAsync(command, default);
        Assert.DoesNotContain("token", item.Config.Keys);
        Assert.Contains("token", item.ConfiguredSecrets);
        var entity = await database.NotificationChannels.SingleAsync();
        Assert.DoesNotContain(command.Config["token"], entity.ProtectedConfig);
        Assert.NotEqual(default, item.CreateTime);
        command.Id = item.Id;
        command.Version = item.Version;
        command.Config = new() { ["chatId"] = "123" };
        command.KeepSecrets = ["token"];
        item = await service.SaveAsync(command, default);
        Assert.Equal(2, item.Version);
        await Assert.ThrowsAsync<BusinessException>(() => service.SaveAsync(command, default));
        INotificationSender sender = service;
        Assert.True((await sender.SendAsync(long.Parse(item.Id), new NotificationMessage { Title = "test", Body = "body" }, default)).Success);
        Assert.Equal(1, transport.Sends);
        command.Version = item.Version;
        command.Enabled = false;
        item = await service.SaveAsync(command, default);
        Assert.False((await sender.SendAsync(long.Parse(item.Id), new NotificationMessage { Title = "test" }, default)).Success);
        await service.DeleteAsync(new DeleteNotificationChannelCommand { Id = item.Id, Version = item.Version }, default);
        Assert.Empty(await service.QueryAsync(default));
        Assert.Single(await database.NotificationChannels.IgnoreQueryFilters().ToListAsync());
        Assert.False((await sender.SendAsync(long.Parse(item.Id), new NotificationMessage { Title = "test" }, default)).Success);
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public async Task Management_AllTypesSupportMultipleNamedInstances(string type)
    {
        await using var database = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseSqlite("Data Source=:memory:").Options);
        await database.Database.OpenConnectionAsync();
        await database.Database.EnsureCreatedAsync();
        var configuration = new NotificationConfiguration(new EphemeralDataProtectionProvider());
        var transport = new NotificationTransport(new SystemOptions { NotificationAllowedOrigins = ["https://notify.example", "https://hooks.example"] });
        var service = new NotificationService(database, new DefaultIdProvider(1), configuration, transport, NullLogger<NotificationService>.Instance);
        var first = await service.SaveAsync(new SaveNotificationChannelCommand { Name = "生产通知", Type = type, Enabled = true, Config = Config(type) }, default);
        var second = await service.SaveAsync(new SaveNotificationChannelCommand { Name = "测试通知", Type = type, Enabled = false, Config = Config(type) }, default);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, (await service.QueryAsync(default)).Count);
        Assert.True(first.Enabled);
        Assert.False(second.Enabled);
        await Assert.ThrowsAsync<BusinessException>(() => service.SaveAsync(new SaveNotificationChannelCommand
        {
            Name = " 生产通知 ", Type = type, Config = Config(type),
        }, default));
        await Assert.ThrowsAsync<BusinessException>(() => service.SaveAsync(new SaveNotificationChannelCommand
        {
            Id = second.Id, Version = second.Version, Name = first.Name, Type = type, Config = Config(type),
        }, default));
        var updated = await service.SaveAsync(new SaveNotificationChannelCommand
        {
            Id = first.Id, Version = first.Version, Name = first.Name, Type = type, Config = Config(type), Enabled = true,
        }, default);
        Assert.Equal(first.Id, updated.Id);
        Assert.Equal(second.Name, (await service.QueryAsync(default)).Single(x => x.Id == second.Id).Name);
    }

    [Fact]
    public async Task DraftTest_DoesNotPersist_AndSanitizesRemoteFailures()
    {
        await using var database = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseSqlite("Data Source=:memory:").Options);
        await database.Database.OpenConnectionAsync();
        await database.Database.EnsureCreatedAsync();
        var transport = new FakeTransport { Fail = true };
        var service = new NotificationService(database, new DefaultIdProvider(1), new NotificationConfiguration(new EphemeralDataProtectionProvider()), transport, NullLogger<NotificationService>.Instance);
        var result = await service.TestAsync(new TestNotificationChannelCommand { Name = "draft", Type = "telegram", Config = Config("telegram") }, default);
        Assert.False(result.Success);
        Assert.DoesNotContain("private-token", result.Message);
        Assert.Empty(await database.NotificationChannels.ToListAsync());
    }

    private static Dictionary<string, string> Config(string type)
    {
        var values = new Dictionary<string, string>
        {
            ["token"] = "123:fake-token", ["chatId"] = "123", ["secret"] = "signing-secret",
            ["webhook"] = type switch
            {
                "discord" => "https://discord.com/api/webhooks/123/fake",
                "feishu" => "https://open.feishu.cn/open-apis/bot/v2/hook/fake",
                "dingtalk" => "https://oapi.dingtalk.com/robot/send?access_token=fake",
                "wecom" => "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=fake",
                _ => "https://hooks.example/notify",
            },
            ["server"] = "https://notify.example",
            ["roomId"] = "!room:example.org", ["headers"] = "{}", ["template"] = "{}",
        };
        if (type == "serverchan") values["token"] = "SCTfake";
        return NotificationTypes.Fields[type].ToDictionary(key => key, key => values[key]);
    }

    private sealed class FakeTransport() : NotificationTransport(new SystemOptions())
    {
        public int Sends { get; private set; }
        public bool Fail { get; set; }

        public override Task<(HttpStatusCode Status, string Body)> SendAsync(string type, HttpRequestMessage request, CancellationToken token)
        {
            Sends++;
            if (Fail) throw new HttpRequestException("https://private-token.example");
            return Task.FromResult((HttpStatusCode.OK, """{"ok":true}"""));
        }
    }
}
