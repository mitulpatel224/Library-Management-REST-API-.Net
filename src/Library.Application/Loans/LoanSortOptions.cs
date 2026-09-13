using System.Linq.Expressions;
using Library.Domain.Entities;

namespace Library.Application.Loans;

/// <summary>
/// The allowed sort fields for the loan listing, and the expression each maps to.
/// </summary>
/// <remarks>
/// The same whitelist pattern as <see cref="Books.BookSortOptions"/> and
/// <see cref="Members.MemberSortOptions"/>, for the same reason: a SQL parameter
/// can stand in for a value but never for a column name, so <c>ORDER BY @p</c> is
/// not valid SQL and no escaping makes interpolation safe. The caller's string is
/// used to <i>look up</i> a pre-built expression, never to build one.
/// </remarks>
public static class LoanSortOptions
{
    /// <summary>
    /// Due date ascending: the most overdue loan first, which is the order the
    /// chase list is worked in.
    /// </summary>
    public const string Default = "due";

    public static IReadOnlyDictionary<string, Expression<Func<Loan, object?>>> SortMap { get; } =
        new Dictionary<string, Expression<Func<Loan, object?>>>(StringComparer.OrdinalIgnoreCase)
        {
            ["due"] = loan => loan.DueAt,
            ["issued"] = loan => loan.IssuedAt,
            ["returned"] = loan => loan.ReturnedAt,
            ["member"] = loan => loan.Member.FullName,
            ["title"] = loan => loan.BookCopy.Book.Title,
            ["barcode"] = loan => loan.BookCopy.Barcode,
        };

    public static IReadOnlyCollection<string> AllowedFields { get; } = [.. SortMap.Keys];

    /// <summary>
    /// Resolves a caller-supplied sort key, falling back to the default when it is
    /// absent or not on the whitelist.
    /// </summary>
    public static Expression<Func<Loan, object?>> Resolve(string? sortBy)
    {
        if (!string.IsNullOrWhiteSpace(sortBy) &&
            SortMap.TryGetValue(sortBy.Trim(), out Expression<Func<Loan, object?>>? expression))
        {
            return expression;
        }

        return SortMap[Default];
    }

    public static bool IsAllowed(string? sortBy) =>
        string.IsNullOrWhiteSpace(sortBy) || SortMap.ContainsKey(sortBy.Trim());
}
