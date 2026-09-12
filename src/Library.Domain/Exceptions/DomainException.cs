namespace Library.Domain.Exceptions;

/// <summary>
/// Base type for every exception the domain raises deliberately.
/// </summary>
/// <remarks>
/// <para>
/// The distinction that matters: a <see cref="DomainException"/> means
/// <i>"the caller asked for something the rules forbid"</i>. Any other exception
/// means <i>"the system is broken"</i>. The first is a 4xx and is safe to show
/// the client; the second is a 500 and must never leak its detail outward.
/// One global handler in the API layer makes exactly that split, which is why
/// no controller in this codebase contains a try/catch.
/// </para>
/// <para>
/// <see cref="ErrorCode"/> is a stable, machine-readable string
/// (<c>book.not_found</c>, <c>loan.copy_already_on_loan</c>). Clients branch on
/// it; humans read <see cref="Exception.Message"/>. Without it, callers end up
/// string-matching on prose, which breaks the moment you reword a message.
/// </para>
/// </remarks>
public abstract class DomainException : Exception
{
    protected DomainException(string errorCode, string message) : base(message)
        => ErrorCode = errorCode;

    protected DomainException(string errorCode, string message, Exception innerException)
        : base(message, innerException)
        => ErrorCode = errorCode;

    /// <summary>Stable machine-readable identifier for this failure.</summary>
    public string ErrorCode { get; }
}
