using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;
using TransferProof.Api.Endpoints;
using TransferProof.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders().AddConsole();
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context => {
    if (!context.ProblemDetails.Extensions.ContainsKey("code")) context.ProblemDetails.Extensions["code"] = "request_failed";
});
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.AddTransferProofInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddTransient<MerchantKeyFilter>();
builder.Services.AddOptions<FormOptions>().Configure<IOptions<StorageOptions>>((form, storage) => form.MultipartBodyLengthLimit = storage.Value.MaxUploadBytes);
builder.WebHost.ConfigureKestrel((context, options) => options.Limits.MaxRequestBodySize = checked((context.Configuration.GetValue<long?>("Storage:MaxUploadBytes") ?? 50 * 1024 * 1024) + 65536));
var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapOpenApi();
app.MapScalarApiReference();
app.MapHealthChecks("/health");
app.MapOrders(); app.MapCaptureLinks(); app.MapCaptureSessions(); app.MapVerifications(); app.MapDisputes(); app.MapSigningKeys();
Directory.CreateDirectory("appdata");
Directory.CreateDirectory(app.Services.GetRequiredService<IOptions<StorageOptions>>().Value.Root);
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TransferProofDbContext>();
    var connection = db.Database.GetDbConnection();
    var parent = Path.GetDirectoryName(connection.DataSource);
    if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
    await db.Database.EnsureCreatedAsync();
}
await app.RunAsync();
public partial class Program;
