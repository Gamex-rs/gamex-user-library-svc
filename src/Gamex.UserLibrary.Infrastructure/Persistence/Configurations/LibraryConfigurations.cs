using Gamex.UserLibrary.Domain.Catalog;
using Gamex.UserLibrary.Domain.Library;
using Gamex.UserLibrary.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Gamex.UserLibrary.Infrastructure.Persistence.Configurations;

internal sealed class LibraryEntryConfiguration : IEntityTypeConfiguration<LibraryEntry>
{
    public void Configure(EntityTypeBuilder<LibraryEntry> builder)
    {
        builder.ToTable("library_entries");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Price).HasPrecision(10, 2);
        builder.Property(e => e.PreferredGames).HasColumnType("text");
        builder.Property(e => e.Notes).HasColumnType("text");
        builder.Ignore(e => e.Flags);
        builder.Ignore(e => e.IsListed);
        builder.Ignore(e => e.IsOffered);
        builder.Ignore(e => e.ListingTypes);

        builder.HasIndex(e => new { e.UserId, e.GameId, e.PlatformId, e.Format }).IsUnique();
        builder.HasIndex(e => new { e.UserId, e.GameId });
        builder.HasIndex(e => new { e.PlatformId, e.ListingStatus });
        builder.HasIndex(e => new { e.ListingStatus, e.ListedAt });
        builder.HasIndex(e => new { e.ListingStatus, e.ListingExpiresAt });
        builder.HasIndex(e => new { e.GameId, e.PlatformId, e.IsWishlist });

        builder.HasOne<User>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CatalogGame>().WithMany().HasForeignKey(e => e.GameId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CatalogPlatform>().WithMany().HasForeignKey(e => e.PlatformId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ListingCompletionConfiguration : IEntityTypeConfiguration<ListingCompletion>
{
    public void Configure(EntityTypeBuilder<ListingCompletion> builder)
    {
        builder.ToTable("listing_completions");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.HasIndex(c => new { c.LibraryEntryId, c.ListedAt, c.UserId }).IsUnique();
        builder.HasOne<LibraryEntry>().WithMany().HasForeignKey(c => c.LibraryEntryId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CompletedTransactionConfiguration : IEntityTypeConfiguration<CompletedTransaction>
{
    public void Configure(EntityTypeBuilder<CompletedTransaction> builder)
    {
        // No FK to library_entries: a transaction (and its ratings) outlives deleting the entry.
        builder.ToTable("transactions");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.HasIndex(t => t.LibraryEntryId);
        builder.HasIndex(t => t.OwnerId);
        builder.HasIndex(t => t.PartnerId);
    }
}

internal sealed class UserRatingConfiguration : IEntityTypeConfiguration<UserRating>
{
    public void Configure(EntityTypeBuilder<UserRating> builder)
    {
        builder.ToTable("user_ratings");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Score).HasColumnType("tinyint");
        builder.Property(r => r.Comment).HasColumnType("text");
        builder.HasIndex(r => new { r.RaterId, r.TransactionId }).IsUnique();
        builder.HasIndex(r => new { r.RateeId, r.CreatedAt });
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.RaterId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.RateeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CompletedTransaction>().WithMany().HasForeignKey(r => r.TransactionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CatalogGameConfiguration : IEntityTypeConfiguration<CatalogGame>
{
    public void Configure(EntityTypeBuilder<CatalogGame> builder)
    {
        builder.ToTable("catalog_games");
        builder.HasKey(g => g.Id);
        builder.Property(g => g.Id).ValueGeneratedNever();
        builder.Property(g => g.Title).HasMaxLength(255).IsRequired();
        builder.Property(g => g.CoverImageUrl).HasMaxLength(500);
        builder.HasIndex(g => g.Title);
        builder.HasMany(g => g.Platforms).WithOne().HasForeignKey(p => p.GameId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(g => g.Platforms).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class CatalogGamePlatformConfiguration : IEntityTypeConfiguration<CatalogGamePlatform>
{
    public void Configure(EntityTypeBuilder<CatalogGamePlatform> builder)
    {
        builder.ToTable("catalog_game_platforms");
        builder.HasKey(p => new { p.GameId, p.PlatformId });
        builder.HasOne<CatalogPlatform>().WithMany().HasForeignKey(p => p.PlatformId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CatalogPlatformConfiguration : IEntityTypeConfiguration<CatalogPlatform>
{
    public void Configure(EntityTypeBuilder<CatalogPlatform> builder)
    {
        builder.ToTable("catalog_platforms");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Name).HasMaxLength(100).IsRequired();
        builder.Property(p => p.Slug).HasMaxLength(100).IsRequired();
        builder.HasIndex(p => p.Slug).IsUnique();
    }
}
