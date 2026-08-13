using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using FileSentry.Api.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace FileSentry.IntegrationTests;

public sealed class AuthenticationApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private static readonly byte[] ExpectedClamAvPing = Encoding.ASCII.GetBytes("zPING\0");
    private static readonly byte[] ClamAvPong = Encoding.ASCII.GetBytes("PONG\0");

    private readonly PostgreSqlContainer _postgreSqlContainer = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("filesentry_tests")
        .WithUsername("filesentry_tests")
        .WithPassword($"test-{Guid.NewGuid():N}")
        .Build();
    private readonly TcpListener _clamAvListener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _clamAvCancellationSource = new();
    private readonly string _storageRootPath = Path.Combine(
        Path.GetTempPath(),
        $"filesentry-integration-{Guid.NewGuid():N}");
    private Task? _clamAvServerTask;

    public AuthenticationApiFactory()
    {
        SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    }

    public string Issuer { get; } = "FileSentry.IntegrationTests";

    public string Audience { get; } = "FileSentry.IntegrationTests.Client";

    public string SigningKey { get; }

    public string StorageRootPath => _storageRootPath;

    public string TempRootPath => Path.Combine(_storageRootPath, "temp");

    public string QuarantineRootPath => Path.Combine(_storageRootPath, "quarantine");

    public string CleanRootPath => Path.Combine(_storageRootPath, "clean");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:PostgreSql"] = _postgreSqlContainer.GetConnectionString(),
                ["ClamAV:Host"] = IPAddress.Loopback.ToString(),
                ["ClamAV:Port"] = GetClamAvPort().ToString(),
                ["Jwt:Issuer"] = Issuer,
                ["Jwt:Audience"] = Audience,
                ["Jwt:SigningKey"] = SigningKey,
                ["Jwt:AccessTokenLifetimeMinutes"] = "15",
                ["AuthenticationRateLimit:PermitLimit"] = "1000",
                ["AuthenticationRateLimit:WindowSeconds"] = "60",
                ["UploadRateLimit:PermitLimit"] = "1000",
                ["UploadRateLimit:WindowSeconds"] = "60",
                ["ScannerWorker:Enabled"] = "false",
                ["Storage:RootPath"] = _storageRootPath
            });
        });
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        await _postgreSqlContainer.StartAsync();
        _clamAvListener.Start();
        _clamAvServerTask = RunClamAvServerAsync(_clamAvCancellationSource.Token);

        using IServiceScope scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<FileSentryDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _clamAvCancellationSource.CancelAsync();
        _clamAvListener.Stop();

        if (_clamAvServerTask is not null)
        {
            try
            {
                await _clamAvServerTask;
            }
            catch (OperationCanceledException) when (_clamAvCancellationSource.IsCancellationRequested)
            {
            }
        }

        _clamAvCancellationSource.Dispose();
        await _postgreSqlContainer.DisposeAsync();

        if (Directory.Exists(_storageRootPath))
        {
            Directory.Delete(_storageRootPath, recursive: true);
        }
    }

    private int GetClamAvPort()
    {
        if (_clamAvListener.LocalEndpoint is not IPEndPoint endpoint)
        {
            _clamAvListener.Start();
            endpoint = (IPEndPoint)_clamAvListener.LocalEndpoint;
        }

        return endpoint.Port;
    }

    private async Task RunClamAvServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using TcpClient client = await _clamAvListener.AcceptTcpClientAsync(cancellationToken);
            await using NetworkStream stream = client.GetStream();
            byte[] command = new byte[ExpectedClamAvPing.Length];
            await stream.ReadExactlyAsync(command, cancellationToken);

            if (command.AsSpan().SequenceEqual(ExpectedClamAvPing))
            {
                await stream.WriteAsync(ClamAvPong, cancellationToken);
            }
        }
    }
}
