using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class PlayerGameStatsConfiguration : IEntityTypeConfiguration<PlayerGameStats>
{
    public void Configure(EntityTypeBuilder<PlayerGameStats> builder)
    {
        builder.ToTable("player_game_stats");
        builder.HasKey(s => s.StatId);
        builder.Property(s => s.StatId).HasColumnName("stat_id");
        builder.Property(s => s.TeamId).HasColumnName("team_id");
        builder.Property(s => s.PlayerUserId).HasColumnName("player_user_id");
        builder.Property(s => s.EventId).HasColumnName("event_id");
        builder.Property(s => s.RecordedBy).HasColumnName("recorded_by");
        builder.Property(s => s.MatchDate).HasColumnName("match_date");
        builder.Property(s => s.OpponentName).HasColumnName("opponent_name").HasMaxLength(200);
        builder.Property(s => s.MinutesPlayed).HasColumnName("minutes_played");
        builder.Property(s => s.Goals).HasColumnName("goals");
        builder.Property(s => s.Assists).HasColumnName("assists");
        builder.Property(s => s.YellowCards).HasColumnName("yellow_cards");
        builder.Property(s => s.RedCards).HasColumnName("red_cards");
        builder.Property(s => s.Rating).HasColumnName("rating").HasPrecision(4, 2);
        builder.Property(s => s.Notes).HasColumnName("notes");
        builder.Property(s => s.CreatedAt).HasColumnName("created_at");
        builder.Property(s => s.UpdatedAt).HasColumnName("updated_at");

        builder.HasOne(s => s.Team)
            .WithMany()
            .HasForeignKey(s => s.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(s => s.Player)
            .WithMany()
            .HasForeignKey(s => s.PlayerUserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(s => s.Event)
            .WithMany()
            .HasForeignKey(s => s.EventId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(s => s.Recorder)
            .WithMany()
            .HasForeignKey(s => s.RecordedBy)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => new { s.TeamId, s.MatchDate });
        builder.HasIndex(s => new { s.PlayerUserId, s.MatchDate });

        builder.HasQueryFilter(s =>
            s.Team.DeletedAt == null &&
            s.Player.DeletedAt == null &&
            (s.EventId == null || s.Event!.DeletedAt == null) &&
            s.Recorder.DeletedAt == null);
    }
}
