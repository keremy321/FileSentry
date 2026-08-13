using System.Text;
using System.Threading.RateLimiting;
using FileSentry.Api.Infrastructure.Health;
using FileSentry.Api.Infrastructure.Options;
using FileSentry.Api.Persistence;
using FileSentry.Api.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??=
            new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "Provide a JWT access token using: Bearer {token}"
        };

        return Task.CompletedTask;
    });
    options.AddOperationTransformer((operation, context, _) =>
    {
        bool requiresAuthorization = context.Description.ActionDescriptor.EndpointMetadata
            .OfType<IAuthorizeData>()
            .Any();

        if (requiresAuthorization)
        {
            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = []
            });
        }

        return Task.CompletedTask;
    });
});
builder.Services
    .AddOptions<PostgreSqlOptions>()
    .Configure<IConfiguration>((options, configuration) =>
        options.ConnectionString = configuration.GetConnectionString("PostgreSql") ?? string.Empty)
    .Validate(options => !string.IsNullOrWhiteSpace(options.ConnectionString),
        "The required connection string 'ConnectionStrings:PostgreSql' is missing.")
    .ValidateOnStart();
builder.Services.AddDbContext<FileSentryDbContext>((serviceProvider, options) =>
    options.UseNpgsql(
        serviceProvider.GetRequiredService<IOptions<PostgreSqlOptions>>().Value.ConnectionString));

builder.Services
    .AddIdentityCore<ApplicationUser>(options =>
    {
        options.User.RequireUniqueEmail = true;

        options.Password.RequiredLength = 12;
        options.Password.RequireUppercase = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireDigit = true;
        options.Password.RequireNonAlphanumeric = true;

        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
    })
    .AddSignInManager()
    .AddEntityFrameworkStores<FileSentryDbContext>();

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
    .AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .Validate(options => !string.IsNullOrWhiteSpace(options.Issuer),
        $"{JwtOptions.SectionName}:Issuer must not be empty.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.Audience),
        $"{JwtOptions.SectionName}:Audience must not be empty.")
    .Validate(options => Encoding.UTF8.GetByteCount(options.SigningKey ?? string.Empty) >= 32,
        $"{JwtOptions.SectionName}:SigningKey must contain at least 32 bytes.")
    .Validate(options => options.AccessTokenLifetimeMinutes is >= 1 and <= 60,
        $"{JwtOptions.SectionName}:AccessTokenLifetimeMinutes must be between 1 and 60.")
    .ValidateOnStart();

builder.Services
    .AddOptions<AuthenticationRateLimitOptions>()
    .Bind(builder.Configuration.GetSection(AuthenticationRateLimitOptions.SectionName))
    .Validate(options => options.PermitLimit is >= 1 and <= 1000,
        $"{AuthenticationRateLimitOptions.SectionName}:PermitLimit must be between 1 and 1000.")
    .Validate(options => options.WindowSeconds is >= 1 and <= 3600,
        $"{AuthenticationRateLimitOptions.SectionName}:WindowSeconds must be between 1 and 3600.")
    .ValidateOnStart();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
builder.Services
    .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((options, jwtOptionsAccessor) =>
    {
        JwtOptions jwtOptions = jwtOptionsAccessor.Value;
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter();
builder.Services
    .AddOptions<RateLimiterOptions>()
    .Configure<IOptions<AuthenticationRateLimitOptions>>((options, rateLimitOptionsAccessor) =>
    {
        AuthenticationRateLimitOptions authenticationRateLimitOptions =
            rateLimitOptionsAccessor.Value;
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy(AuthenticationRateLimitOptions.PolicyName, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = authenticationRateLimitOptions.PermitLimit,
                    Window = TimeSpan.FromSeconds(authenticationRateLimitOptions.WindowSeconds),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
        options.OnRejected = async (context, _) =>
        {
            await Results.Problem(
                    statusCode: StatusCodes.Status429TooManyRequests,
                    title: "Authentication rate limit exceeded",
                    detail: "Too many authentication attempts were received.",
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = "AUTH_RATE_LIMITED"
                    })
                .ExecuteAsync(context.HttpContext);
        };
    });

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<JwtTokenService>();

builder.Services
    .AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), ["live", "ready"])
    .AddDbContextCheck<FileSentryDbContext>("postgresql", tags: ["ready"])
    .AddCheck<ClamAvHealthCheck>("clamav", tags: ["ready"]);

var app = builder.Build();

_ = app.Services.GetRequiredService<IOptions<PostgreSqlOptions>>().Value;
_ = app.Services.GetRequiredService<IOptions<ClamAvOptions>>().Value;
_ = app.Services.GetRequiredService<IOptions<JwtOptions>>().Value;
_ = app.Services.GetRequiredService<IOptions<AuthenticationRateLimitOptions>>().Value;

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live")
}).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
}).AllowAnonymous();

app.Run();

public partial class Program;
