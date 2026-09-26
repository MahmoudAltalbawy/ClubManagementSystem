using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(u => u.UserId);
        builder.Property(u => u.UserId).HasColumnName("user_id");
        builder.Property(u => u.Email).HasColumnName("email").HasColumnType("citext").IsRequired();
        builder.Property(u => u.Username).HasColumnName("username").HasColumnType("citext");
        builder.Property(u => u.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(u => u.PhoneNumber).HasColumnName("phone_number").HasMaxLength(30);
        builder.Property(u => u.Dob).HasColumnName("dob");
       builder.Property(u => u.Bio).HasColumnName("bio").HasMaxLength(500);
       builder.Property(u => u.YearsOfExperience).HasColumnName("years_of_experience");
       builder.Property(u => u.ProfileImageUrl).HasColumnName("profile_image_url").HasMaxLength(500);
        builder.Property(u => u.IsAdmin).HasColumnName("is_admin").HasDefaultValue(false);
        builder.Property(u => u.DeletedAt).HasColumnName("deleted_at");
        builder.Property(u => u.CreatedAt).HasColumnName("created_at");
        builder.Property(u => u.UpdatedAt).HasColumnName("updated_at");

        builder.HasMany(u => u.AuthProviders)
               .WithOne(a => a.User)
               .HasForeignKey(a => a.UserId);

        builder.HasMany(u => u.RefreshTokens)
               .WithOne(rt => rt.User)
               .HasForeignKey(rt => rt.UserId);

        builder.HasMany(u => u.ClubMemberships)
               .WithOne(cm => cm.User)
               .HasForeignKey(cm => cm.UserId);

        builder.HasMany(u => u.TeamMemberships)
               .WithOne(tm => tm.User)
               .HasForeignKey(tm => tm.UserId);

        // Partial unique indexes are enforced at DB level — EF doesn't manage them.
        builder.HasQueryFilter(u => u.DeletedAt == null);
    }
}
