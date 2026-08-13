namespace FileSentry.Api.Contracts.Authentication;

public sealed record CurrentUserResponse(Guid UserId, string Email);
