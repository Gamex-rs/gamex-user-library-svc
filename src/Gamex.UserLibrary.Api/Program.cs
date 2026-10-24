using System.Text.Json;
using System.Text.Json.Serialization;
using Gamex.UserLibrary.Api.Auth;
using Gamex.UserLibrary.Api.Endpoints;
using Gamex.UserLibrary.Application;
using Gamex.UserLibrary.Infrastructure;
using Gamex.UserLibrary.Infrastructure.Persistence;
using Gamex.UserLibrary.Infrastructure.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(options =>
    // Enums as the spec's snake_case values ("like_new", "want_to_buy"), in requests and responses.
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)));
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);
builder.Services.AddGamexAuth(builder.Configuration);

var app = builder.Build();

var jwt = app.Services.GetRequiredService<JwtOptions>();
if (jwt.IsEphemeral)
{
#pragma warning disable CA1848 // One-time startup message.
    app.Logger.LogWarning(
        "JWT_PRIVATE_KEY is not configured; using an ephemeral development key (tokens are invalid after restart). Public key for other services:\n{PublicKey}",
        jwt.PublicKeyPem);
#pragma warning restore CA1848
}

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health");
app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapLibraryEndpoints();

if (app.Configuration.GetValue("DB_MIGRATE_ON_STARTUP", app.Environment.IsDevelopment()))
{
    await app.Services.MigrateDatabaseAsync();
}

await app.RunAsync();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
