using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class EventPlanConfiguration : IEntityTypeConfiguration<EventPlan>
{
    public void Configure(EntityTypeBuilder<EventPlan> builder)
    {
        builder.ToTable("event_plan");

        builder.HasKey(ep => ep.EventPlanId);
        builder.Property(ep => ep.EventPlanId).HasColumnName("event_plan_id");
        builder.Property(ep => ep.EventId).HasColumnName("event_id");
        builder.Property(ep => ep.PlanId).HasColumnName("plan_id");
        builder.Property(ep => ep.LinkedByUserId).HasColumnName("linked_by_user_id");
        builder.Property(ep => ep.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(ep => new { ep.EventId, ep.PlanId }).IsUnique();

        builder.HasOne(ep => ep.Event)
            .WithMany()
            .HasForeignKey(ep => ep.EventId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(ep => ep.Plan)
            .WithMany()
            .HasForeignKey(ep => ep.PlanId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(ep => ep.LinkedByUser)
            .WithMany()
            .HasForeignKey(ep => ep.LinkedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasQueryFilter(ep =>
            ep.Event.DeletedAt == null &&
            ep.Plan.DeletedAt == null &&
            ep.LinkedByUser.DeletedAt == null);
    }
}
