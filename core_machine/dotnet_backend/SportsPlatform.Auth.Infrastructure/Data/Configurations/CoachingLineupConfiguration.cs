using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class CoachingLineupConfiguration : IEntityTypeConfiguration<CoachingLineup>
{
    public void Configure(EntityTypeBuilder<CoachingLineup> builder)
    {
        builder.ToTable("coaching_lineup");
        builder.HasKey(l => l.LineupId);
        builder.Property(l => l.LineupId).HasColumnName("lineup_id");
        builder.Property(l => l.TeamId).HasColumnName("team_id");
        builder.Property(l => l.EventId).HasColumnName("event_id");
        builder.Property(l => l.SeasonId).HasColumnName("season_id");
        builder.Property(l => l.CreatedBy).HasColumnName("created_by");
        builder.Property(l => l.Title).HasColumnName("title").HasMaxLength(200).IsRequired();
        builder.Property(l => l.Formation).HasColumnName("formation").HasMaxLength(50);
        builder.Property(l => l.GameModel).HasColumnName("game_model");
        builder.Property(l => l.TacticalNotes).HasColumnName("tactical_notes");
        builder.Property(l => l.Visibility).HasColumnName("visibility").HasColumnType("plan_visibility");
        builder.Property(l => l.DeletedAt).HasColumnName("deleted_at");
        builder.Property(l => l.CreatedAt).HasColumnName("created_at");
        builder.Property(l => l.UpdatedAt).HasColumnName("updated_at");

        builder.HasOne(l => l.Team)
            .WithMany()
            .HasForeignKey(l => l.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(l => l.Event)
            .WithMany()
            .HasForeignKey(l => l.EventId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(l => l.Season)
            .WithMany()
            .HasForeignKey(l => l.SeasonId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(l => l.Creator)
            .WithMany()
            .HasForeignKey(l => l.CreatedBy)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(l => new { l.TeamId, l.EventId });
        builder.HasQueryFilter(l => l.DeletedAt == null);
    }
}
