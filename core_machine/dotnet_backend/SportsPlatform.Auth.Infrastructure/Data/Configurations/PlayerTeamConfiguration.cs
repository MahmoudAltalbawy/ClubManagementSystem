using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class PlayerTeamConfiguration : IEntityTypeConfiguration<PlayerTeam>
{
    public void Configure(EntityTypeBuilder<PlayerTeam> builder)
    {
        builder.ToTable("player_team");

        builder.HasKey(pt => pt.Id);
        builder.Property(pt => pt.Id).HasColumnName("id");
        builder.Property(pt => pt.PlayerId).HasColumnName("player_id");
        builder.Property(pt => pt.TeamId).HasColumnName("team_id");
        builder.Property(pt => pt.JoinedDate).HasColumnName("joined_date");
        builder.Property(pt => pt.LeftDate).HasColumnName("left_date");
        builder.Property(pt => pt.IsCurrent).HasColumnName("is_current");
        builder.Property(pt => pt.CreatedAt).HasColumnName("created_at");
        builder.Property(pt => pt.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(pt => pt.TeamId);
        builder.HasIndex(pt => pt.PlayerId);

        builder.HasOne(pt => pt.Player)
            .WithMany()
            .HasForeignKey(pt => pt.PlayerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(pt => pt.Team)
            .WithMany()
            .HasForeignKey(pt => pt.TeamId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasQueryFilter(pt =>
            pt.Player.DeletedAt == null &&
            pt.Team.DeletedAt == null);
    }
}
