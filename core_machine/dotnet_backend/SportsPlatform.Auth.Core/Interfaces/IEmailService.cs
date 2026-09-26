namespace SportsPlatform.Auth.Core.Interfaces;

public interface IEmailService
{
    Task SendInvitationEmailAsync(
        string recipientEmail,
        string invitationToken,
        string clubOrTeamName,
        string roleName,
        string inviterName);

    Task SendNotificationEmailAsync(
        string recipientEmail,
        string subject,
        string body);
}
