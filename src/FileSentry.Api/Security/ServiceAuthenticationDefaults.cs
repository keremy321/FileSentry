namespace FileSentry.Api.Security;

public static class ServiceAuthenticationDefaults
{
    public const string Scheme = "ServiceApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string ActorTypeClaim = "filesentry_actor_type";
    public const string ActorType = "service";
}
