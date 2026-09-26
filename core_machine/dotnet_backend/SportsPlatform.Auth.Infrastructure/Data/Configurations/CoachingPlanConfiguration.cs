using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class CoachingPlanConfiguration : IEntityTypeConfiguration<CoachingPlan>
{
    public void Configure(EntityTypeBuilder<CoachingPlan> builder)
    {
        builder.ToTable("coaching_plan");
        builder.HasKey(p => p.PlanId);
        builder.Property(p => p.PlanId).HasColumnName("plan_id");
        builder.Property(p => p.TeamId).HasColumnName("team_id");
        builder.Property(p => p.CreatedBy).HasColumnName("created_by");
        builder.Property(p => p.Title).HasColumnName("title").HasMaxLength(200).IsRequired();
        builder.Property(p => p.Description).HasColumnName("description");
        builder.Property(p => p.Content).HasColumnName("content").IsRequired();
        builder.Property(p => p.Visibility).HasColumnName("visibility").HasColumnType("plan_visibility");
        builder.Property(p => p.DeletedAt).HasColumnName("deleted_at");
        builder.Property(p => p.CreatedAt).HasColumnName("created_at");
        builder.Property(p => p.UpdatedAt).HasColumnName("updated_at");

        builder.HasOne(p => p.Team)
            .WithMany()
            .HasForeignKey(p => p.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(p => p.Creator)
            .WithMany()
            .HasForeignKey(p => p.CreatedBy)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasQueryFilter(p => p.DeletedAt == null);
    }
}
