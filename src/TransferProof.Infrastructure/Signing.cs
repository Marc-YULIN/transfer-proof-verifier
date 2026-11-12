using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TransferProof.Core.Abstractions;

namespace TransferProof.Infrastructure;

public sealed class Es256JwsSigner : IJwsSigner, IDisposable
{
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly JsonWebKey publicKey;
    private readonly object gate = new();
    public Es256JwsSigner(IOptions<SigningOptions> options, ILogger<Es256JwsSigner> logger)
    {
        var path = options.Value.PrivateKeyPemPath;
        if (string.IsNullOrWhiteSpace(path)) path = "appdata/signing-key.pem";
        if (File.Exists(path)) key.ImportFromPem(File.ReadAllText(path));
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(file)) writer.Write(key.ExportECPrivateKeyPem());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            InfrastructureLog.SigningKeyCreated(logger);
        }
        var parameters = key.ExportParameters(false);
        if (key.KeySize != 256 || parameters.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value) throw new InvalidOperationException("Signing key must use P-256.");
        var x = TokenEncoding.Encode(parameters.Q.X!); var y = TokenEncoding.Encode(parameters.Q.Y!);
        var canonical = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{x}\",\"y\":\"{y}\"}}";
        var kid = TokenEncoding.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        publicKey = new("EC", "P-256", x, y, kid, "ES256", "sig");
    }
    public JsonWebKeySet GetPublicJwks() => new([publicKey]);
    public string Sign(object payload)
    {
        var header = TokenEncoding.Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = publicKey.kid, typ = "JWT" }));
        var content = header + "." + TokenEncoding.Encode(JsonSerializer.SerializeToUtf8Bytes(payload, JsonSerializerOptions.Web));
        lock (gate) return content + "." + TokenEncoding.Encode(key.SignData(Encoding.ASCII.GetBytes(content), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }
    public void Dispose() => key.Dispose();
}

public static class JwsVerifier
{
    public static bool Verify(string jws, JsonWebKeySet jwks)
    {
        try
        {
            var parts = jws.Split('.'); if (parts.Length != 3) return false;
            using var header = JsonDocument.Parse(TokenEncoding.Decode(parts[0]));
            if (header.RootElement.GetProperty("alg").GetString() != "ES256") return false;
            var kid = header.RootElement.GetProperty("kid").GetString();
            var jwk = jwks.keys.SingleOrDefault(k => k.kid == kid && k.kty == "EC" && k.crv == "P-256" && k.alg == "ES256" && k.use == "sig");
            if (jwk is null) return false;
            using var key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = TokenEncoding.Decode(jwk.x), Y = TokenEncoding.Decode(jwk.y) } });
            return key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), TokenEncoding.Decode(parts[2]), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException) { return false; }
    }
}
