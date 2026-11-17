using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TransferProof.Core.Abstractions;
using TransferProof.Infrastructure;

namespace TransferProof.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public TestDirectory Directory { get; } = new();
    public ConcurrentQueue<string> Webhooks { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["ConnectionStrings:Database"] = "Data Source=" + Directory.File("test.db") + ";Pooling=False", ["ConnectionStrings:Redis"] = "",
            ["Storage:Root"] = Directory.File("storage"), ["Signing:PrivateKeyPemPath"] = Directory.File("key.pem"),
            ["Extraction:Provider"] = "Embedded", ["Security:MerchantApiKey"] = "test-merchant", ["Security:TokenKey"] = Convert.ToBase64String(new byte[32]),
            ["Webhook:Url"] = "https://merchant.test/events", ["Webhook:RetryDelaysSeconds:0"] = "0", ["Webhook:RetryDelaysSeconds:1"] = "0", ["Webhook:RetryDelaysSeconds:2"] = "0"
        }));
        builder.ConfigureServices(services => services.AddHttpClient("Webhook").ConfigurePrimaryHttpMessageHandler(() => new StubHandler(async (request, ct) => {
            Webhooks.Enqueue(await request.Content!.ReadAsStringAsync(ct)); return StubHandler.Json("{}");
        })));
    }
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) Directory.Dispose(); }
}

public sealed class ApiTests
{
    private static HttpClient Merchant(ApiFactory factory)
    {
        var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-Api-Key", "test-merchant"); return client;
    }
    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }
    private static async Task<(JsonElement Session, string Token)> SessionAsync(HttpClient client, string order = "demo-001")
    {
        await ReadAsync(await client.PostAsJsonAsync("/api/orders", new { id = order, buyerName = "Alex Chen", sellerName = "Jamie Lin", sellerBank = "Demo Bank", sellerAccount = "110-234-567890", amount = 150000, currency = "TWD" }));
        var link = await ReadAsync(await client.PostAsJsonAsync("/api/capture-links", new { orderId = order }));
        var token = Uri.UnescapeDataString(link.GetProperty("deepLink").GetString()!.Split("token=", StringSplitOptions.None)[1]);
        return (await ReadAsync(await client.PostAsJsonAsync("/api/capture-sessions/exchange", new { token })), token);
    }
    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, JsonElement session, decimal amount = 150000, bool loading = false)
    {
        using var content = new MultipartFormDataContent(); using var image = new ByteArrayContent(TestMedia.Png(TestMedia.Extraction(TimeProvider.System.GetUtcNow(), amount), loading));
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png"); content.Add(image, "file", "sample.png");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/capture-sessions/{session.GetProperty("sessionId").GetString()}/proof") { Content = content };
        request.Headers.Add("X-Session-Token", session.GetProperty("sessionToken").GetString()); return await client.SendAsync(request);
    }
    private static async Task<JsonElement> PollAsync(HttpClient client, string url)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var result = await ReadAsync(await client.GetAsync(url, timeout.Token));
            if (result.GetProperty("status").GetString() is "Completed" or "Failed") return result;
            await Task.Delay(50, timeout.Token);
        }
    }
    [Fact]
    public async Task Verify_FullWorkflow_MatchesAndRejectsUsedToken()
    {
        using var factory = new ApiFactory(); using var client = Merchant(factory); var (session, token) = await SessionAsync(client);
        using var replay = await client.PostAsJsonAsync("/api/capture-sessions/exchange", new { token });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal("token_used", JsonSerializer.Deserialize<JsonElement>(await replay.Content.ReadAsStringAsync()).GetProperty("code").GetString());
        var accepted = await ReadAsync(await UploadAsync(client, session));
        var result = await PollAsync(client, accepted.GetProperty("statusUrl").GetString()!);
        Assert.Equal("Matched", result.GetProperty("verdict").GetString()); Assert.Equal(JsonValueKind.Null, result.GetProperty("disputeId").ValueKind);
        Assert.Single(result.GetProperty("attempts").EnumerateArray());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (factory.Webhooks.IsEmpty) await Task.Delay(20, timeout.Token);
        var webhook = JsonSerializer.Deserialize<JsonElement>(Assert.Single(factory.Webhooks));
        var jwks = await client.GetFromJsonAsync<JsonWebKeySet>("/.well-known/jwks.json");
        Assert.True(JwsVerifier.Verify(webhook.GetProperty("jws").GetString()!, jwks!));
    }
    [Theory]
    [InlineData(false, "Mismatched")][InlineData(true, "Unreadable")]
    public async Task Verify_WrongAmountOrLoading_OpensDispute(bool loading, string verdict)
    {
        using var factory = new ApiFactory(); using var client = Merchant(factory); var (session, _) = await SessionAsync(client);
        var accepted = await ReadAsync(await UploadAsync(client, session, 149999, loading));
        var result = await PollAsync(client, accepted.GetProperty("statusUrl").GetString()!);
        Assert.Equal(verdict, result.GetProperty("verdict").GetString());
        var disputes = await ReadAsync(await client.GetAsync("/api/disputes?status=Open"));
        var dispute = Assert.Single(disputes.EnumerateArray()); Assert.Equal(result.GetProperty("disputeId").GetGuid(), dispute.GetProperty("id").GetGuid());
        using var resolved = await client.PostAsync($"/api/disputes/{dispute.GetProperty("id").GetGuid()}/resolve", null); Assert.Equal(HttpStatusCode.NoContent, resolved.StatusCode);
        var open = await ReadAsync(await client.GetAsync("/api/disputes?status=Open")); Assert.Empty(open.EnumerateArray());
    }
    [Fact]
    public async Task UploadAsync_RepeatedSession_ReturnsConflict()
    {
        using var factory = new ApiFactory(); using var client = Merchant(factory); var (session, _) = await SessionAsync(client);
        using var first = await UploadAsync(client, session); Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        using var second = await UploadAsync(client, session); Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("session_already_used", JsonSerializer.Deserialize<JsonElement>(await second.Content.ReadAsStringAsync()).GetProperty("code").GetString());
    }
    [Fact]
    public async Task CreateOrder_MissingApiKey_ReturnsUnauthorized()
    {
        using var factory = new ApiFactory(); using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/orders", new { id = "test" }); Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
    [Fact]
    public async Task GetJwks_PublicEndpoint_ReturnsOneP256Key()
    {
        using var factory = new ApiFactory(); using var client = factory.CreateClient();
        var keys = await client.GetFromJsonAsync<JsonWebKeySet>("/.well-known/jwks.json");
        var key = Assert.Single(keys!.keys); Assert.Equal("EC", key.kty); Assert.Equal("P-256", key.crv); Assert.Equal(32, TokenEncoding.Decode(key.x).Length);
    }
}
