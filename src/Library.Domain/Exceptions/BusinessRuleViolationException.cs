namespace Library.Domain.Exceptions;

/// <summary>
/// The request is syntactically valid and the resource exists, but performing it
/// would break a business rule. Maps to <b>422 Unprocessable Entity</b>.
/// </summary>
/// <remarks>
/// <para>
/// The three-way split this codebase uses, and why:
/// </para>
/// <list type="bullet">
///   <item><b>400</b> - the request is malformed (a string where an int belongs).
///         Caught by model binding; never reaches the domain.</item>
///   <item><b>422</b> - the request is well formed but the rules say no
///         ("this member already has 5 books out"). <i>This</i> exception.</item>
///   <item><b>409</b> - the request collides with current state
///         ("that copy is already on loan"). <see cref="ConflictException"/>.</item>
/// </list>
/// <para>
/// 409 versus 422 is a genuinely debatable line. The rule applied here: if the
/// caller could succeed by retrying later without changing the request, it is a
/// conflict; if the request itself is the problem, it is unprocessable.
/// </para>
/// </remarks>
public sealed class BusinessRuleViolationException : DomainException
{
    public BusinessRuleViolationException(string errorCode, string message)
        : base(errorCode, message) { }
}
