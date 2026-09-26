using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class CoachNoteConfiguration : IEntityTypeConfiguration<CoachNote>
{
    public void Configure(EntityTypeBuilder<CoachNote> builder)
    {
        builder.ToTable("coach_note");

        builder.HasKey(n => n.NoteId);
        builder.Property(n => n.NoteId).HasColumnName("note_id");
        builder.Property(n => n.EventId).HasColumnName("event_id");
        builder.Property(n => n.TeamId).HasColumnName("team_id");
        builder.Property(n => n.AuthorUserId).HasColumnName("author_user_id");
        builder.Property(n => n.AuthorRole).HasColumnName("author_role").HasMaxLength(100);
        builder.Property(n => n.Body).HasColumnName("body").HasMaxLength(4000);
        builder.Property(n => n.DeletedAt).HasColumnName("deleted_at");
        builder.Property(n => n.CreatedAt).HasColumnName("created_at");
        builder.Property(n => n.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(n => n.EventId);

        builder.HasOne(n => n.Event)
            .WithMany()
            .HasForeignKey(n => n.EventId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(n => n.AuthorUser)
            .WithMany()
            .HasForeignKey(n => n.AuthorUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasQueryFilter(n => n.DeletedAt == null);
    }
}
