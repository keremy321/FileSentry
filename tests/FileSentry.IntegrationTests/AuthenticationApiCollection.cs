namespace FileSentry.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class AuthenticationApiCollection
    : ICollectionFixture<AuthenticationApiFactory>
{
    public const string Name = "Authentication API";
}
