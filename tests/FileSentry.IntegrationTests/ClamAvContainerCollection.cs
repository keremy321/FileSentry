using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace FileSentry.IntegrationTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ClamAvContainerCollection : ICollectionFixture<ClamAvContainerFixture>
{
    public const string Name = "Real ClamAV";
}

public sealed class ClamAvContainerFixture : IAsyncLifetime
{
    private const int ClamAvPort = 3310;

    private readonly IContainer _container = new ContainerBuilder("clamav/clamav:1.4_base")
        .WithEnvironment("CLAMD_STARTUP_TIMEOUT", "180")
        .WithPortBinding(ClamAvPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilInternalTcpPortIsAvailable(ClamAvPort))
        .Build();

    public string Host => _container.Hostname;

    public int Port => _container.GetMappedPublicPort(ClamAvPort);

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
