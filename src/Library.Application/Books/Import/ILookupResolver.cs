namespace Library.Application.Books.Import;

/// <summary>
/// Turns the lookup <i>names</i> in an import file into the ids the catalogue uses,
/// creating any that do not yet exist.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why creating is the default rather than an error.</b> A supplier's file
/// says "Penguin Books" and "Science Fiction". It cannot know this database's
/// ids, and it has no way to pre-register anything. An importer that rejected
/// every row whose publisher was not already on file would fail the entire first
/// import of any new supplier — which is precisely when the feature is needed.
/// </para>
/// <para>
/// The cost is real and worth naming: a typo creates a lookup row. "Pengiun
/// Books" becomes a publisher, and nothing flags it. The mitigations are that
/// names are trimmed and matched case-insensitively, that the result reports how
/// many lookups were created so an unexpectedly large number is visible, and
/// that merging duplicates afterwards is a tractable admin job. Blocking the
/// import instead would not prevent bad data, only delay it.
/// </para>
/// <para>
/// <b>Why an interface in the Application layer.</b> The resolution rules are a
/// use-case concern — what counts as "the same publisher" is a business
/// question. Where the rows live is not, so the implementation sits in
/// Infrastructure.
/// </para>
/// </remarks>
public interface ILookupResolver
{
    /// <summary>
    /// Resolves a category name to its id, creating the category if absent.
    /// </summary>
    /// <remarks>
    /// Matching is case-insensitive on the trimmed name, so "science fiction"
    /// and "Science Fiction " resolve to the same row rather than creating a
    /// second one that differs only in presentation.
    /// </remarks>
    Task<int> ResolveCategoryAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Resolves a publisher name to its id, creating it if absent. Null passes through.</summary>
    Task<int?> ResolvePublisherAsync(string? name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves author names to ids, in order, creating any that are absent.
    /// </summary>
    /// <remarks>
    /// Order is preserved because it becomes <c>BookAuthor.AuthorOrder</c> —
    /// authorship order is meaningful data, not presentation.
    /// <para>
    /// A name is split on the last space: "Erich Gamma" becomes first "Erich",
    /// last "Gamma". That is wrong for "Ursula K. Le Guin" and for cultures
    /// where the family name comes first. A supplier feed carrying separate
    /// name fields would be better, but most do not — so this is the pragmatic
    /// reading, and the resulting row is editable afterwards.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<int>> ResolveAuthorsAsync(
        IReadOnlyList<string> names,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves genre names to ids, creating any that are absent.</summary>
    Task<IReadOnlyList<int>> ResolveGenresAsync(
        IReadOnlyList<string> names,
        CancellationToken cancellationToken = default);

    /// <summary>How many lookup rows this resolver has created since it was constructed.</summary>
    /// <remarks>
    /// Reported back in <see cref="BookImportResult.LookupsCreated"/>. A number
    /// far larger than expected is the signal that a file is full of typos or
    /// that the wrong column was mapped.
    /// </remarks>
    int CreatedCount { get; }
}
