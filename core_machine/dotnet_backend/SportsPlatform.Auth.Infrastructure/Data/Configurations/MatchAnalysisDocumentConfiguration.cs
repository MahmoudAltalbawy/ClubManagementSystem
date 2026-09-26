using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class MatchAnalysisDocumentConfiguration : IEntityTypeConfiguration<MatchAnalysisDocument>
{
    public void Configure(EntityTypeBuilder<MatchAnalysisDocument> builder)
    {
        builder.ToTable("match_analysis_document");
        builder.HasKey(d => d.DocumentId);
        builder.Property(d => d.DocumentId).HasColumnName("document_id");
        builder.Property(d => d.ReportId).HasColumnName("report_id");
        builder.Property(d => d.DocumentType).HasColumnName("document_type").HasMaxLength(80);
        builder.Property(d => d.FileName).HasColumnName("file_name").HasMaxLength(240);

        builder.HasOne(d => d.Report)
            .WithMany(r => r.Documents)
            .HasForeignKey(d => d.ReportId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
