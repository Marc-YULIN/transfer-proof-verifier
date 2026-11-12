using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TransferProof.Core.Abstractions;

namespace TransferProof.Infrastructure;

public static class TokenEncoding
{
    public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}

public sealed class AesGcmTokenProtector(IOptions<SecurityOptions> options, TimeProvider time) : ITokenProtector
{
    public string Protect(InstallTokenPayload payload)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = JsonSerializer.SerializeToUtf8Bytes(payload);
        var ciphertext = new byte[plain.Length]; var tag = new byte[16];
        using var aes = new AesGcm(Convert.FromBase64String(options.Value.TokenKey), 16);
        aes.Encrypt(nonce, plain, ciphertext, tag);
        return TokenEncoding.Encode([.. nonce, .. ciphertext, .. tag]);
    }

    public bool TryUnprotect(string token, out InstallTokenPayload? payload) =>
        TryReadAuthenticated(token, out payload) && payload!.ExpiresAt > time.GetUtcNow();

    /// <summary>Reads authenticated expiry so the API can distinguish an expired token from a malformed token.</summary>
    public bool TryReadAuthenticated(string token, out InstallTokenPayload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 16384) return false;
        try
        {
            var data = TokenEncoding.Decode(token);
            if (data.Length < 29) return false;
            var plain = new byte[data.Length - 28];
            using var aes = new AesGcm(Convert.FromBase64String(options.Value.TokenKey), 16);
            aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(12, plain.Length), data.AsSpan(data.Length - 16), plain);
            payload = JsonSerializer.Deserialize<InstallTokenPayload>(plain);
            return payload is not null && !string.IsNullOrWhiteSpace(payload.OrderId) && !string.IsNullOrWhiteSpace(payload.Nonce);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException or ArgumentException) { return false; }
    }
}

public sealed class InMemoryOneTimeTokenStore(TimeProvider time) : IOneTimeTokenStore
{
    private readonly ConcurrentDictionary<string, (string Value, DateTimeOffset ExpiresAt)> entries = new();
    public Task StoreAsync(string key, string valueHash, TimeSpan ttl, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        entries[key] = (valueHash, time.GetUtcNow() + ttl);
        foreach (var item in entries.Where(x => x.Value.ExpiresAt <= time.GetUtcNow()))
            ((ICollection<KeyValuePair<string, (string, DateTimeOffset)>>)entries).Remove(item);
        return Task.CompletedTask;
    }
    public Task<bool> TryConsumeAsync(string key, string expectedHash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!entries.TryGetValue(key, out var entry) || entry.ExpiresAt <= time.GetUtcNow()) return Task.FromResult(false);
        if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(entry.Value), System.Text.Encoding.UTF8.GetBytes(expectedHash)))
            return Task.FromResult(false);
        // Compare-and-remove: succeeds only if the slot still holds exactly the entry we compared.
        return Task.FromResult(entries.TryRemove(new KeyValuePair<string, (string Value, DateTimeOffset ExpiresAt)>(key, entry)));
    }
    public Task MarkUsedAsync(string tokenHash, TimeSpan ttl, CancellationToken cancellationToken) => StoreAsync("used:" + tokenHash, "1", ttl, cancellationToken);
    public Task<bool> WasUsedAsync(string tokenHash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(entries.TryGetValue("used:" + tokenHash, out var entry) && entry.ExpiresAt > time.GetUtcNow());
    }
}

public sealed class RedisOneTimeTokenStore(IConnectionMultiplexer redis) : IOneTimeTokenStore
{
    // GET + DEL in one Lua script: Redis runs scripts atomically, so a stale hash can never delete the current slot.
    private const string CompareAndDeleteScript =
        "if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) else return 0 end";

    public async Task StoreAsync(string key, string valueHash, TimeSpan ttl, CancellationToken cancellationToken) =>
        await redis.GetDatabase().StringSetAsync("tpv:" + key, valueHash, ttl).WaitAsync(cancellationToken);
    public async Task<bool> TryConsumeAsync(string key, string expectedHash, CancellationToken cancellationToken)
    {
        var deleted = await redis.GetDatabase()
            .ScriptEvaluateAsync(CompareAndDeleteScript, [(RedisKey)("tpv:" + key)], [(RedisValue)expectedHash])
            .WaitAsync(cancellationToken);
        return (long)deleted == 1;
    }
    public Task MarkUsedAsync(string tokenHash, TimeSpan ttl, CancellationToken cancellationToken) => StoreAsync("used:" + tokenHash, "1", ttl, cancellationToken);
    public async Task<bool> WasUsedAsync(string tokenHash, CancellationToken cancellationToken) =>
        await redis.GetDatabase().KeyExistsAsync("tpv:used:" + tokenHash).WaitAsync(cancellationToken);
}
