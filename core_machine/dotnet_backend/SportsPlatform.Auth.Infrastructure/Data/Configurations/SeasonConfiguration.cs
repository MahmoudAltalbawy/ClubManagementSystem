using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class SeasonConfiguration : IEntityTypeConfiguration<Season>
{
    public void Configure(EntityTypeBuilder<Season> builder)
    {
        builder.ToTable("season");
        builder.HasKey(s => s.SeasonId);
        builder.Property(s => s.SeasonId).HasColumnName("season_id");
        builder.Property(s => s.TeamId).HasColumnName("team_id");
        builder.Property(s => s.CreatedBy).HasColumnName("created_by");
        builder.Property(s => s.Label).HasColumnName("label").HasMaxLength(50).IsRequired();
        builder.Property(s => s.StartDate).HasColumnName("start_date");
        builder.Property(s => s.EndDate).HasColumnName("end_date");
        builder.Property(s => s.IsCurrent).HasColumnName("is_current").HasDefaultValue(false);
        builder.Property(s => s.CreatedAt).HasColumnName("created_at");
        builder.Property(s => s.UpdatedAt).HasColumnName("updated_at");

        builder.HasOne(s => s.Team)
            .WithMany(t => t.Seasons)
            .HasForeignKey(s => s.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(s => s.Creator)
            .WithMany()
            .HasForeignKey(s => s.CreatedBy)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(s => new { s.TeamId, s.Label }).IsUnique();
    }
}
