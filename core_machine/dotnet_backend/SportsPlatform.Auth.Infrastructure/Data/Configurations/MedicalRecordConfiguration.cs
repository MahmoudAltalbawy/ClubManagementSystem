using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class MedicalRecordConfiguration : IEntityTypeConfiguration<MedicalRecord>
{
    public void Configure(EntityTypeBuilder<MedicalRecord> builder)
    {
        builder.ToTable("medical_record");
        builder.HasKey(m => m.RecordId);
        builder.Property(m => m.RecordId).HasColumnName("record_id");
        builder.Property(m => m.TeamId).HasColumnName("team_id");
        builder.Property(m => m.PlayerId).HasColumnName("player_id");
        builder.Property(m => m.DoctorUserId).HasColumnName("doctor_user_id");
        builder.Property(m => m.RecordDate).HasColumnName("record_date");
        builder.Property(m => m.InjuryType).HasColumnName("injury_type").HasMaxLength(200);
        builder.Property(m => m.Diagnosis).HasColumnName("diagnosis");
        builder.Property(m => m.ExpectedReturnDate).HasColumnName("expected_return_date");
        builder.Property(m => m.RecoveryTips).HasColumnName("recovery_tips");
        builder.Property(m => m.IsCleared).HasColumnName("is_cleared").HasDefaultValue(false);
        builder.Property(m => m.CreatedBy).HasColumnName("created_by");
        builder.Property(m => m.UpdatedBy).HasColumnName("updated_by");
        builder.Property(m => m.CreatedAt).HasColumnName("created_at");
        builder.Property(m => m.UpdatedAt).HasColumnName("updated_at");

        builder.HasOne(m => m.Team)
            .WithMany()
            .HasForeignKey(m => m.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.Player)
            .WithMany()
            .HasForeignKey(m => m.PlayerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.DoctorUser)
            .WithMany()
            .HasForeignKey(m => m.DoctorUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(m => m.CreatedByUser)
            .WithMany()
            .HasForeignKey(m => m.CreatedBy)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(m => m.UpdatedByUser)
            .WithMany()
            .HasForeignKey(m => m.UpdatedBy)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(m => m.DocumentRequests)
            .WithOne(dr => dr.Record)
            .HasForeignKey(dr => dr.RecordId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasQueryFilter(m =>
            m.Team.DeletedAt == null &&
            m.Player.DeletedAt == null &&
            (m.DoctorUserId == null || m.DoctorUser!.DeletedAt == null) &&
            (m.CreatedBy == null || m.CreatedByUser!.DeletedAt == null) &&
            (m.UpdatedBy == null || m.UpdatedByUser!.DeletedAt == null));
    }
}
