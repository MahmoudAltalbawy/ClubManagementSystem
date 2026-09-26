using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class AttendanceConfiguration : IEntityTypeConfiguration<Attendance>
{
    public void Configure(EntityTypeBuilder<Attendance> builder)
    {
        builder.ToTable("attendance");
        builder.HasKey(a => a.AttendanceId);
        builder.Property(a => a.AttendanceId).HasColumnName("attendance_id");
        builder.Property(a => a.EventId).HasColumnName("event_id");
        builder.Property(a => a.InstanceDate).HasColumnName("instance_date");
        builder.Property(a => a.PlayerId).HasColumnName("player_id");
        builder.Property(a => a.RecordedByUserId).HasColumnName("recorded_by_user_id");
        builder.Property(a => a.Status).HasColumnName("status").HasColumnType("attendance_status");
        builder.Property(a => a.RecordedAt).HasColumnName("recorded_at");
        builder.Property(a => a.Notes).HasColumnName("notes");
        builder.Property(a => a.CreatedAt).HasColumnName("created_at");
        builder.Property(a => a.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(a => new { a.EventId, a.InstanceDate, a.PlayerId }).IsUnique();

        builder.HasOne(a => a.Event)
            .WithMany(e => e.Attendances)
            .HasForeignKey(a => a.EventId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(a => a.Player)
            .WithMany(p => p.Attendances)
            .HasForeignKey(a => a.PlayerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(a => a.RecordedByUser)
            .WithMany()
            .HasForeignKey(a => a.RecordedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasQueryFilter(a =>
            a.Event.DeletedAt == null &&
            a.Player.DeletedAt == null &&
            (a.RecordedByUserId == null || a.RecordedByUser!.DeletedAt == null));
    }
}
