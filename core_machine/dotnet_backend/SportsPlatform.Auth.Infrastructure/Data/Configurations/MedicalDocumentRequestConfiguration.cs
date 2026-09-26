using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class MedicalDocumentRequestConfiguration : IEntityTypeConfiguration<MedicalDocumentRequest>
{
    public void Configure(EntityTypeBuilder<MedicalDocumentRequest> builder)
    {
        builder.ToTable("medical_document_request");
        builder.HasKey(r => r.RequestId);

        builder.Property(r => r.RequestId).HasColumnName("request_id");
        builder.Property(r => r.RecordId).HasColumnName("record_id");
        builder.Property(r => r.DocumentName).HasColumnName("document_name").HasMaxLength(200);
        builder.Property(r => r.Note).HasColumnName("note");
        builder.Property(r => r.Status).HasColumnName("status").HasMaxLength(20);
        builder.Property(r => r.RequestedBy).HasColumnName("requested_by");
        builder.Property(r => r.UploadedBy).HasColumnName("uploaded_by");
        builder.Property(r => r.OriginalFileName).HasColumnName("original_file_name").HasMaxLength(255);
        builder.Property(r => r.StoredFileName).HasColumnName("stored_file_name").HasMaxLength(255);
        builder.Property(r => r.ContentType).HasColumnName("content_type").HasMaxLength(100);
        builder.Property(r => r.FileSizeBytes).HasColumnName("file_size_bytes");
        builder.Property(r => r.RequestedAt).HasColumnName("requested_at");
        builder.Property(r => r.UploadedAt).HasColumnName("uploaded_at");
        builder.Property(r => r.CreatedAt).HasColumnName("created_at");
        builder.Property(r => r.UpdatedAt).HasColumnName("updated_at");

        builder.HasOne(r => r.Record)
            .WithMany(m => m.DocumentRequests)
            .HasForeignKey(r => r.RecordId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.RequestedByUser)
            .WithMany()
            .HasForeignKey(r => r.RequestedBy)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.UploadedByUser)
            .WithMany()
            .HasForeignKey(r => r.UploadedBy)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasQueryFilter(r =>
            r.Record.Team.DeletedAt == null &&
            r.Record.Player.DeletedAt == null &&
            (r.Record.DoctorUserId == null || r.Record.DoctorUser!.DeletedAt == null) &&
            (r.Record.CreatedBy == null || r.Record.CreatedByUser!.DeletedAt == null) &&
            (r.Record.UpdatedBy == null || r.Record.UpdatedByUser!.DeletedAt == null) &&
            r.RequestedByUser.DeletedAt == null &&
            (r.UploadedBy == null || r.UploadedByUser!.DeletedAt == null));
    }
}
