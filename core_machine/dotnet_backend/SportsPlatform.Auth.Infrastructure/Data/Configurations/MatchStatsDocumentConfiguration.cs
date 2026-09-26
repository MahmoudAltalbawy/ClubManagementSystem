using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class MatchStatsDocumentConfiguration : IEntityTypeConfiguration<MatchStatsDocument>
{
    public void Configure(EntityTypeBuilder<MatchStatsDocument> builder)
    {
        builder.ToTable("match_stats_document");
        builder.HasKey(d => d.DocumentId);
        builder.Property(d => d.DocumentId).HasColumnName("document_id");
        builder.Property(d => d.MatchStatsId).HasColumnName("match_stats_id");
        builder.Property(d => d.PdfType).HasColumnName("pdf_type").HasMaxLength(40);
        builder.Property(d => d.StoragePath).HasColumnName("storage_path").HasMaxLength(500);
        builder.Property(d => d.FileName).HasColumnName("file_name").HasMaxLength(300);
        builder.Property(d => d.ContentType).HasColumnName("content_type").HasMaxLength(150);
        builder.Property(d => d.FileSize).HasColumnName("file_size");
        builder.Property(d => d.ExtractedText).HasColumnName("extracted_text");
        builder.Property(d => d.UploadedAt).HasColumnName("uploaded_at");

        builder.HasOne(d => d.MatchStats)
            .WithMany(s => s.Documents)
            .HasForeignKey(d => d.MatchStatsId)
            .OnDelete(DeleteBehavior.Cascade);

        // At most one PDF of each type per match.
        builder.HasIndex(d => new { d.MatchStatsId, d.PdfType }).IsUnique();

        builder.HasQueryFilter(d =>
            d.MatchStats.Team.DeletedAt == null &&
            d.MatchStats.Event.DeletedAt == null &&
            d.MatchStats.Recorder.DeletedAt == null);
    }
}
