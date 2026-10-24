using Gamex.UserLibrary.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Gamex.UserLibrary.Infrastructure.Email;

/// <summary>
/// Placeholder until an email provider (e.g. Amazon SES) is chosen: logs the reset link.
/// The link grants account access, so it is logged at Debug level only.
/// </summary>
internal sealed partial class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendPasswordResetAsync(string email, string displayName, Uri resetUrl, CancellationToken cancellationToken)
    {
        LogResetRequested(logger, email);
        LogResetLink(logger, resetUrl);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Password reset requested for {Email} (email sending not configured)")]
    private static partial void LogResetRequested(ILogger logger, string email);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Password reset link: {ResetUrl}")]
    private static partial void LogResetLink(ILogger logger, Uri resetUrl);
}
