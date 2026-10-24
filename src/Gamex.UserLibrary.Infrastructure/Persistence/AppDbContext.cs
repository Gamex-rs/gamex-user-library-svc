using Gamex.UserLibrary.Application.Abstractions;
using Gamex.UserLibrary.Domain.Catalog;
using Gamex.UserLibrary.Domain.Library;
using Gamex.UserLibrary.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MySql.Data.MySqlClient;

namespace Gamex.UserLibrary.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    private const int MySqlDuplicateEntry = 1062;

    public DbSet<User> Users => Set<User>();
    public DbSet<UserPlatform> UserPlatforms => Set<UserPlatform>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<OAuthLogin> OAuthLogins => Set<OAuthLogin>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<LibraryEntry> LibraryEntries => Set<LibraryEntry>();
    public DbSet<ListingCompletion> ListingCompletions => Set<ListingCompletion>();
    public DbSet<CompletedTransaction> Transactions => Set<CompletedTransaction>();
    public DbSet<UserRating> Ratings => Set<UserRating>();
    public DbSet<CatalogGame> CatalogGames => Set<CatalogGame>();
    public DbSet<CatalogPlatform> CatalogPlatforms => Set<CatalogPlatform>();

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        Database.BeginTransactionAsync(cancellationToken);

    public bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is MySqlException { Number: MySqlDuplicateEntry };

    public void DiscardChanges() => ChangeTracker.Clear();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        modelBuilder.ApplySnakeCaseNames();
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<Guid>().HaveColumnType("char(36)");
        configurationBuilder.Properties<DateTime>().HaveColumnType("datetime(6)").HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateOnly>().HaveColumnType("date").HaveConversion<DateOnlyConverter>();
        configurationBuilder.Properties<GameFormat>().HaveMaxLength(20).HaveConversion<SnakeCaseEnumConverter<GameFormat>>();
        configurationBuilder.Properties<GameCondition>().HaveMaxLength(20).HaveConversion<SnakeCaseEnumConverter<GameCondition>>();
        configurationBuilder.Properties<ListingStatus>().HaveMaxLength(20).HaveConversion<SnakeCaseEnumConverter<ListingStatus>>();
    }
}
