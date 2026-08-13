using Microsoft.EntityFrameworkCore;

namespace FileSentry.Api.Persistence;

public sealed class FileSentryDbContext(DbContextOptions<FileSentryDbContext> options)
    : DbContext(options);
