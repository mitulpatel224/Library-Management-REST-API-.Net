using Library.Domain.Common;
using Library.Domain.Exceptions;

namespace Library.Domain.Entities;

/// <summary>
/// A publishing house. One publisher, many books.
/// </summary>
/// <remarks>
/// Extracting this from a <c>Book.PublisherName</c> string column is textbook
/// 3NF: the publisher's country depends on the publisher, not on the book, so
/// storing it against the book is a transitive dependency. Left flat, correcting
/// a misspelled publisher name means an UPDATE across every one of its books,
/// and any row missed becomes a second, phantom publisher in the filter list.
/// </remarks>
public sealed class Publisher : AuditableEntity
{
    private Publisher() { }

    private Publisher(string name, string? country, string? website)
    {
        Name = name;
        Country = country;
        Website = website;
    }

    public string Name { get; private set; } = null!;

    public string? Country { get; private set; }

    public string? Website { get; private set; }

    private readonly List<Book> _books = [];
    public IReadOnlyCollection<Book> Books => _books.AsReadOnly();

    public static Publisher Create(string name, string? country = null, string? website = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new BusinessRuleViolationException(
                "publisher.name_required", "Publisher name is required.");
        }

        return new Publisher(name.Trim(), country?.Trim(), website?.Trim());
    }

    public void Update(string name, string? country, string? website)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new BusinessRuleViolationException(
                "publisher.name_required", "Publisher name is required.");
        }

        Name = name.Trim();
        Country = country?.Trim();
        Website = website?.Trim();
    }
}
