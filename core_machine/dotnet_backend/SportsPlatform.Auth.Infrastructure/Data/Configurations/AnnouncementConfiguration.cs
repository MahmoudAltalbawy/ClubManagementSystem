using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class AnnouncementConfiguration : IEntityTypeConfiguration<Announcement>
{
    public void Configure(EntityTypeBuilder<Announcement> builder)
    {
        builder.ToTable("announcement");
        builder.HasKey(a => a.AnnouncementId);
        builder.Property(a => a.AnnouncementId).HasColumnName("announcement_id");
        builder.Property(a => a.TeamId).HasColumnName("team_id");
        builder.Property(a => a.CreatedBy).HasColumnName("created_by");
        builder.Property(a => a.Title).HasColumnName("title").HasMaxLength(200).IsRequired();
        builder.Property(a => a.Content).HasColumnName("content").IsRequired();
        builder.Property(a => a.ImageUrl).HasColumnName("image_url").HasMaxLength(500);
        builder.Property(a => a.Priority).HasColumnName("priority").HasColumnType("announcement_priority");
        builder.Property(a => a.DeletedAt).HasColumnName("deleted_at");
        builder.Property(a => a.CreatedAt).HasColumnName("created_at");
        builder.Property(a => a.UpdatedAt).HasColumnName("updated_at");

        builder.HasOne(a => a.Team)
            .WithMany()
            .HasForeignKey(a => a.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(a => a.Creator)
            .WithMany()
            .HasForeignKey(a => a.CreatedBy)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasQueryFilter(a => a.DeletedAt == null);
    }
}
