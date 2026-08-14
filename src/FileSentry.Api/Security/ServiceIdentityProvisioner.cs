using FileSentry.Api.Infrastructure.Options;
using FileSentry.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FileSentry.Api.Security;

public sealed class ServiceIdentityProvisioner(
    FileSentryDbContext dbContext,
    IOptions<ServiceAuthenticationOptions> options,
    TimeProvider timeProvider)
{
    private const string ReservedDomain = "service.filesentry.invalid";

    public async Task EnsureProvisionedAsync(CancellationToken cancellationToken)
    {
        ServiceAuthenticationOptions configuredOptions = options.Value;
        if (!configuredOptions.Enabled)
        {
            return;
        }

        string reservedUserName = GetReservedUserName(configuredOptions.ServiceId);
        ApplicationUser? existingUser = await dbContext.Users.SingleOrDefaultAsync(
            user => user.Id == configuredOptions.ServiceId,
            cancellationToken);
        if (existingUser is null)
        {
            dbContext.Users.Add(new ApplicationUser
            {
                Id = configuredOptions.ServiceId,
                UserName = reservedUserName,
                NormalizedUserName = reservedUserName.ToUpperInvariant(),
                Email = reservedUserName,
                NormalizedEmail = reservedUserName.ToUpperInvariant(),
                EmailConfirmed = true,
                CreatedAtUtc = timeProvider.GetUtcNow()
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        if (!string.Equals(existingUser.UserName, reservedUserName, StringComparison.Ordinal)
            || !string.Equals(existingUser.Email, reservedUserName, StringComparison.Ordinal)
            || existingUser.PasswordHash is not null)
        {
            throw new InvalidOperationException(
                "The configured service identity conflicts with an existing user.");
        }
    }

    public static string GetReservedUserName(Guid serviceId) =>
        $"service-{serviceId:N}@{ReservedDomain}";
}
