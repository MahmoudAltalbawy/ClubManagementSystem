using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class TeamMembershipConfiguration : IEntityTypeConfiguration<TeamMembership>
{
    public void Configure(EntityTypeBuilder<TeamMembership> builder)
    {
        builder.ToTable("team_membership");
        builder.HasKey(tm => tm.TeamMembershipId);
        builder.Property(tm => tm.TeamMembershipId).HasColumnName("team_membership_id");
        builder.Property(tm => tm.TeamId).HasColumnName("team_id");
        builder.Property(tm => tm.UserId).HasColumnName("user_id");
        builder.Property(tm => tm.Role).HasColumnName("role").HasColumnType("role_name_type");
        builder.Property(tm => tm.Status).HasColumnName("status").HasColumnType("membership_status");
        builder.Property(tm => tm.InvitedBy).HasColumnName("invited_by");
        builder.Property(tm => tm.JoinedAt).HasColumnName("joined_at");
        builder.Property(tm => tm.CreatedAt).HasColumnName("created_at");
        builder.Property(tm => tm.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(tm => new { tm.TeamId, tm.UserId }).IsUnique();
        builder.HasIndex(tm => tm.UserId);

        builder.HasOne(tm => tm.Team)
               .WithMany(t => t.Memberships)
               .HasForeignKey(tm => tm.TeamId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(tm => tm.User)
               .WithMany(u => u.TeamMemberships)
               .HasForeignKey(tm => tm.UserId)
               .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(tm => tm.InvitedByUser)
               .WithMany()
               .HasForeignKey(tm => tm.InvitedBy)
               .OnDelete(DeleteBehavior.SetNull);

        builder.HasQueryFilter(tm =>
            tm.Team.DeletedAt == null &&
            tm.User.DeletedAt == null &&
            (tm.InvitedBy == null || tm.InvitedByUser!.DeletedAt == null));
    }
}
