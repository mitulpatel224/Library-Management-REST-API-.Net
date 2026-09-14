using Library.Application.Books.Import;
using Library.Domain.Entities;
using Library.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Library.Infrastructure.Import;

/// <summary>
/// Resolves lookup names to ids during an import, creating rows that do not exist.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scoped, and stateful within that scope.</b> An import resolves the same
/// publisher hundreds of times — a supplier feed is mostly one publisher — so
/// every name resolved is cached in memory for the rest of the request. Without
/// the cache a 10,000-row file issues 10,000 near-identical lookups.
/// </para>
/// <para>
/// The cache is also what makes creation safe within a single file. A new
/// publisher appearing on rows 5 and 6 is created once: row 5 stages the insert
/// and caches the instance, and row 6 finds it there rather than querying a
/// database that has not committed yet and staging a second copy.
/// </para>
/// </remarks>
public sealed class LookupResolver : ILookupResolver
{
    private readonly LibraryDbContext _context;

    // Keyed case-insensitively, so "Penguin Books" and "penguin books" resolve to
    // one row rather than creating a second that differs only in presentation.
    private readonly Dictionary<string, Category> _categories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Publisher> _publishers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Author> _authors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Genre> _genres = new(StringComparer.OrdinalIgnoreCase);

    public LookupResolver(LibraryDbContext context) => _context = context;

    public int CreatedCount { get; private set; }

    public async Task<int> ResolveCategoryAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        string key = (name ?? string.Empty).Trim();

        if (_categories.TryGetValue(key, out Category? cached))
        {
            return cached.Id;
        }

        Category? existing = await _context.Categories
            .FirstOrDefaultAsync(c => c.Name == key, cancellationToken);

        if (existing is null)
        {
            existing = Category.Create(key);
            _context.Categories.Add(existing);

            // Saved immediately rather than with the batch, because the Book
            // being built needs this row's key NOW - a foreign key cannot point
            // at an id the database has not issued.
            await _context.SaveChangesAsync(cancellationToken);
            CreatedCount++;
        }

        _categories[key] = existing;
        return existing.Id;
    }

    public async Task<int?> ResolvePublisherAsync(
        string? name,
        CancellationToken cancellationToken = default)
    {
        // Publisher is optional on a Book, so a blank column is "not supplied"
        // rather than a publisher named "".
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string key = name.Trim();

        if (_publishers.TryGetValue(key, out Publisher? cached))
        {
            return cached.Id;
        }

        Publisher? existing = await _context.Publishers
            .FirstOrDefaultAsync(p => p.Name == key, cancellationToken);

        if (existing is null)
        {
            existing = Publisher.Create(key);
            _context.Publishers.Add(existing);
            await _context.SaveChangesAsync(cancellationToken);
            CreatedCount++;
        }

        _publishers[key] = existing;
        return existing.Id;
    }

    public async Task<IReadOnlyList<int>> ResolveAuthorsAsync(
        IReadOnlyList<string> names,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);

        List<int> ids = [];

        foreach (string raw in names)
        {
            string key = raw.Trim();

            if (key.Length == 0)
            {
                continue;
            }

            if (_authors.TryGetValue(key, out Author? cached))
            {
                ids.Add(cached.Id);
                continue;
            }

            (string firstName, string lastName) = SplitName(key);

            Author? existing = await _context.Authors
                .FirstOrDefaultAsync(
                    a => a.FirstName == firstName && a.LastName == lastName, cancellationToken);

            if (existing is null)
            {
                existing = Author.Create(firstName, lastName);
                _context.Authors.Add(existing);
                await _context.SaveChangesAsync(cancellationToken);
                CreatedCount++;
            }

            _authors[key] = existing;
            ids.Add(existing.Id);
        }

        // Distinct, because Book.SetAuthors rejects the same author twice - and a
        // file listing "Martin; Martin" should not fail the row over a typo we
        // can resolve. Order is preserved, so credit order survives.
        return [.. ids.Distinct()];
    }

    public async Task<IReadOnlyList<int>> ResolveGenresAsync(
        IReadOnlyList<string> names,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);

        List<int> ids = [];

        foreach (string raw in names)
        {
            string key = raw.Trim();

            if (key.Length == 0)
            {
                continue;
            }

            if (_genres.TryGetValue(key, out Genre? cached))
            {
                ids.Add(cached.Id);
                continue;
            }

            Genre? existing = await _context.Genres
                .FirstOrDefaultAsync(g => g.Name == key, cancellationToken);

            if (existing is null)
            {
                existing = Genre.Create(key);
                _context.Genres.Add(existing);
                await _context.SaveChangesAsync(cancellationToken);
                CreatedCount++;
            }

            _genres[key] = existing;
            ids.Add(existing.Id);
        }

        return [.. ids.Distinct()];
    }

    /// <summary>
    /// Splits a single name field into first and last names on the LAST space.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Erich Gamma" gives ("Erich", "Gamma"); "Ursula K. Le Guin" gives
    /// ("Ursula K. Le", "Guin"), which is wrong — "Le Guin" is the family name.
    /// </para>
    /// <para>
    /// There is no heuristic that gets this right in general. Name order varies
    /// by culture, particles like "van" and "Le" belong to the surname in some
    /// traditions and not others, and a single field simply does not carry the
    /// distinction. The honest fix is a feed with separate name columns.
    /// </para>
    /// <para>
    /// So this takes the reading that is right most often for the files this
    /// system receives, and the resulting author row stays editable afterwards.
    /// A mononym — "Aristotle" — becomes a last name with no first name, which
    /// <c>Author.Create</c> permits.
    /// </para>
    /// </remarks>
    private static (string FirstName, string LastName) SplitName(string fullName)
    {
        int lastSpace = fullName.LastIndexOf(' ');

        return lastSpace <= 0
            ? (string.Empty, fullName)
            : (fullName[..lastSpace].Trim(), fullName[(lastSpace + 1)..].Trim());
    }
}
