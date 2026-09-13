using Library.Application.Notifications;
using Microsoft.Extensions.Logging;

namespace Library.Infrastructure.Notifications;

/// <inheritdoc cref="INotificationService"/>
public sealed partial class NotificationService : INotificationService
{
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(ILogger<NotificationService> logger) => _logger = logger;

    /// <inheritdoc />
    public event EventHandler<LibraryNotification>? NotificationRaised;

    /// <summary>
    /// Raises the event, then logs. Subscribers run synchronously, inline.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The local copy is not a style tic.</b> <c>NotificationRaised</c> is a
    /// field under the event, and another thread can unsubscribe the last handler
    /// between the null check and the invoke — leaving a
    /// <c>NullReferenceException</c> that reproduces roughly never and only under
    /// load. Copying to a local makes the check and the call refer to the same
    /// delegate. <c>?.Invoke</c> compiles to exactly this and is the idiomatic
    /// form; it is written out here because the reason is the interesting part.
    /// </para>
    /// <para>
    /// <b>A throwing subscriber stops the rest.</b> Multicast invocation runs
    /// subscribers in order and does not catch, so subscriber two never runs if
    /// subscriber one throws — and the exception surfaces in the caller, which did
    /// not raise it. That is caught here so an advisory notification can never
    /// fail the operation that triggered it.
    /// </para>
    /// </remarks>
    public void Notify(LibraryNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        EventHandler<LibraryNotification>? handlers = NotificationRaised;

        if (handlers is not null)
        {
            try
            {
                handlers(this, notification);
            }
            catch (Exception ex)
            {
                LogSubscriberFailed(notification.Kind, ex);
            }
        }

        LogNotification(notification.Kind, notification.MembershipNumber, notification.Message);
    }

    [LoggerMessage(
        EventId = 6100,
        Level = LogLevel.Information,
        Message = "Notification {Kind} for {MembershipNumber}: {Message}")]
    private partial void LogNotification(string kind, string membershipNumber, string message);

    [LoggerMessage(
        EventId = 6101,
        Level = LogLevel.Warning,
        Message = "A notification subscriber threw while handling {Kind}. "
                  + "Remaining subscribers for this notification did not run.")]
    private partial void LogSubscriberFailed(string kind, Exception exception);
}
