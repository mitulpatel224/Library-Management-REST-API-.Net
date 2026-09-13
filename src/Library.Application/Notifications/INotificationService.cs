namespace Library.Application.Notifications;

/// <summary>What happened, in terms a notification can be written from.</summary>
public sealed record LibraryNotification(
    string Kind,
    string MembershipNumber,
    string Message,
    DateTimeOffset RaisedAt);

/// <summary>
/// In-process notification, using the C# <c>event</c> keyword.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists to contrast with domain events, not to duplicate them.</b> The
/// two mechanisms look similar and solve genuinely different problems, and
/// choosing the wrong one is a real bug rather than a style preference:
/// </para>
/// <list type="table">
///   <listheader>
///     <term>Aspect</term><description>C# <c>event</c> vs domain event</description>
///   </listheader>
///   <item>
///     <term>When it runs</term>
///     <description><b>Immediately</b>, inline on the raising thread, versus
///       <b>after the transaction commits</b>.</description>
///   </item>
///   <item>
///     <term>If the transaction rolls back</term>
///     <description>The C# event <b>already fired</b> and cannot be recalled; the
///       domain event is simply never dispatched.</description>
///   </item>
///   <item>
///     <term>Who knows about whom</term>
///     <description>Subscribers attach at runtime to a concrete instance; domain
///       event handlers are resolved from DI by type.</description>
///   </item>
///   <item>
///     <term>Failure</term>
///     <description>A throwing subscriber propagates into the raiser; a failing
///       domain event handler is logged and isolated.</description>
///   </item>
/// </list>
/// <para>
/// <b>Why an event is right <i>here</i>.</b> A notification is advisory. Nothing
/// downstream depends on it, a missed one harms nobody, and firing it a moment
/// early costs nothing because it writes no data. Assessing a fine is the
/// opposite on every count — it creates a financial record — which is why that
/// one is a domain event dispatched only once the return is durable.
/// </para>
/// <para>
/// <b>The trap this shape avoids.</b> A public <c>event</c> field can be
/// overwritten with <c>=</c> by any caller, silently detaching every other
/// subscriber. Exposing it as an event (not a delegate field) restricts callers
/// to <c>+=</c> and <c>-=</c>.
/// </para>
/// </remarks>
public interface INotificationService
{
    /// <summary>Raised as soon as something notification-worthy happens.</summary>
    event EventHandler<LibraryNotification>? NotificationRaised;

    void Notify(LibraryNotification notification);
}
