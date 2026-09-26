using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;
using SportsPlatform.Auth.Core.Entities;
using SportsPlatform.Auth.Core.Interfaces;
using SportsPlatform.Auth.Infrastructure.Data;

namespace SportsPlatform.Auth.Infrastructure.Services;

public class MessagingService : IMessagingService
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _storage;
    public MessagingService(AppDbContext db, IFileStorageService storage) { _db = db; _storage = storage; }

    public async Task<ConversationDto> CreateConversationAsync(Guid callerUserId, CreateConversationRequest request)
    {
        if (!request.ParticipantUserIds.Contains(callerUserId))
            request.ParticipantUserIds.Add(callerUserId);

        if (request.ParticipantUserIds.Count < 2)
            throw new InvalidOperationException("A conversation requires at least 2 participants.");

        // For 1:1, check if conversation already exists
        if (!request.IsGroup && request.ParticipantUserIds.Count == 2)
        {
            var existing = await FindExisting1to1Async(request.ParticipantUserIds[0], request.ParticipantUserIds[1]);
            if (existing != null) return existing;
        }

        var now = DateTime.UtcNow;
        var conversation = new Conversation
        {
            ConversationId = Guid.NewGuid(),
            IsGroup = request.IsGroup,
            Name = request.Name?.Trim(),
            CreatedAt = now
        };

        _db.Conversations.Add(conversation);
        foreach (var uid in request.ParticipantUserIds.Distinct())
        {
            _db.ConversationParticipants.Add(new ConversationParticipant
            {
                ConversationId = conversation.ConversationId,
                UserId = uid,
                JoinedAt = now
            });
        }

        await _db.SaveChangesAsync();
        return await BuildConversationDtoAsync(conversation.ConversationId, callerUserId);
    }

    public async Task<List<ConversationDto>> GetConversationsAsync(Guid callerUserId)
    {
        var conversationIds = await _db.ConversationParticipants
            .Where(cp => cp.UserId == callerUserId)
            .Select(cp => cp.ConversationId)
            .ToListAsync();

        var result = new List<ConversationDto>();
        foreach (var cid in conversationIds)
            result.Add(await BuildConversationDtoAsync(cid, callerUserId));

        return result.OrderByDescending(c => c.LastMessage?.SentAt ?? c.CreatedAt).ToList();
    }

    public async Task<List<MessageDto>> GetMessagesAsync(Guid conversationId, Guid callerUserId, int page = 1, int pageSize = 50)
    {
        await EnsureParticipantAsync(conversationId, callerUserId);

        var messages = await _db.Messages
            .Include(m => m.Sender)
            .Include(m => m.Reactions).ThenInclude(r => r.User)
            .Include(m => m.ReadReceipts).ThenInclude(r => r.User)
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.SentAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var participantIds = await GetParticipantIdsAsync(conversationId);
        return messages.Select(m => MapToMessageDto(m, participantIds)).ToList();
    }

    public async Task<MessageDto> SendMessageAsync(Guid conversationId, Guid callerUserId, SendMessageRequest request)
    {
        await EnsureParticipantAsync(conversationId, callerUserId);

        var message = new Message
        {
            MessageId = Guid.NewGuid(),
            ConversationId = conversationId,
            SenderUserId = callerUserId,
            Content = request.Content.Trim(),
            SentAt = DateTime.UtcNow,
            IsRead = false,
            MessageType = request.MessageType ?? "text",
            MediaUrl = request.MediaUrl,
            MediaFileName = request.MediaFileName,
            LocationLatitude = request.LocationLatitude,
            LocationLongitude = request.LocationLongitude,
            LocationLabel = request.LocationLabel?.Trim()
        };

        _db.Messages.Add(message);
        await _db.SaveChangesAsync();

        var sender = await _db.Users.FirstAsync(u => u.UserId == callerUserId);
        message.Sender = sender;
        var participantIds = await GetParticipantIdsAsync(conversationId);
        return MapToMessageDto(message, participantIds);
    }

    public async Task MarkAsReadAsync(Guid conversationId, Guid callerUserId)
    {
        await EnsureParticipantAsync(conversationId, callerUserId);
        var participantIds = await GetParticipantIdsAsync(conversationId);
        var unread = await _db.Messages
            .Where(m => m.ConversationId == conversationId
                && m.SenderUserId != callerUserId
                && !m.IsRead
                && !_db.MessageReadReceipts.Any(r => r.MessageId == m.MessageId && r.UserId == callerUserId))
            .ToListAsync();

        var now = DateTime.UtcNow;
        foreach (var message in unread)
        {
            _db.MessageReadReceipts.Add(new MessageReadReceipt
            {
                MessageId = message.MessageId,
                UserId = callerUserId,
                ReadAt = now
            });
        }

        await _db.SaveChangesAsync();

        var messageIds = unread.Select(m => m.MessageId).ToHashSet();
        var receipts = await _db.MessageReadReceipts
            .Where(r => messageIds.Contains(r.MessageId))
            .Select(r => new { r.MessageId, r.UserId })
            .ToListAsync();
        var receiptsByMessage = receipts
            .GroupBy(r => r.MessageId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.UserId).ToHashSet());

        foreach (var message in unread)
        {
            var requiredReaders = participantIds.Where(id => id != message.SenderUserId);
            var readers = receiptsByMessage.TryGetValue(message.MessageId, out var value)
                ? value
                : new HashSet<Guid>();
            message.IsRead = requiredReaders.All(readers.Contains);
        }

        await _db.SaveChangesAsync();
    }

    private async Task<ConversationDto?> FindExisting1to1Async(Guid u1, Guid u2)
    {
        var shared = await _db.ConversationParticipants
            .Where(cp => cp.UserId == u1)
            .Select(cp => cp.ConversationId)
            .Intersect(_db.ConversationParticipants.Where(cp => cp.UserId == u2).Select(cp => cp.ConversationId))
            .ToListAsync();

        foreach (var cid in shared)
        {
            var conv = await _db.Conversations.FirstOrDefaultAsync(c => c.ConversationId == cid && !c.IsGroup);
            if (conv != null) return await BuildConversationDtoAsync(cid, u1);
        }
        return null;
    }

    private async Task<ConversationDto> BuildConversationDtoAsync(Guid conversationId, Guid callerUserId)
    {
        var conv = await _db.Conversations
            .Include(c => c.Participants).ThenInclude(p => p.User).ThenInclude(u => u.TeamMemberships).ThenInclude(tm => tm.Team)
            .FirstOrDefaultAsync(c => c.ConversationId == conversationId)
            ?? throw new InvalidOperationException("Conversation not found.");

        var lastMsg = await _db.Messages.Include(m => m.Sender)
            .Include(m => m.Reactions).ThenInclude(r => r.User)
            .Include(m => m.ReadReceipts).ThenInclude(r => r.User)
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.SentAt).FirstOrDefaultAsync();

        var unread = await _db.Messages.CountAsync(m =>
            m.ConversationId == conversationId
            && m.SenderUserId != callerUserId
            && !m.IsRead
            && !_db.MessageReadReceipts.Any(r => r.MessageId == m.MessageId && r.UserId == callerUserId));
        var participantIds = conv.Participants.Select(p => p.UserId).ToList();

        return new ConversationDto
        {
            ConversationId = conv.ConversationId,
            IsGroup = conv.IsGroup,
            Name = conv.Name,
            Participants = conv.Participants.Select(p => new ParticipantDto { UserId = p.UserId, Name = p.User.Name, ProfileImageUrl = p.User.ProfileImageUrl, TeamName = p.User.TeamMemberships.FirstOrDefault()?.Team?.TeamName }).ToList(),
            LastMessage = lastMsg == null ? null : MapToMessageDto(lastMsg, participantIds),
            UnreadCount = unread,
            CreatedAt = conv.CreatedAt
        };
    }

    public async Task<MessageDto> EditMessageAsync(Guid messageId, Guid callerUserId, EditMessageRequest request)
    {
        var message = await _db.Messages
            .Include(m => m.Sender)
            .Include(m => m.Reactions).ThenInclude(r => r.User)
            .Include(m => m.ReadReceipts).ThenInclude(r => r.User)
            .FirstOrDefaultAsync(m => m.MessageId == messageId)
            ?? throw new InvalidOperationException("Message not found.");

        if (message.SenderUserId != callerUserId)
            throw new UnauthorizedAccessException("You can only edit your own messages.");

        if ((DateTime.UtcNow - message.SentAt).TotalHours > 1)
            throw new InvalidOperationException("Messages can only be edited within 1 hour of sending.");

        message.Content = request.Content.Trim();
        message.EditedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var participantIds = await GetParticipantIdsAsync(message.ConversationId);
        return MapToMessageDto(message, participantIds);
    }

    public async Task DeleteMessageAsync(Guid messageId, Guid callerUserId)
    {
        var message = await _db.Messages.FirstOrDefaultAsync(m => m.MessageId == messageId)
            ?? throw new InvalidOperationException("Message not found.");

        if (message.SenderUserId != callerUserId)
            throw new UnauthorizedAccessException("You can only delete your own messages.");

        if ((DateTime.UtcNow - message.SentAt).TotalHours > 1)
            throw new InvalidOperationException("Messages can only be deleted within 1 hour of sending.");

        message.IsDeleted = true;
        message.Content = string.Empty;
        await _db.SaveChangesAsync();
    }

    public async Task<MessageReactionDto> AddReactionAsync(Guid messageId, Guid callerUserId, SendReactionRequest request)
    {
        var messageExists = await _db.Messages.AnyAsync(m => m.MessageId == messageId);
        if (!messageExists) throw new InvalidOperationException("Message not found.");

        var existing = await _db.MessageReactions
            .FirstOrDefaultAsync(r => r.MessageId == messageId && r.UserId == callerUserId && r.Emoji == request.Emoji);

        if (existing != null)
        {
            _db.MessageReactions.Remove(existing);
            await _db.SaveChangesAsync();
            var user = await _db.Users.FirstAsync(u => u.UserId == callerUserId);
            return new MessageReactionDto
            {
                ReactionId = existing.ReactionId,
                UserId = callerUserId,
                UserName = user.Name,
                Emoji = request.Emoji
            };
        }

        var reaction = new MessageReaction
        {
            ReactionId = Guid.NewGuid(),
            MessageId = messageId,
            UserId = callerUserId,
            Emoji = request.Emoji,
            CreatedAt = DateTime.UtcNow
        };

        _db.MessageReactions.Add(reaction);
        await _db.SaveChangesAsync();

        var sender = await _db.Users.FirstAsync(u => u.UserId == callerUserId);
        return new MessageReactionDto
        {
            ReactionId = reaction.ReactionId,
            UserId = callerUserId,
            UserName = sender.Name,
            Emoji = reaction.Emoji
        };
    }

    public async Task RemoveReactionAsync(Guid messageId, Guid callerUserId, string emoji)
    {
        var reaction = await _db.MessageReactions
            .FirstOrDefaultAsync(r => r.MessageId == messageId && r.UserId == callerUserId && r.Emoji == emoji);

        if (reaction != null)
        {
            _db.MessageReactions.Remove(reaction);
            await _db.SaveChangesAsync();
        }
    }

    public async Task<MessageDto> SendMediaMessageAsync(Guid conversationId, Guid callerUserId, Stream fileStream, string fileName, string contentType, long fileSizeBytes)
    {
        await EnsureParticipantAsync(conversationId, callerUserId);

        const long maxSize = 25 * 1024 * 1024; // 25 MB
        if (fileSizeBytes > maxSize)
            throw new InvalidOperationException("File size exceeds the 25 MB limit.");

        var mediaUrl = await _storage.SaveFileAsync(fileStream, fileName, "chat-media", contentType);
        var messageType = contentType.StartsWith("image/") ? "image"
            : contentType.StartsWith("video/") ? "video"
            : contentType.StartsWith("audio/") ? "audio"
            : "document";

        var message = new Message
        {
            MessageId = Guid.NewGuid(),
            ConversationId = conversationId,
            SenderUserId = callerUserId,
            Content = fileName,
            SentAt = DateTime.UtcNow,
            IsRead = false,
            MessageType = messageType,
            MediaUrl = mediaUrl,
            MediaFileName = fileName
        };

        _db.Messages.Add(message);
        await _db.SaveChangesAsync();

        var sender = await _db.Users.FirstAsync(u => u.UserId == callerUserId);
        message.Sender = sender;
        var participantIds = await GetParticipantIdsAsync(conversationId);
        return MapToMessageDto(message, participantIds);
    }

    private static MessageDto MapToMessageDto(Message m, IReadOnlyCollection<Guid>? participantUserIds = null)
    {
        var requiredReaderIds = participantUserIds?
            .Where(id => id != m.SenderUserId)
            .ToHashSet() ?? new HashSet<Guid>();
        var seenBy = m.ReadReceipts?
            .Where(r => r.UserId != m.SenderUserId
                && (requiredReaderIds.Count == 0 || requiredReaderIds.Contains(r.UserId)))
            .OrderBy(r => r.ReadAt)
            .Select(r => new MessageSeenByDto
            {
                UserId = r.UserId,
                UserName = r.User.Name,
                ProfileImageUrl = r.User.ProfileImageUrl,
                ReadAt = r.ReadAt
            })
            .ToList() ?? new List<MessageSeenByDto>();
        var requiredSeenCount = requiredReaderIds.Count;
        var seenByAll = requiredSeenCount > 0 && seenBy.Count >= requiredSeenCount;

        return new MessageDto
        {
            MessageId = m.MessageId,
            ConversationId = m.ConversationId,
            SenderUserId = m.SenderUserId,
            SenderName = m.Sender.Name,
            SenderProfileImageUrl = m.Sender.ProfileImageUrl,
            Content = m.Content,
            SentAt = m.SentAt,
            IsRead = seenByAll || m.IsRead,
            EditedAt = m.EditedAt,
            IsDeleted = m.IsDeleted,
            MessageType = m.MessageType,
            MediaUrl = m.MediaUrl,
            MediaFileName = m.MediaFileName,
            LocationLatitude = m.LocationLatitude,
            LocationLongitude = m.LocationLongitude,
            LocationLabel = m.LocationLabel,
            Reactions = m.Reactions?.Select(r => new MessageReactionDto
            {
                ReactionId = r.ReactionId,
                UserId = r.UserId,
                UserName = r.User.Name,
                Emoji = r.Emoji
            }).ToList() ?? new List<MessageReactionDto>(),
            SeenBy = seenBy,
            SeenByCount = seenBy.Count,
            RequiredSeenCount = requiredSeenCount,
            SeenByAll = seenByAll || m.IsRead
        };
    }

    private async Task<List<Guid>> GetParticipantIdsAsync(Guid conversationId)
    {
        return await _db.ConversationParticipants
            .Where(cp => cp.ConversationId == conversationId)
            .Select(cp => cp.UserId)
            .ToListAsync();
    }

    private async Task EnsureParticipantAsync(Guid conversationId, Guid userId)
    {
        var isParticipant = await _db.ConversationParticipants
            .AnyAsync(cp => cp.ConversationId == conversationId && cp.UserId == userId);
        if (!isParticipant)
            throw new UnauthorizedAccessException("You are not a participant in this conversation.");
    }
}
