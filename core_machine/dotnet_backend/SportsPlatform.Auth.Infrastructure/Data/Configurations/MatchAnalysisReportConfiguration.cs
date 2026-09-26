using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class MatchAnalysisReportConfiguration : IEntityTypeConfiguration<MatchAnalysisReport>
{
    public void Configure(EntityTypeBuilder<MatchAnalysisReport> builder)
    {
        builder.ToTable("match_analysis_report");
        builder.HasKey(r => r.ReportId);
        builder.Property(r => r.ReportId).HasColumnName("report_id");
        builder.Property(r => r.TeamId).HasColumnName("team_id");
        builder.Property(r => r.TeamCode).HasColumnName("team_code").HasMaxLength(20);
        builder.Property(r => r.OpponentCode).HasColumnName("opponent_code").HasMaxLength(20);
        builder.Property(r => r.OpponentName).HasColumnName("opponent_name").HasMaxLength(120);
        builder.Property(r => r.MatchDate).HasColumnName("match_date");
        builder.Property(r => r.Competition).HasColumnName("competition").HasMaxLength(160);
        builder.Property(r => r.Venue).HasColumnName("venue").HasMaxLength(180);
        builder.Property(r => r.GameNo).HasColumnName("game_no").HasMaxLength(80);
        builder.Property(r => r.TeamScore).HasColumnName("team_score");
        builder.Property(r => r.OpponentScore).HasColumnName("opponent_score");
        builder.Property(r => r.Result).HasColumnName("result").HasMaxLength(20);
        builder.Property(r => r.Summary).HasColumnName("summary");
        builder.Property(r => r.CreatedAt).HasColumnName("created_at");
        builder.Property(r => r.UpdatedAt).HasColumnName("updated_at");

        builder.HasOne(r => r.Team)
            .WithMany()
            .HasForeignKey(r => r.TeamId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(r => r.MatchDate);
        builder.HasIndex(r => r.TeamId);
    }
}
