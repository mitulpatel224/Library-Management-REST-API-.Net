using Library.Application.Common.Abstractions;
using Library.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Library.Infrastructure.Persistence;

/// <summary>
/// Commits the current <see cref="LibraryDbContext"/> change set, translating
/// database constraint violations into domain exceptions.
/// </summary>
/// <remarks>
/// <para>
/// <b>This translation is the point of the class.</b> Without it a duplicate
/// ISBN surfaces as <c>DbUpdateException</c> — an EF Core type carrying a
/// provider-specific SQLite or SQL Server error inside it — and the API returns
/// 500 for something that is squarely the caller's problem.
/// </para>
/// <para>
/// <b>Why it matters more than tidiness.</b> Services check for a duplicate
/// before inserting, and that check produces a clear error message. But between
/// the check and the insert there is a window: two concurrent requests can both
/// see "no such ISBN" and both proceed. Only one can win the unique index. The
/// loser arrives here, and this is what turns its failure into a correct
/// <c>409 Conflict</c> rather than an unhandled crash.
/// </para>
/// <blockquote>
/// The pre-check is for the error message. The index is the guarantee. This
/// method is what makes the guarantee presentable.
/// </blockquote>
/// </remarks>
public sealed class UnitOfWork : IUnitOfWork
{
    private readonly LibraryDbContext _context;

    public UnitOfWork(LibraryDbContext context) => _context = context;

    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // The original exception is kept as the inner exception, so the log
            // retains the constraint name while the caller sees only a clean
            // domain error.
            throw new ConflictException(
                "resource.duplicate",
                "The value violates a uniqueness rule. It may already exist.",
                ex);
        }
    }

    /// <summary>
    /// Detects a unique-constraint violation across both supported providers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is one of the few places where provider differences genuinely leak,
    /// because EF Core does not normalise database error codes. The inner
    /// exception is <c>SqliteException</c> or <c>SqlException</c>, each with its
    /// own numbering:
    /// </para>
    /// <list type="bullet">
    ///   <item>SQLite: result code <b>19</b> (SQLITE_CONSTRAINT), extended
    ///     <b>2067</b> (UNIQUE) or <b>1555</b> (PRIMARY KEY).</item>
    ///   <item>SQL Server: error <b>2627</b> (unique constraint) or
    ///     <b>2601</b> (unique index).</item>
    /// </list>
    /// <para>
    /// Matching on the numbers rather than on message text is deliberate: error
    /// messages are localised, so a server running under a non-English locale
    /// would silently stop matching and every conflict would become a 500.
    /// </para>
    /// <para>
    /// The provider exception types are reached by name to avoid this project
    /// taking a compile-time dependency on both client libraries purely for a
    /// type check.
    /// </para>
    /// </remarks>
    private static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        Exception? inner = exception.InnerException;

        if (inner is null)
        {
            return false;
        }

        return inner.GetType().Name switch
        {
            "SqliteException" => GetIntProperty(inner, "SqliteErrorCode") == 19
                              || GetIntProperty(inner, "SqliteExtendedErrorCode") is 2067 or 1555,

            "SqlException" => GetIntProperty(inner, "Number") is 2627 or 2601,

            _ => false,
        };
    }

    private static int? GetIntProperty(Exception exception, string propertyName) =>
        exception.GetType().GetProperty(propertyName)?.GetValue(exception) as int?;
}
