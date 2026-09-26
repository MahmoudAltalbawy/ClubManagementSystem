using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class PlayerProfileConfiguration : IEntityTypeConfiguration<PlayerProfile>
{
    public void Configure(EntityTypeBuilder<PlayerProfile> builder)
    {
        builder.ToTable("player_profile");

        builder.HasKey(pp => pp.PlayerId);
        builder.Property(pp => pp.PlayerId).HasColumnName("player_id");
        builder.Property(pp => pp.UserId).HasColumnName("user_id");
        builder.Property(pp => pp.Position).HasColumnName("position").HasMaxLength(50);
        builder.Property(pp => pp.JerseyNumber).HasColumnName("jersey_number");
        builder.Property(pp => pp.Height).HasColumnName("height");
        builder.Property(pp => pp.Weight).HasColumnName("weight");
        builder.Property(pp => pp.DeletedAt).HasColumnName("deleted_at");
        builder.Property(pp => pp.CreatedAt).HasColumnName("created_at");
        builder.Property(pp => pp.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(pp => pp.UserId).IsUnique();

        builder.HasOne(pp => pp.User)
            .WithOne()
            .HasForeignKey<PlayerProfile>(pp => pp.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasQueryFilter(pp => pp.DeletedAt == null);
    }
}
