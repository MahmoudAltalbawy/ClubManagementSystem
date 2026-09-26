using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class EventDocumentConfiguration : IEntityTypeConfiguration<EventDocument>
{
    public void Configure(EntityTypeBuilder<EventDocument> builder)
    {
        builder.ToTable("event_document");

        builder.HasKey(d => d.DocumentId);
        builder.Property(d => d.DocumentId).HasColumnName("document_id");
        builder.Property(d => d.EventId).HasColumnName("event_id");
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

        builder.HasIndex(d => d.EventId);

        builder.HasOne(d => d.Event)
            .WithMany()
            .HasForeignKey(d => d.EventId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(d => d.UploadedByUser)
            .WithMany()
            .HasForeignKey(d => d.UploadedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasQueryFilter(d => d.DeletedAt == null);
    }
}
