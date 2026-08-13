using Microsoft.AspNetCore.Identity;

namespace FileSentry.Api.Security;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public DateTimeOffset CreatedAtUtc { get; set; }
}
