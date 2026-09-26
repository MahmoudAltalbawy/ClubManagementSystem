using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SportsPlatform.Auth.Core.Entities;

namespace SportsPlatform.Auth.Infrastructure.Data.Configurations;

public class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("conversation");
        builder.HasKey(c => c.ConversationId);
        builder.Property(c => c.ConversationId).HasColumnName("conversation_id");
        builder.Property(c => c.IsGroup).HasColumnName("is_group");
        builder.Property(c => c.Name).HasColumnName("name").HasMaxLength(200);
        builder.Property(c => c.CreatedAt).HasColumnName("created_at");
    }
}

public class ConversationParticipantConfiguration : IEntityTypeConfiguration<ConversationParticipant>
{
    public void Configure(EntityTypeBuilder<ConversationParticipant> builder)
    {
        builder.ToTable("conversation_participant");
        builder.HasKey(cp => new { cp.ConversationId, cp.UserId });
        builder.Property(cp => cp.ConversationId).HasColumnName("conversation_id");
        builder.Property(cp => cp.UserId).HasColumnName("user_id");
        builder.Property(cp => cp.JoinedAt).HasColumnName("joined_at");

        builder.HasOne(cp => cp.Conversation)
            .WithMany(c => c.Participants)
            .HasForeignKey(cp => cp.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(cp => cp.User)
            .WithMany()
            .HasForeignKey(cp => cp.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasQueryFilter(cp => cp.User.DeletedAt == null);
    }
}

public class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("message");
        builder.HasKey(m => m.MessageId);
        builder.Property(m => m.MessageId).HasColumnName("message_id");
        builder.Property(m => m.ConversationId).HasColumnName("conversation_id");
        builder.Property(m => m.SenderUserId).HasColumnName("sender_user_id");
        builder.Property(m => m.Content).HasColumnName("content").IsRequired();
        builder.Property(m => m.SentAt).HasColumnName("sent_at");
        builder.Property(m => m.IsRead).HasColumnName("is_read").HasDefaultValue(false);
        builder.Property(m => m.EditedAt).HasColumnName("edited_at");
        builder.Property(m => m.IsDeleted).HasColumnName("is_deleted").HasDefaultValue(false);
        builder.Property(m => m.MessageType).HasColumnName("message_type").HasMaxLength(50).HasDefaultValue("text");
        builder.Property(m => m.MediaUrl).HasColumnName("media_url").HasMaxLength(500);
        builder.Property(m => m.MediaFileName).HasColumnName("media_file_name").HasMaxLength(300);
        builder.Property(m => m.LocationLatitude).HasColumnName("location_latitude");
        builder.Property(m => m.LocationLongitude).HasColumnName("location_longitude");
        builder.Property(m => m.LocationLabel).HasColumnName("location_label").HasMaxLength(200);

        builder.HasOne(m => m.Conversation)
            .WithMany(c => c.Messages)
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.Sender)
            .WithMany()
            .HasForeignKey(m => m.SenderUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(m => new { m.ConversationId, m.SentAt });

        builder.HasQueryFilter(m => m.Sender.DeletedAt == null);
    }
}

public class MessageReactionConfiguration : IEntityTypeConfiguration<MessageReaction>
{
    public void Configure(EntityTypeBuilder<MessageReaction> builder)
    {
        builder.ToTable("message_reaction");
        builder.HasKey(r => r.ReactionId);
        builder.Property(r => r.ReactionId).HasColumnName("reaction_id");
        builder.Property(r => r.MessageId).HasColumnName("message_id");
        builder.Property(r => r.UserId).HasColumnName("user_id");
        builder.Property(r => r.Emoji).HasColumnName("emoji").HasMaxLength(50).IsRequired();
        builder.Property(r => r.CreatedAt).HasColumnName("created_at");

        builder.HasOne(r => r.Message)
            .WithMany(m => m.Reactions)
            .HasForeignKey(r => r.MessageId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.User)
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => new { r.MessageId, r.UserId, r.Emoji }).IsUnique();

        builder.HasQueryFilter(r =>
            r.Message.Sender.DeletedAt == null &&
            r.User.DeletedAt == null);
    }
}

public class MessageReadReceiptConfiguration : IEntityTypeConfiguration<MessageReadReceipt>
{
    public void Configure(EntityTypeBuilder<MessageReadReceipt> builder)
    {
        builder.ToTable("message_read_receipt");
        builder.HasKey(r => new { r.MessageId, r.UserId });
        builder.Property(r => r.MessageId).HasColumnName("message_id");
        builder.Property(r => r.UserId).HasColumnName("user_id");
        builder.Property(r => r.ReadAt).HasColumnName("read_at");

        builder.HasOne(r => r.Message)
            .WithMany(m => m.ReadReceipts)
            .HasForeignKey(r => r.MessageId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(r => r.User)
            .WithMany()
            .HasForeignKey(r => r.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => r.UserId);

        builder.HasQueryFilter(r =>
            r.Message.Sender.DeletedAt == null &&
            r.User.DeletedAt == null);
    }
}
