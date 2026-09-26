using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> builder)
    {
        builder.ToTable("team");

        builder.HasKey(t => t.TeamId);
        builder.Property(t => t.TeamId).HasColumnName("team_id");
        builder.Property(t => t.ClubId).HasColumnName("club_id");
        builder.Property(t => t.TeamName).HasColumnName("team_name").HasMaxLength(100);
        builder.Property(t => t.ImageUrl).HasColumnName("image_url").HasMaxLength(500);
        builder.Property(t => t.CategoryId).HasColumnName("category_id");
        builder.Property(t => t.CreatedBy).HasColumnName("created_by");
        builder.Property(t => t.DeletedAt).HasColumnName("deleted_at");
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");
        builder.Property(t => t.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(t => t.ClubId);

        builder.HasOne(t => t.Club)
               .WithMany(c => c.Teams)
               .HasForeignKey(t => t.ClubId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(t => t.Creator)
               .WithMany()
               .HasForeignKey(t => t.CreatedBy)
               .OnDelete(DeleteBehavior.Restrict);

        // Soft-delete query filter
        builder.HasQueryFilter(t => t.DeletedAt == null);
    }
}
