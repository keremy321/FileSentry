namespace FileSentry.Api.Contracts.Authentication;

public sealed record RegisterRequest(string? Email, string? Password);
