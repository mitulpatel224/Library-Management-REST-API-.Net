using Library.Domain.Common;
using Library.Domain.Exceptions;

namespace Library.Domain.Entities;

/// <summary>
/// A descriptive genre: Mystery, Historical, Biography. Many-to-many with <see cref="Book"/>.
/// </summary>
/// <remarks>
/// <see cref="Slug"/> is a URL- and filter-friendly form of the name
/// ("Science Fiction" becomes "science-fiction"). It is stored rather than
/// computed on read so it can carry a unique index and be matched exactly -
/// filtering on a slug is an index seek, whereas a case-insensitive match on a
/// display name is not.
/// </remarks>
public sealed class Genre : AuditableEntity
{
    private Genre() { }

    private Genre(string name, string slug, string? description)
    {
        Name = name;
        Slug = slug;
        Description = description;
    }

    public string Name { get; private set; } = null!;

    public string Slug { get; private set; } = null!;

    public string? Description { get; private set; }

    private readonly List<BookGenre> _bookGenres = [];
    public IReadOnlyCollection<BookGenre> BookGenres => _bookGenres.AsReadOnly();

    public static Genre Create(string name, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new BusinessRuleViolationException(
                "genre.name_required", "Genre name is required.");
        }

        string trimmed = name.Trim();
        return new Genre(trimmed, ToSlug(trimmed), description?.Trim());
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new BusinessRuleViolationException(
                "genre.name_required", "Genre name is required.");
        }

        Name = name.Trim();
        Slug = ToSlug(Name);
    }

    public void UpdateDescription(string? description) => Description = description?.Trim();

    /// <summary>Lower-cases and hyphenates a display name.</summary>
    private static string ToSlug(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        int length = 0;
        bool lastWasHyphen = false;

        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer[length++] = char.ToLowerInvariant(c);
                lastWasHyphen = false;
            }
            else if (!lastWasHyphen && length > 0)
            {
                // Collapse any run of separators into a single hyphen.
                buffer[length++] = '-';
                lastWasHyphen = true;
            }
        }

        // Drop a trailing hyphen left by a name ending in punctuation.
        if (length > 0 && buffer[length - 1] == '-')
        {
            length--;
        }

        return new string(buffer[..length]);
    }
}
