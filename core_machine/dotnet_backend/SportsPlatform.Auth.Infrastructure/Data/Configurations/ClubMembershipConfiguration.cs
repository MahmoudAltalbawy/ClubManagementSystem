using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class ClubMembershipConfiguration : IEntityTypeConfiguration<ClubMembership>
{
    public void Configure(EntityTypeBuilder<ClubMembership> builder)
    {
        builder.ToTable("club_membership");
        builder.HasKey(cm => cm.ClubMembershipId);
        builder.Property(cm => cm.ClubMembershipId).HasColumnName("club_membership_id");
        builder.Property(cm => cm.ClubId).HasColumnName("club_id");
        builder.Property(cm => cm.UserId).HasColumnName("user_id");
        builder.Property(cm => cm.Role).HasColumnName("role").HasColumnType("role_name_type");
        builder.Property(cm => cm.Status).HasColumnName("status").HasColumnType("membership_status");
        builder.Property(cm => cm.InvitedBy).HasColumnName("invited_by");
        builder.Property(cm => cm.JoinedAt).HasColumnName("joined_at");
        builder.Property(cm => cm.CreatedAt).HasColumnName("created_at");
        builder.Property(cm => cm.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(cm => new { cm.ClubId, cm.UserId }).IsUnique();
        builder.HasIndex(cm => cm.UserId);

        builder.HasOne(cm => cm.Club)
               .WithMany(c => c.Memberships)
               .HasForeignKey(cm => cm.ClubId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(cm => cm.User)
               .WithMany(u => u.ClubMemberships)
               .HasForeignKey(cm => cm.UserId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(cm => cm.InvitedByUser)
               .WithMany()
               .HasForeignKey(cm => cm.InvitedBy)
               .OnDelete(DeleteBehavior.SetNull);

        builder.HasQueryFilter(cm =>
            cm.Club.DeletedAt == null &&
            cm.User.DeletedAt == null &&
            (cm.InvitedBy == null || cm.InvitedByUser!.DeletedAt == null));
    }
}
