namespace Library.Domain.Exceptions;

/// <summary>
/// The request is well formed but conflicts with the current state of the
/// resource. Maps to <b>409 Conflict</b>.
/// </summary>
/// <remarks>
/// <para>
/// This is the exception behind the system's headline rule: a copy already on
/// loan cannot be issued again. It is raised from two places, and both matter:
/// </para>
/// <list type="number">
///   <item>
///     the service checks for an active loan first, so the normal path returns a
///     clear message without hitting a constraint violation;
///   </item>
///   <item>
///     the <c>SaveChanges</c> path catches the unique-index violation from the
///     database and translates it here, which is what actually makes the rule
///     safe under concurrency. Two simultaneous issue requests both pass the
///     check in (1); only one can win the index in (2).
///   </item>
/// </list>
/// <para>
/// Checking first is for the error message. The index is for correctness.
/// </para>
/// </remarks>
public sealed class ConflictException : DomainException
{
    public ConflictException(string errorCode, string message)
        : base(errorCode, message) { }

    public ConflictException(string errorCode, string message, Exception innerException)
        : base(errorCode, message, innerException) { }
}
