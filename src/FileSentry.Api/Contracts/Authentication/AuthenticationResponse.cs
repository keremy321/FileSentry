namespace FileSentry.Api.Contracts.Authentication;

public sealed record AuthenticationResponse(
    string AccessToken,
    string TokenType,
    DateTimeOffset ExpiresAtUtc);
