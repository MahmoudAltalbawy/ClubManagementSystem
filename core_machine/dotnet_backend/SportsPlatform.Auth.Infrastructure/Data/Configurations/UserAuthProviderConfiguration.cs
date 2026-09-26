using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class UserAuthProviderConfiguration : IEntityTypeConfiguration<UserAuthProvider>
{
    public void Configure(EntityTypeBuilder<UserAuthProvider> builder)
    {
        builder.ToTable("user_auth_provider");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id");
        builder.Property(a => a.UserId).HasColumnName("user_id");
        builder.Property(a => a.Provider).HasColumnName("provider").HasColumnType("auth_provider_type");
        builder.Property(a => a.ProviderUserId).HasColumnName("provider_user_id").HasMaxLength(255);
        builder.Property(a => a.ProviderIdentifier).HasColumnName("provider_identifier").HasMaxLength(255);
        builder.Property(a => a.PasswordHash).HasColumnName("password_hash");
        builder.Property(a => a.IsVerified).HasColumnName("is_verified");
        builder.Property(a => a.CreatedAt).HasColumnName("created_at");
        builder.Property(a => a.UpdatedAt).HasColumnName("updated_at");

        builder.HasQueryFilter(a => a.User.DeletedAt == null);
    }
}
