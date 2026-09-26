using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class MatchLineupAnalysisConfiguration : IEntityTypeConfiguration<MatchLineupAnalysis>
{
    public void Configure(EntityTypeBuilder<MatchLineupAnalysis> builder)
    {
        builder.ToTable("match_lineup_analysis");
        builder.HasKey(l => l.LineupId);
        builder.Property(l => l.LineupId).HasColumnName("lineup_id");
        builder.Property(l => l.ReportId).HasColumnName("report_id");
        builder.Property(l => l.TeamCode).HasColumnName("team_code").HasMaxLength(20);
        builder.Property(l => l.LineupPlayers).HasColumnName("lineup_players");
        builder.Property(l => l.TimeOnCourt).HasColumnName("time_on_court").HasMaxLength(12);
        builder.Property(l => l.TimeSeconds).HasColumnName("time_seconds");
        builder.Property(l => l.PointsFor).HasColumnName("points_for");
        builder.Property(l => l.PointsAgainst).HasColumnName("points_against");
        builder.Property(l => l.ScoreDiff).HasColumnName("score_diff");
        builder.Property(l => l.PointsPerMinute).HasColumnName("points_per_minute").HasPrecision(8, 4);
        builder.Property(l => l.Rebounds).HasColumnName("rebounds");
        builder.Property(l => l.Steals).HasColumnName("steals");
        builder.Property(l => l.Turnovers).HasColumnName("turnovers");
        builder.Property(l => l.Assists).HasColumnName("assists");

        builder.HasOne(l => l.Report)
            .WithMany(r => r.Lineups)
            .HasForeignKey(l => l.ReportId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(l => new { l.ReportId, l.ScoreDiff });
    }
}
