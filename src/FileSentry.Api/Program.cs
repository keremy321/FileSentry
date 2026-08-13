using FileSentry.Api.Infrastructure.Health;
using FileSentry.Api.Infrastructure.Options;
using FileSentry.Api.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

var postgreSqlConnectionString = builder.Configuration.GetConnectionString("PostgreSql");
if (string.IsNullOrWhiteSpace(postgreSqlConnectionString))
{
    throw new InvalidOperationException(
        "The required connection string 'ConnectionStrings:PostgreSql' is missing.");
}

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddDbContext<FileSentryDbContext>(options =>
    options.UseNpgsql(postgreSqlConnectionString));

builder.Services
    .AddOptions<ClamAvOptions>()
    .Bind(builder.Configuration.GetSection(ClamAvOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.Host),
        $"{ClamAvOptions.SectionName}:Host must not be empty.")
    .Validate(options => options.Port is >= 1 and <= 65535,
        $"{ClamAvOptions.SectionName}:Port must be between 1 and 65535.")
    .Validate(options => options.TimeoutSeconds is >= 1 and <= 30,
        $"{ClamAvOptions.SectionName}:TimeoutSeconds must be between 1 and 30.")
    .ValidateOnStart();

builder.Services
    .AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), ["live", "ready"])
    .AddDbContextCheck<FileSentryDbContext>("postgresql", tags: ["ready"])
    .AddCheck<ClamAvHealthCheck>("clamav", tags: ["ready"]);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live")
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});

app.Run();

public partial class Program;
