using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class FitnessRecordConfiguration : IEntityTypeConfiguration<FitnessRecord>
{
    public void Configure(EntityTypeBuilder<FitnessRecord> builder)
    {
        builder.ToTable("fitness_record");
        builder.HasKey(f => f.FitnessId);
        builder.Property(f => f.FitnessId).HasColumnName("fitness_id");
        builder.Property(f => f.TeamId).HasColumnName("team_id");
        builder.Property(f => f.PlayerId).HasColumnName("player_id");
        builder.Property(f => f.FitnessUserId).HasColumnName("fitness_user_id");
        builder.Property(f => f.TestDate).HasColumnName("test_date");
        builder.Property(f => f.Height).HasColumnName("height");
        builder.Property(f => f.Weight).HasColumnName("weight");
        builder.Property(f => f.Bmi).HasColumnName("bmi");
        builder.Property(f => f.BodyFatPct).HasColumnName("body_fat_pct");
        builder.Property(f => f.SpeedTestResult).HasColumnName("speed_test_result");
        builder.Property(f => f.EnduranceScore).HasColumnName("endurance_score");
        builder.Property(f => f.CustomTestName).HasColumnName("custom_test_name").HasMaxLength(100);
        builder.Property(f => f.CustomTestResult).HasColumnName("custom_test_result");
        builder.Property(f => f.CreatedBy).HasColumnName("created_by");
        builder.Property(f => f.UpdatedBy).HasColumnName("updated_by");
        builder.Property(f => f.CreatedAt).HasColumnName("created_at");
        builder.Property(f => f.UpdatedAt).HasColumnName("updated_at");

        builder.HasOne(f => f.Team)
            .WithMany()
            .HasForeignKey(f => f.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(f => f.Player)
            .WithMany()
            .HasForeignKey(f => f.PlayerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(f => f.FitnessUser)
            .WithMany()
            .HasForeignKey(f => f.FitnessUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(f => f.CreatedByUser)
            .WithMany()
            .HasForeignKey(f => f.CreatedBy)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(f => f.UpdatedByUser)
            .WithMany()
            .HasForeignKey(f => f.UpdatedBy)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasQueryFilter(f =>
            f.Team.DeletedAt == null &&
            f.Player.DeletedAt == null &&
            (f.FitnessUserId == null || f.FitnessUser!.DeletedAt == null) &&
            (f.CreatedBy == null || f.CreatedByUser!.DeletedAt == null) &&
            (f.UpdatedBy == null || f.UpdatedByUser!.DeletedAt == null));
    }
}
