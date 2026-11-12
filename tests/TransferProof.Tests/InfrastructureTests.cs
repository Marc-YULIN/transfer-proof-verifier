using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TransferProof.Core.Abstractions;
using TransferProof.Core.Domain;
using TransferProof.Infrastructure;

namespace TransferProof.Tests;

public sealed class InfrastructureTests
{
    [Fact]
    public void TryUnprotect_RoundTripTamperExpiryAndWrongKey_RejectsInvalidTokens()
    {
        var time = new ManualTimeProvider();
        var options = Options.Create(new SecurityOptions { TokenKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) });
        var protector = new AesGcmTokenProtector(options, time); var payload = new InstallTokenPayload("order", "nonce", time.GetUtcNow().AddMinutes(1));
        var token = protector.Protect(payload); Assert.True(protector.TryUnprotect(token, out var result)); Assert.Equal(payload, result);
        var bytes = TokenEncoding.Decode(token);
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= 1; Assert.False(protector.TryUnprotect(TokenEncoding.Encode(bytes), out _)); bytes[i] ^= 1;
        }
        var other = new AesGcmTokenProtector(Options.Create(new SecurityOptions { TokenKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }), time);
        Assert.False(other.TryUnprotect(token, out _)); time.Advance(TimeSpan.FromMinutes(1)); Assert.False(protector.TryUnprotect(token, out _));
    }
    [Fact]
    public async Task TryConsumeAsync_ConcurrentCalls_OnlyOneSucceeds()
    {
        var store = new InMemoryOneTimeTokenStore(new ManualTimeProvider());
        await store.StoreAsync("key", "hash", TimeSpan.FromMinutes(1), CancellationToken.None);
        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => store.TryConsumeAsync("key", "hash", CancellationToken.None))));
        Assert.Single(results, x => x);
    }
    [Fact]
    public async Task TryConsumeAsync_ExpiredEntry_ReturnsFalse()
    {
        var time = new ManualTimeProvider(); var store = new InMemoryOneTimeTokenStore(time);
        await store.StoreAsync("key", "hash", TimeSpan.FromSeconds(1), CancellationToken.None);
        await store.MarkUsedAsync("hash", TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.True(await store.WasUsedAsync("hash", CancellationToken.None));
        time.Advance(TimeSpan.FromSeconds(1)); Assert.False(await store.TryConsumeAsync("key", "hash", CancellationToken.None)); Assert.False(await store.WasUsedAsync("hash", CancellationToken.None));
    }
    [Fact]
    public async Task TryConsumeAsync_StaleHash_DoesNotBurnCurrentEntry()
    {
        var store = new InMemoryOneTimeTokenStore(new ManualTimeProvider());
        await store.StoreAsync("key", "old", TimeSpan.FromMinutes(1), CancellationToken.None);
        await store.StoreAsync("key", "new", TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.False(await store.TryConsumeAsync("key", "old", CancellationToken.None));
        Assert.True(await store.TryConsumeAsync("key", "new", CancellationToken.None));
    }
    [Fact]
    public async Task Score_SolidAndTextImages_SeparatesLoadingScreen()
    {
        using var dir = new TestDirectory(); var scorer = new SkiaFrameScorer(Options.Create(new FfmpegOptions()));
        await File.WriteAllBytesAsync(dir.File("loading.png"), TestMedia.Png(loading: true));
        await File.WriteAllBytesAsync(dir.File("text.png"), TestMedia.Png());
        Assert.True(scorer.Score(dir.File("loading.png")).IsLikelyLoadingScreen);
        Assert.False(scorer.Score(dir.File("text.png")).IsLikelyLoadingScreen);
    }
    [Fact]
    public void Verify_SignedTamperedAndUnknownKid_OnlyAcceptsValidJws()
    {
        using var dir = new TestDirectory(); using var signer = new Es256JwsSigner(Options.Create(new SigningOptions { PrivateKeyPemPath = dir.File("key.pem") }), NullLogger<Es256JwsSigner>.Instance);
        var jws = signer.Sign(new { orderId = "sample" }); var jwks = signer.GetPublicJwks();
        Assert.True(JwsVerifier.Verify(jws, jwks));
        var parts = jws.Split('.'); parts[1] = TokenEncoding.Encode(Encoding.UTF8.GetBytes("{\"orderId\":\"changed\"}"));
        Assert.False(JwsVerifier.Verify(string.Join('.', parts), jwks));
        Assert.False(JwsVerifier.Verify(jws, new([jwks.keys[0] with { kid = "unknown" }])));
        using var reloaded = new Es256JwsSigner(Options.Create(new SigningOptions { PrivateKeyPemPath = dir.File("key.pem") }), NullLogger<Es256JwsSigner>.Instance);
        Assert.Equal(jwks.keys[0], reloaded.GetPublicJwks().keys[0]);
    }
    [Fact]
    public async Task ExtractAsync_PngMetadata_RoundTripsAndRejectsCorruption()
    {
        using var dir = new TestDirectory(); var expected = TestMedia.Extraction(new ManualTimeProvider().GetUtcNow());
        var bytes = TestMedia.Png(expected); await File.WriteAllBytesAsync(dir.File("sample.png"), bytes);
        Assert.Equal(expected, await new EmbeddedMetadataTransferExtractor().ExtractAsync(new(dir.File("sample.png"), null, "KRW"), CancellationToken.None));
        Assert.Equal(0xcbf43926u, PngMetadata.Crc32("123456789"u8));
        bytes[^18] ^= 1; Assert.ThrowsAny<Exception>(() => PngMetadata.Read(new MemoryStream(bytes)));
    }
    [Fact]
    public async Task ExtractAsync_GeminiRateLimited_RetriesAndSendsImage()
    {
        using var dir = new TestDirectory(); var expected = TestMedia.Extraction(new ManualTimeProvider().GetUtcNow()); await File.WriteAllBytesAsync(dir.File("sample.png"), TestMedia.Png());
        var calls = 0;
        using var handler = new StubHandler(async (request, ct) => {
            Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            Assert.EndsWith("/v1beta/models/gemini-2.5-flash:generateContent", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var inline = body.RootElement.GetProperty("contents")[0].GetProperty("parts")[1].GetProperty("inline_data");
            Assert.Equal("image/png", inline.GetProperty("mime_type").GetString()); Assert.NotEmpty(Convert.FromBase64String(inline.GetProperty("data").GetString()!));
            if (++calls == 1) return StubHandler.Json("{}", HttpStatusCode.TooManyRequests);
            return StubHandler.Json(JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text = JsonSerializer.Serialize(expected, JsonSerializerOptions.Web) } } } } } }));
        });
        using var client = new HttpClient(handler); var factory = Substitute.For<IHttpClientFactory>(); factory.CreateClient("Gemini").Returns(client);
        var extractor = new GeminiTransferExtractor(factory, Options.Create(new GeminiOptions { ApiKey = "test-key", Endpoint = "https://example.test" }), TimeProvider.System);
        Assert.Equal(expected, await extractor.ExtractAsync(new(dir.File("sample.png"), "Transfer completed", "KRW"), CancellationToken.None)); Assert.Equal(2, calls);
    }
    [Fact]
    public async Task ExtractAsync_OcrLabels_ParsesFields()
    {
        const string text = "Transfer completed\nAmount: KRW 150,000\nTo: Jamie Lin\nBank: Demo Bank\nAccount: 110-***-567890\nFrom: Alex Chen\nDate: 2026-11-12 00:00:00 +00:00";
        var result = await new OcrHeuristicTransferExtractor().ExtractAsync(new("unused", text, "KRW"), CancellationToken.None);
        Assert.Equal(150000, result!.Amount); Assert.Equal("Jamie Lin", result.ReceiverName); Assert.True(result.IsCompletionScreen); Assert.Equal(1, result.Confidence);
    }
}
