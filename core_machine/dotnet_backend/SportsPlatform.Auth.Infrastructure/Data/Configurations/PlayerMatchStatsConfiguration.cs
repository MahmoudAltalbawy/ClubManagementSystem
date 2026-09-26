using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class PlayerMatchStatsConfiguration : IEntityTypeConfiguration<PlayerMatchStats>
{
    public void Configure(EntityTypeBuilder<PlayerMatchStats> builder)
    {
        builder.ToTable("player_match_stats");
        builder.HasKey(s => s.PlayerMatchStatsId);
        builder.Property(s => s.PlayerMatchStatsId).HasColumnName("player_match_stats_id");
        builder.Property(s => s.MatchStatsId).HasColumnName("match_stats_id");
        builder.Property(s => s.TeamId).HasColumnName("team_id");
        builder.Property(s => s.EventId).HasColumnName("event_id");
        builder.Property(s => s.SeasonId).HasColumnName("season_id");
        builder.Property(s => s.PlayerUserId).HasColumnName("player_user_id");
        builder.Property(s => s.MinutesPlayed).HasColumnName("minutes_played");
        builder.Property(s => s.Goals).HasColumnName("goals");
        builder.Property(s => s.Assists).HasColumnName("assists");
        builder.Property(s => s.ShotsOnTarget).HasColumnName("shots_on_target");
        builder.Property(s => s.TotalShots).HasColumnName("total_shots");
        builder.Property(s => s.PassesCompleted).HasColumnName("passes_completed");
        builder.Property(s => s.PassesAttempted).HasColumnName("passes_attempted");
        builder.Property(s => s.PassAccuracy).HasColumnName("pass_accuracy").HasPrecision(5, 2);
        builder.Property(s => s.Tackles).HasColumnName("tackles");
        builder.Property(s => s.Interceptions).HasColumnName("interceptions");
        builder.Property(s => s.YellowCards).HasColumnName("yellow_cards");
        builder.Property(s => s.RedCards).HasColumnName("red_cards");
        builder.Property(s => s.Rating).HasColumnName("rating").HasPrecision(4, 2);
        builder.Property(s => s.Notes).HasColumnName("notes");
        builder.Property(s => s.Granularity).HasColumnName("granularity").HasMaxLength(50);
        builder.Property(s => s.RowType).HasColumnName("row_type").HasMaxLength(50);
        builder.Property(s => s.Status).HasColumnName("status").HasMaxLength(50);
        builder.Property(s => s.PlayerNo).HasColumnName("player_no");
        builder.Property(s => s.IsStarter).HasColumnName("is_starter");
        builder.Property(s => s.IsCaptain).HasColumnName("is_captain");
        builder.Property(s => s.GamesListed).HasColumnName("games_listed");
        builder.Property(s => s.GamesPlayed).HasColumnName("games_played");
        builder.Property(s => s.Starts).HasColumnName("starts");
        builder.Property(s => s.BbMinutes).HasColumnName("bb_minutes").HasMaxLength(20);
        builder.Property(s => s.TwoPtMA).HasColumnName("two_pt_ma").HasMaxLength(20);
        builder.Property(s => s.ThreePtMA).HasColumnName("three_pt_ma").HasMaxLength(20);
        builder.Property(s => s.FtMA).HasColumnName("ft_ma").HasMaxLength(20);
        builder.Property(s => s.OffensiveRebounds).HasColumnName("offensive_rebounds");
        builder.Property(s => s.DefensiveRebounds).HasColumnName("defensive_rebounds");
        builder.Property(s => s.TotalRebounds).HasColumnName("total_rebounds");
        builder.Property(s => s.BbAssists).HasColumnName("bb_assists");
        builder.Property(s => s.BbTurnovers).HasColumnName("bb_turnovers");
        builder.Property(s => s.BbSteals).HasColumnName("bb_steals");
        builder.Property(s => s.BbBlocks).HasColumnName("bb_blocks");
        builder.Property(s => s.BbPersonalFouls).HasColumnName("bb_personal_fouls");
        builder.Property(s => s.BbFoulsDrawn).HasColumnName("bb_fouls_drawn");
        builder.Property(s => s.BbEfficiency).HasColumnName("bb_efficiency");
        builder.Property(s => s.BbPoints).HasColumnName("bb_points");
        builder.Property(s => s.BbTeamOffReb).HasColumnName("bb_team_off_reb");
        builder.Property(s => s.BbTeamDefReb).HasColumnName("bb_team_def_reb");
        builder.Property(s => s.BbTeamReb).HasColumnName("bb_team_reb");
        builder.Property(s => s.BbTeamPF).HasColumnName("bb_team_pf");
        builder.Property(s => s.BbTeamFD).HasColumnName("bb_team_fd");

        builder.HasOne(s => s.MatchStats)
            .WithMany(m => m.PlayerStats)
            .HasForeignKey(s => s.MatchStatsId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(s => s.Team)
            .WithMany()
            .HasForeignKey(s => s.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(s => s.Event)
            .WithMany()
            .HasForeignKey(s => s.EventId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(s => s.Season)
            .WithMany()
            .HasForeignKey(s => s.SeasonId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(s => s.Player)
            .WithMany()
            .HasForeignKey(s => s.PlayerUserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => new { s.MatchStatsId, s.PlayerUserId }).IsUnique();
        builder.HasIndex(s => new { s.TeamId, s.PlayerUserId });
        builder.HasIndex(s => new { s.TeamId, s.EventId });

        builder.HasQueryFilter(s =>
            s.MatchStats.Team.DeletedAt == null &&
            s.MatchStats.Event.DeletedAt == null &&
            s.MatchStats.Recorder.DeletedAt == null &&
            s.Team.DeletedAt == null &&
            s.Event.DeletedAt == null &&
            s.Player.DeletedAt == null);
    }
}
