using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class CoachingLineupPlayerConfiguration : IEntityTypeConfiguration<CoachingLineupPlayer>
{
    public void Configure(EntityTypeBuilder<CoachingLineupPlayer> builder)
    {
        builder.ToTable("coaching_lineup_player");
        builder.HasKey(p => p.LineupPlayerId);
        builder.Property(p => p.LineupPlayerId).HasColumnName("lineup_player_id");
        builder.Property(p => p.LineupId).HasColumnName("lineup_id");
        builder.Property(p => p.PlayerUserId).HasColumnName("player_user_id");
        builder.Property(p => p.Position).HasColumnName("position").HasMaxLength(80);
        builder.Property(p => p.Unit).HasColumnName("unit").HasMaxLength(40);
        builder.Property(p => p.SortOrder).HasColumnName("sort_order");
        builder.Property(p => p.Instructions).HasColumnName("instructions");

        builder.HasOne(p => p.Lineup)
            .WithMany(l => l.Players)
            .HasForeignKey(p => p.LineupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(p => p.Player)
            .WithMany()
            .HasForeignKey(p => p.PlayerUserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(p => new { p.LineupId, p.PlayerUserId }).IsUnique();

        builder.HasQueryFilter(p =>
            p.Lineup.DeletedAt == null &&
            p.Player.DeletedAt == null);
    }
}
