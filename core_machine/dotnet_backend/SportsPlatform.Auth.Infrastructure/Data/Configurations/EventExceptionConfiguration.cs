using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class EventExceptionConfiguration : IEntityTypeConfiguration<EventException>
{
    public void Configure(EntityTypeBuilder<EventException> builder)
    {
        builder.ToTable("event_exception");
        builder.HasKey(e => e.EventExceptionId);
        builder.Property(e => e.EventExceptionId).HasColumnName("event_exception_id");
        builder.Property(e => e.EventId).HasColumnName("event_id");
        builder.Property(e => e.OriginalDate).HasColumnName("original_date");
        builder.Property(e => e.NewStartAt).HasColumnName("new_start_at");
        builder.Property(e => e.NewEndAt).HasColumnName("new_end_at");
        builder.Property(e => e.IsCancelled).HasColumnName("is_cancelled").HasDefaultValue(false);
        builder.Property(e => e.Notes).HasColumnName("notes");
        builder.Property(e => e.CreatedBy).HasColumnName("created_by");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(e => new { e.EventId, e.OriginalDate }).IsUnique();

        builder.HasOne(e => e.Event)
            .WithMany(ev => ev.Exceptions)
            .HasForeignKey(e => e.EventId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.Creator)
            .WithMany()
            .HasForeignKey(e => e.CreatedBy)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasQueryFilter(e =>
            e.Event.DeletedAt == null &&
            e.Creator.DeletedAt == null);
    }
}
