using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TransferProof.Infrastructure;

namespace TransferProof.Api.Endpoints;

public sealed class MerchantKeyFilter(IOptions<SecurityOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var supplied = context.HttpContext.Request.Headers["X-Api-Key"].ToString();
        return !string.IsNullOrEmpty(supplied) && EndpointSupport.FixedEquals(supplied, options.Value.MerchantApiKey)
            ? await next(context) : EndpointSupport.Problem(401, "api_key_invalid", "A valid merchant API key is required.");
    }
}
public static class EndpointSupport
{
    public static IResult Problem(int status, string code, string title) => Results.Problem(statusCode: status, title: title,
        extensions: new Dictionary<string, object?> { ["code"] = code });
    public static bool FixedEquals(string a, string b) => CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
