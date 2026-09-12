namespace Library.Application.Common.Abstractions;

/// <summary>
/// Supplies the current time.
/// </summary>
/// <remarks>
/// <para>
/// This interface exists because of one rule that runs through the whole
/// project: <b>no production code calls <c>DateTime.Now</c> or
/// <c>DateTimeOffset.UtcNow</c> directly.</b>
/// </para>
/// <para>
/// Overdue detection and fine calculation are pure functions of "what time is
/// it now" versus <c>Loan.DueAt</c>. If the current time is baked into the call
/// stack, the only way to test a 12-day-overdue loan is to wait twelve days, or
/// to back-date the loan and hope the arithmetic is symmetric. With the clock
/// injected, the test simply advances it - see <c>FakeClock</c> in
/// Library.UnitTests.
/// </para>
/// <para>
/// <see cref="DateTimeOffset"/> rather than <see cref="DateTime"/>: it carries an
/// explicit UTC offset, so a value can never be silently reinterpreted in another
/// time zone. Everything is stored in UTC and converted only for display.
/// </para>
/// <para>
/// .NET 8 introduced the abstract <c>TimeProvider</c> class, which serves the
/// same purpose. A narrow domain-owned interface is used here instead so the
/// Application layer depends on nothing it does not define itself - and because
/// a two-member interface is easier to substitute in a test than a class with a
/// timer factory on it.
/// </para>
/// </remarks>
public interface IClock
{
    /// <summary>Current instant in UTC.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Current date in UTC, for due-date and day-count arithmetic.</summary>
    DateOnly Today { get; }
}
