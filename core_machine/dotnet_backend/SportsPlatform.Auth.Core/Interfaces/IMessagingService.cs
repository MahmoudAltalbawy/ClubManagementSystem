using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;

namespace SportsPlatform.Auth.Core.Interfaces;

public interface IMessagingService
{
    Task<ConversationDto> CreateConversationAsync(Guid callerUserId, CreateConversationRequest request);
    Task<List<ConversationDto>> GetConversationsAsync(Guid callerUserId);
    Task<List<MessageDto>> GetMessagesAsync(Guid conversationId, Guid callerUserId, int page = 1, int pageSize = 50);
    Task<MessageDto> SendMessageAsync(Guid conversationId, Guid callerUserId, SendMessageRequest request);
    Task MarkAsReadAsync(Guid conversationId, Guid callerUserId);
    Task<MessageDto> EditMessageAsync(Guid messageId, Guid callerUserId, EditMessageRequest request);
    Task DeleteMessageAsync(Guid messageId, Guid callerUserId);
    Task<MessageReactionDto> AddReactionAsync(Guid messageId, Guid callerUserId, SendReactionRequest request);
    Task RemoveReactionAsync(Guid messageId, Guid callerUserId, string emoji);
    Task<MessageDto> SendMediaMessageAsync(Guid conversationId, Guid callerUserId, Stream fileStream, string fileName, string contentType, long fileSizeBytes);
}
