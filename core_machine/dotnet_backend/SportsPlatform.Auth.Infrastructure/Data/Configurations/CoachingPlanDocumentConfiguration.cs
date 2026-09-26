using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class CoachingPlanDocumentConfiguration : IEntityTypeConfiguration<CoachingPlanDocument>
{
    public void Configure(EntityTypeBuilder<CoachingPlanDocument> builder)
    {
        builder.ToTable("coaching_plan_document");

        builder.HasKey(d => d.DocumentId);
        builder.Property(d => d.DocumentId).HasColumnName("document_id");
        builder.Property(d => d.PlanId).HasColumnName("plan_id");
        builder.Property(d => d.UploadedByUserId).HasColumnName("uploaded_by_user_id");
        builder.Property(d => d.FileName).HasColumnName("file_name").HasMaxLength(500);
        builder.Property(d => d.OriginalFileName).HasColumnName("original_file_name").HasMaxLength(500);
        builder.Property(d => d.ContentType).HasColumnName("content_type").HasMaxLength(200);
        builder.Property(d => d.Description).HasColumnName("description").HasMaxLength(2000);
        builder.Property(d => d.UploadedByRole).HasColumnName("uploaded_by_role").HasMaxLength(100);
        builder.Property(d => d.FileSize).HasColumnName("file_size");
        builder.Property(d => d.StoragePath).HasColumnName("storage_path").HasMaxLength(1000);
        builder.Property(d => d.DeletedAt).HasColumnName("deleted_at");
        builder.Property(d => d.CreatedAt).HasColumnName("created_at");
        builder.Property(d => d.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(d => d.PlanId);

        builder.HasOne(d => d.Plan)
            .WithMany(p => p.Documents)
            .HasForeignKey(d => d.PlanId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(d => d.UploadedByUser)
            .WithMany()
            .HasForeignKey(d => d.UploadedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasQueryFilter(d => d.DeletedAt == null);
    }
}
