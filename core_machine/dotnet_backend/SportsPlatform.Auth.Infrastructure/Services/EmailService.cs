using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Infrastructure.Services;

public class EmailService : IEmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration config, ILogger<EmailService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task SendInvitationEmailAsync(
        string recipientEmail,
        string invitationToken,
        string clubOrTeamName,
        string roleName,
        string inviterName)
    {
        var senderEmail = _config["Email:SenderEmail"];
        var senderName = _config["Email:SenderName"] ?? "Equipex";
        var password = _config["Email:Password"];

        if (string.IsNullOrWhiteSpace(senderEmail) || string.IsNullOrWhiteSpace(password))
        {
            _logger.LogError(
                "SMTP email configuration is missing. Sender configured: {HasSender}; password configured: {HasPassword}.",
                !string.IsNullOrWhiteSpace(senderEmail),
                !string.IsNullOrWhiteSpace(password));
            throw new InvalidOperationException("SMTP email sender configuration is missing.");
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(senderName, senderEmail));
        message.To.Add(MailboxAddress.Parse(recipientEmail));
        message.Subject = $"You're invited to join {clubOrTeamName} as {roleName}";
        message.Body = new TextPart("html")
        {
            Text = $"""
                <h2>Equipex Invitation</h2>
                <p>{inviterName} invited you to join <strong>{clubOrTeamName}</strong> as <strong>{roleName}</strong>.</p>
                <p>Click the link below to open the Equipex app and accept your invitation:</p>
                <p><a href="https://MahmoudAltalbawy.github.io/invite?token={invitationToken}" style="padding: 10px 20px; background-color: #4CAF50; color: white; text-decoration: none; border-radius: 5px; font-weight: bold;">Accept Invitation</a></p>
                <br>
                <p><small>If you do not have the Equipex app installed, please install it first, then click the button above again to accept.</small></p>
                """
        };

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(
            _config["Email:SmtpHost"] ?? "smtp.gmail.com",
            int.TryParse(_config["Email:SmtpPort"], out var port) ? port : 587,
            SecureSocketOptions.StartTls);
        await smtp.AuthenticateAsync(senderEmail, password);
        var smtpResponse = await smtp.SendAsync(message);
        await smtp.DisconnectAsync(true);

        _logger.LogInformation(
            "Invitation email sent to {RecipientEmail}. SMTP response: {SmtpResponse}",
            recipientEmail,
            smtpResponse);
    }

    public async Task SendNotificationEmailAsync(
        string recipientEmail,
        string subject,
        string body)
    {
        var senderEmail = _config["Email:SenderEmail"];
        var senderName = _config["Email:SenderName"] ?? "Equipex";
        var password = _config["Email:Password"];

        if (string.IsNullOrWhiteSpace(senderEmail) || string.IsNullOrWhiteSpace(password))
        {
            _logger.LogError(
                "SMTP email configuration is missing. Sender configured: {HasSender}; password configured: {HasPassword}.",
                !string.IsNullOrWhiteSpace(senderEmail),
                !string.IsNullOrWhiteSpace(password));
            throw new InvalidOperationException("SMTP email sender configuration is missing.");
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(senderName, senderEmail));
        message.To.Add(MailboxAddress.Parse(recipientEmail));
        message.Subject = subject;
        message.Body = new TextPart("html")
        {
            Text = $"""
                <h2>{System.Net.WebUtility.HtmlEncode(subject)}</h2>
                <p>{System.Net.WebUtility.HtmlEncode(body)}</p>
                <p>Open Equipex to review the latest team updates.</p>
                """
        };

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(
            _config["Email:SmtpHost"] ?? "smtp.gmail.com",
            int.TryParse(_config["Email:SmtpPort"], out var port) ? port : 587,
            SecureSocketOptions.StartTls);
        await smtp.AuthenticateAsync(senderEmail, password);
        var smtpResponse = await smtp.SendAsync(message);
        await smtp.DisconnectAsync(true);

        _logger.LogInformation(
            "Notification email sent to {RecipientEmail} with subject {Subject}. SMTP response: {SmtpResponse}",
            recipientEmail,
            subject,
            smtpResponse);
    }
}
