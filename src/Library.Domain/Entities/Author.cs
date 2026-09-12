using Library.Domain.Common;
using Library.Domain.Exceptions;

namespace Library.Domain.Entities;

/// <summary>
/// A book's author. Many-to-many with <see cref="Book"/> via <see cref="BookAuthor"/>.
/// </summary>
/// <remarks>
/// The relationship is M:N because both directions are genuinely plural: an
/// author writes many books, and a book may have several authors. Flattening
/// this to <c>Book.AuthorName = "Gamma, Helm, Johnson, Vlissides"</c> breaks 1NF
/// - the column holds a list - and makes "find every book by Erich Gamma"
/// a <c>LIKE '%Gamma%'</c> scan that also matches an author named Gammage.
/// </remarks>
public sealed class Author : AuditableEntity
{
    private Author() { }

    private Author(string firstName, string lastName, DateOnly? dateOfBirth, string? biography)
    {
        FirstName = firstName;
        LastName = lastName;
        DateOfBirth = dateOfBirth;
        Biography = biography;
    }

    public string FirstName { get; private set; } = null!;

    public string LastName { get; private set; } = null!;

    public DateOnly? DateOfBirth { get; private set; }

    public string? Biography { get; private set; }

    /// <summary>
    /// Convenience display name.
    /// </summary>
    /// <remarks>
    /// Marked <c>[NotMapped]</c>-by-configuration rather than persisted: storing
    /// it would duplicate data already present in the two name columns, and the
    /// duplicate would drift the first time an author is renamed.
    /// </remarks>
    public string FullName => $"{FirstName} {LastName}".Trim();

    private readonly List<BookAuthor> _bookAuthors = [];
    public IReadOnlyCollection<BookAuthor> BookAuthors => _bookAuthors.AsReadOnly();

    public static Author Create(
        string firstName,
        string lastName,
        DateOnly? dateOfBirth = null,
        string? biography = null)
    {
        if (string.IsNullOrWhiteSpace(lastName))
        {
            throw new BusinessRuleViolationException(
                "author.last_name_required", "Author last name is required.");
        }

        return new Author(
            firstName?.Trim() ?? string.Empty,
            lastName.Trim(),
            dateOfBirth,
            biography?.Trim());
    }

    public void Update(string firstName, string lastName, DateOnly? dateOfBirth, string? biography)
    {
        if (string.IsNullOrWhiteSpace(lastName))
        {
            throw new BusinessRuleViolationException(
                "author.last_name_required", "Author last name is required.");
        }

        FirstName = firstName?.Trim() ?? string.Empty;
        LastName = lastName.Trim();
        DateOfBirth = dateOfBirth;
        Biography = biography?.Trim();
    }
}
