using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> builder)
    {
        builder.ToTable("event");
        builder.HasKey(e => e.EventId);
        builder.Property(e => e.EventId).HasColumnName("event_id");
        builder.Property(e => e.TeamId).HasColumnName("team_id");
        builder.Property(e => e.SeasonId).HasColumnName("season_id");
        builder.Property(e => e.CreatedBy).HasColumnName("created_by");
        builder.Property(e => e.Title).HasColumnName("title").HasMaxLength(200).IsRequired();
        builder.Property(e => e.Description).HasColumnName("description");
        builder.Property(e => e.Location).HasColumnName("location").HasMaxLength(200);
        builder.Property(e => e.LocationLatitude).HasColumnName("location_latitude");
        builder.Property(e => e.LocationLongitude).HasColumnName("location_longitude");
        builder.Property(e => e.StartAt).HasColumnName("start_at");
        builder.Property(e => e.EndAt).HasColumnName("end_at");
        builder.Property(e => e.EventType).HasColumnName("event_type").HasColumnType("event_type");
        builder.Property(e => e.Timezone).HasColumnName("timezone").HasMaxLength(100).HasDefaultValue("UTC");
        builder.Property(e => e.RecurrenceRule).HasColumnName("recurrence_rule");
        builder.Property(e => e.RecurrenceEndDate).HasColumnName("recurrence_end_date");
        builder.Property(e => e.DeletedAt).HasColumnName("deleted_at");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at");

        builder.HasOne(e => e.Team)
            .WithMany(t => t.Events)
            .HasForeignKey(e => e.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.Season)
            .WithMany(s => s.Events)
            .HasForeignKey(e => e.SeasonId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.Creator)
            .WithMany()
            .HasForeignKey(e => e.CreatedBy)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasQueryFilter(e => e.DeletedAt == null);
    }
}
