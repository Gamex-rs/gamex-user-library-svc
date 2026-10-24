using Gamex.UserLibrary.Domain.Catalog;
using Gamex.UserLibrary.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Gamex.UserLibrary.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();
        builder.Property(u => u.DisplayName).HasMaxLength(100).IsRequired();
        builder.Property(u => u.Email).HasMaxLength(255).IsRequired();
        builder.Property(u => u.PasswordHash).HasMaxLength(500);
        builder.Property(u => u.AvatarUrl).HasMaxLength(500);
        builder.Property(u => u.City).HasMaxLength(100);
        builder.Property(u => u.Region).HasMaxLength(100);
        builder.Property(u => u.Country).HasColumnType("char(2)").IsRequired();
        builder.Property(u => u.Bio).HasColumnType("text");
        builder.Property(u => u.Role).HasMaxLength(20).IsRequired();
        builder.Property(u => u.RatingAvg).HasPrecision(3, 2);
        builder.HasIndex(u => u.Email).IsUnique();
        builder.HasIndex(u => u.City);

        builder.HasMany(u => u.Platforms).WithOne().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(u => u.Platforms).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class UserPlatformConfiguration : IEntityTypeConfiguration<UserPlatform>
{
    public void Configure(EntityTypeBuilder<UserPlatform> builder)
    {
        builder.ToTable("user_platforms");
        builder.HasKey(p => new { p.UserId, p.PlatformId });
        builder.HasOne<CatalogPlatform>().WithMany().HasForeignKey(p => p.PlatformId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => new { t.UserId, t.RevokedAt });
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OAuthLoginConfiguration : IEntityTypeConfiguration<OAuthLogin>
{
    public void Configure(EntityTypeBuilder<OAuthLogin> builder)
    {
        builder.ToTable("oauth_providers");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Provider).HasMaxLength(20).IsRequired();
        builder.Property(l => l.ProviderUid).HasMaxLength(255).IsRequired();
        builder.HasIndex(l => new { l.Provider, l.ProviderUid }).IsUnique();
        builder.HasOne<User>().WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PasswordResetTokenConfiguration : IEntityTypeConfiguration<PasswordResetToken>
{
    public void Configure(EntityTypeBuilder<PasswordResetToken> builder)
    {
        builder.ToTable("password_reset_tokens");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
