using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace FileSentry.IntegrationTests;

[Collection(AuthenticationApiCollection.Name)]
public sealed class StartupValidationTests(AuthenticationApiFactory factory)
{
    [Fact]
    public void MissingPostgreSqlConnectionString_FailsStartupClearly()
    {
        using WebApplicationFactory<Program> invalidFactory = WithConfiguration(
            "ConnectionStrings:PostgreSql",
            " ");

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => _ = invalidFactory.Services);

        Assert.Contains(
            "ConnectionStrings:PostgreSql",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    public void MissingOrWeakJwtSigningKey_FailsStartupClearly(string signingKey)
    {
        using WebApplicationFactory<Program> invalidFactory = WithConfiguration(
            "Jwt:SigningKey",
            signingKey.Length == 0 ? " " : signingKey);

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => _ = invalidFactory.Services);

        Assert.Contains(
            "Jwt:SigningKey must contain at least 32 bytes.",
            exception.Message,
            StringComparison.Ordinal);
    }

    private WebApplicationFactory<Program> WithConfiguration(string key, string value) =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [key] = value
                })));
}
