using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
{
    public void Configure(EntityTypeBuilder<Invitation> builder)
    {
        builder.ToTable("invitation");
        builder.HasKey(i => i.InvitationId);
        builder.Property(i => i.InvitationId).HasColumnName("invitation_id");
        builder.Property(i => i.Token).HasColumnName("token").HasMaxLength(128).IsRequired();
        builder.Property(i => i.Email).HasColumnName("email").HasMaxLength(320).IsRequired();
        builder.Property(i => i.Role).HasColumnName("role").HasColumnType("role_name_type");
        builder.Property(i => i.ClubId).HasColumnName("club_id");
        builder.Property(i => i.TeamId).HasColumnName("team_id");
        builder.Property(i => i.InvitedBy).HasColumnName("invited_by");
        builder.Property(i => i.Status).HasColumnName("status").HasColumnType("invitation_status");
        builder.Property(i => i.PlayerPosition).HasColumnName("player_position").HasMaxLength(50);
        builder.Property(i => i.JerseyNumber).HasColumnName("jersey_number");
        builder.Property(i => i.ExpiresAt).HasColumnName("expires_at");
        builder.Property(i => i.AcceptedAt).HasColumnName("accepted_at");
        builder.Property(i => i.AcceptedByUserId).HasColumnName("accepted_by_user_id");
        builder.Property(i => i.ResolvedAt).HasColumnName("resolved_at");
        builder.Property(i => i.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(i => i.Token).IsUnique();
        builder.HasIndex(i => i.Email);
        builder.HasIndex(i => i.ClubId);
        builder.HasIndex(i => i.TeamId);

        builder.HasOne(i => i.Club)
               .WithMany(c => c.Invitations)
               .HasForeignKey(i => i.ClubId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(i => i.Team)
               .WithMany(t => t.Invitations)
               .HasForeignKey(i => i.TeamId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(i => i.InvitedByUser)
               .WithMany()
               .HasForeignKey(i => i.InvitedBy)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(i => i.AcceptedByUser)
               .WithMany()
               .HasForeignKey(i => i.AcceptedByUserId)
               .OnDelete(DeleteBehavior.SetNull);

        builder.HasQueryFilter(i =>
            i.Club.DeletedAt == null &&
            (i.TeamId == null || i.Team!.DeletedAt == null) &&
            i.InvitedByUser.DeletedAt == null &&
            (i.AcceptedByUserId == null || i.AcceptedByUser!.DeletedAt == null));
    }
}
