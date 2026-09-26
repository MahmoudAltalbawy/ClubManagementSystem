using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class MatchStatsConfiguration : IEntityTypeConfiguration<MatchStats>
{
    public void Configure(EntityTypeBuilder<MatchStats> builder)
    {
        builder.ToTable("match_stats");
        builder.HasKey(s => s.MatchStatsId);
        builder.Property(s => s.MatchStatsId).HasColumnName("match_stats_id");
        builder.Property(s => s.TeamId).HasColumnName("team_id");
        builder.Property(s => s.EventId).HasColumnName("event_id");
        builder.Property(s => s.SeasonId).HasColumnName("season_id");
        builder.Property(s => s.RecordedBy).HasColumnName("recorded_by");
        builder.Property(s => s.OpponentName).HasColumnName("opponent_name").HasMaxLength(200);
        builder.Property(s => s.TeamScore).HasColumnName("team_score");
        builder.Property(s => s.OpponentScore).HasColumnName("opponent_score");
        builder.Property(s => s.Result).HasColumnName("result").HasMaxLength(20);
        builder.Property(s => s.Venue).HasColumnName("venue").HasMaxLength(200);
        builder.Property(s => s.CompetitionName).HasColumnName("competition_name").HasMaxLength(200);
        builder.Property(s => s.PossessionPercent).HasColumnName("possession_percent").HasPrecision(5, 2);
        builder.Property(s => s.TotalGoals).HasColumnName("total_goals");
        builder.Property(s => s.TotalAssists).HasColumnName("total_assists");
        builder.Property(s => s.ShotsOnTarget).HasColumnName("shots_on_target");
        builder.Property(s => s.TotalShots).HasColumnName("total_shots");
        builder.Property(s => s.PassesCompleted).HasColumnName("passes_completed");
        builder.Property(s => s.PassesAttempted).HasColumnName("passes_attempted");
        builder.Property(s => s.PassAccuracy).HasColumnName("pass_accuracy").HasPrecision(5, 2);
        builder.Property(s => s.Tackles).HasColumnName("tackles");
        builder.Property(s => s.Interceptions).HasColumnName("interceptions");
        builder.Property(s => s.YellowCards).HasColumnName("yellow_cards");
        builder.Property(s => s.RedCards).HasColumnName("red_cards");
        builder.Property(s => s.Notes).HasColumnName("notes");
        builder.Property(s => s.Category).HasColumnName("category").HasMaxLength(20);
        builder.Property(s => s.Granularity).HasColumnName("granularity").HasMaxLength(50);
        builder.Property(s => s.GameNo).HasColumnName("game_no").HasMaxLength(50);
        builder.Property(s => s.Matchup).HasColumnName("matchup").HasMaxLength(200);
        builder.Property(s => s.TwoPtMA).HasColumnName("two_pt_ma").HasMaxLength(20);
        builder.Property(s => s.ThreePtMA).HasColumnName("three_pt_ma").HasMaxLength(20);
        builder.Property(s => s.FtMA).HasColumnName("ft_ma").HasMaxLength(20);
        builder.Property(s => s.OffensiveRebounds).HasColumnName("offensive_rebounds");
        builder.Property(s => s.DefensiveRebounds).HasColumnName("defensive_rebounds");
        builder.Property(s => s.TotalRebounds).HasColumnName("total_rebounds");
        builder.Property(s => s.BbAssists).HasColumnName("bb_assists");
        builder.Property(s => s.Turnovers).HasColumnName("turnovers");
        builder.Property(s => s.Steals).HasColumnName("steals");
        builder.Property(s => s.Blocks).HasColumnName("blocks");
        builder.Property(s => s.PersonalFouls).HasColumnName("personal_fouls");
        builder.Property(s => s.FoulsDrawn).HasColumnName("fouls_drawn");
        builder.Property(s => s.Efficiency).HasColumnName("efficiency");
        builder.Property(s => s.Points).HasColumnName("points");
        builder.Property(s => s.Minutes).HasColumnName("minutes").HasMaxLength(20);
        builder.Property(s => s.TeamOffReb).HasColumnName("team_off_reb");
        builder.Property(s => s.TeamDefReb).HasColumnName("team_def_reb");
        builder.Property(s => s.TeamReb).HasColumnName("team_reb");
        builder.Property(s => s.TeamPF).HasColumnName("team_pf");
        builder.Property(s => s.TeamFD).HasColumnName("team_fd");
        builder.Property(s => s.SourceFile).HasColumnName("source_file").HasMaxLength(500);
        builder.Property(s => s.RawPdfPath).HasColumnName("raw_pdf_path").HasMaxLength(500);
        builder.Property(s => s.RawPdfFileName).HasColumnName("raw_pdf_file_name").HasMaxLength(300);
        builder.Property(s => s.RawPdfContentType).HasColumnName("raw_pdf_content_type").HasMaxLength(150);
        builder.Property(s => s.RawPdfSize).HasColumnName("raw_pdf_size");
        builder.Property(s => s.RawPdfUploadedAt).HasColumnName("raw_pdf_uploaded_at");
        builder.Property(s => s.ExtractedText).HasColumnName("extracted_text");
        builder.Property(s => s.CreatedAt).HasColumnName("created_at");
        builder.Property(s => s.UpdatedAt).HasColumnName("updated_at");

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

        builder.HasOne(s => s.Recorder)
            .WithMany()
            .HasForeignKey(s => s.RecordedBy)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => new { s.TeamId, s.EventId }).IsUnique();
        builder.HasIndex(s => new { s.TeamId, s.SeasonId });

        builder.HasQueryFilter(s =>
            s.Team.DeletedAt == null &&
            s.Event.DeletedAt == null &&
            s.Recorder.DeletedAt == null);
    }
}
