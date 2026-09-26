using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class AppNotificationConfiguration : IEntityTypeConfiguration<AppNotification>
{
    public void Configure(EntityTypeBuilder<AppNotification> builder)
    {
        builder.ToTable("app_notification");
        builder.HasKey(n => n.NotificationId);

        builder.Property(n => n.NotificationId).HasColumnName("notification_id");
        builder.Property(n => n.RecipientUserId).HasColumnName("recipient_user_id");
        builder.Property(n => n.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(n => n.ClubId).HasColumnName("club_id");
        builder.Property(n => n.TeamId).HasColumnName("team_id");
        builder.Property(n => n.Type).HasColumnName("type").HasMaxLength(80).IsRequired();
        builder.Property(n => n.Priority).HasColumnName("priority").HasMaxLength(30).IsRequired();
        builder.Property(n => n.DeliveryPolicy).HasColumnName("delivery_policy").HasMaxLength(40).IsRequired();
        builder.Property(n => n.Title).HasColumnName("title").HasMaxLength(200).IsRequired();
        builder.Property(n => n.Body).HasColumnName("body").HasMaxLength(1000).IsRequired();
        builder.Property(n => n.TargetType).HasColumnName("target_type").HasMaxLength(80);
        builder.Property(n => n.TargetId).HasColumnName("target_id");
        builder.Property(n => n.TargetRoute).HasColumnName("target_route").HasMaxLength(300);
        builder.Property(n => n.MetadataJson).HasColumnName("metadata_json").HasColumnType("jsonb");
        builder.Property(n => n.UniqueKey).HasColumnName("unique_key").HasMaxLength(250);
        builder.Property(n => n.CreatedAt).HasColumnName("created_at");
        builder.Property(n => n.ReadAt).HasColumnName("read_at");
        builder.Property(n => n.EmailSentAt).HasColumnName("email_sent_at");

        builder.HasIndex(n => new { n.RecipientUserId, n.CreatedAt });
        builder.HasIndex(n => new { n.RecipientUserId, n.ReadAt });
        builder.HasIndex(n => n.TeamId);
        builder.HasIndex(n => n.Type);
        builder.HasIndex(n => n.UniqueKey).IsUnique().HasFilter("unique_key IS NOT NULL");

        builder.HasOne(n => n.RecipientUser)
            .WithMany()
            .HasForeignKey(n => n.RecipientUserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(n => n.ActorUser)
            .WithMany()
            .HasForeignKey(n => n.ActorUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(n => n.Club)
            .WithMany()
            .HasForeignKey(n => n.ClubId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(n => n.Team)
            .WithMany()
            .HasForeignKey(n => n.TeamId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasQueryFilter(n =>
            n.RecipientUser.DeletedAt == null &&
            (n.ActorUserId == null || n.ActorUser!.DeletedAt == null) &&
            (n.ClubId == null || n.Club!.DeletedAt == null) &&
            (n.TeamId == null || n.Team!.DeletedAt == null));
    }
}
