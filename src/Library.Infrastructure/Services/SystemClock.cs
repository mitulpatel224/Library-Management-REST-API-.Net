using Library.Application.Common.Abstractions;

namespace Library.Infrastructure.Services;

/// <summary>
/// The real clock. Registered as a singleton - it holds no state.
/// </summary>
/// <remarks>
/// This class is the <i>only</i> place in production code permitted to read the
/// system time. Everything else takes <see cref="IClock"/>, which is what makes
/// overdue and fine logic testable without waiting for real days to pass.
/// </remarks>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);
}
