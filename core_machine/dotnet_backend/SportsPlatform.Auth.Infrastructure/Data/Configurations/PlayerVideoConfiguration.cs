using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class PlayerVideoConfiguration : IEntityTypeConfiguration<PlayerVideo>
{
    public void Configure(EntityTypeBuilder<PlayerVideo> builder)
    {
        builder.ToTable("player_video");

        builder.HasKey(v => v.VideoId);
        builder.Property(v => v.VideoId).HasColumnName("video_id");
        builder.Property(v => v.PlayerUserId).HasColumnName("player_user_id");
        builder.Property(v => v.TeamId).HasColumnName("team_id");
        builder.Property(v => v.AddedByUserId).HasColumnName("added_by_user_id");
        builder.Property(v => v.AddedByRole).HasColumnName("added_by_role").HasMaxLength(100);
        builder.Property(v => v.Title).HasColumnName("title").HasMaxLength(300);
        builder.Property(v => v.FileName).HasColumnName("file_name").HasMaxLength(300);
        builder.Property(v => v.OriginalFileName).HasColumnName("original_file_name").HasMaxLength(300);
        builder.Property(v => v.ContentType).HasColumnName("content_type").HasMaxLength(150);
        builder.Property(v => v.FileSize).HasColumnName("file_size");
        builder.Property(v => v.StoragePath).HasColumnName("storage_path");
        builder.Property(v => v.DeletedAt).HasColumnName("deleted_at");
        builder.Property(v => v.CreatedAt).HasColumnName("created_at");
        builder.Property(v => v.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(v => v.PlayerUserId);
        builder.HasIndex(v => v.TeamId);

        builder.HasOne(v => v.PlayerUser)
            .WithMany()
            .HasForeignKey(v => v.PlayerUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(v => v.AddedByUser)
            .WithMany()
            .HasForeignKey(v => v.AddedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasQueryFilter(v => v.DeletedAt == null);
    }
}
