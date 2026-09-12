namespace Library.Application.Common.Abstractions;

/// <summary>
/// Commits the changes made during one use case.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists when `DbContext` already is a unit of work.</b> It is not
/// adding the pattern — EF Core supplies it. It is exposing it to the
/// Application layer *without* exposing EF Core, which is the same reason
/// <see cref="IClock"/> exists rather than a direct call to
/// <c>DateTimeOffset.UtcNow</c>.
/// </para>
/// <para>
/// <b>What it buys concretely.</b> A service can load a book, mutate it, add a
/// copy, and commit all of it in one transaction — without knowing that a
/// transaction, or EF Core, is involved:
/// </para>
/// <code>
/// Book book = await _repository.GetEntityAsync(id, ct) ?? throw new NotFoundException(...);
/// book.UpdateDetails(...);
/// book.SetAuthors(request.AuthorIds);
/// await _unitOfWork.SaveChangesAsync(ct);      // one transaction
/// </code>
/// <para>
/// Calling <c>SaveChanges</c> inside each repository method would instead
/// produce three separate transactions, and a failure partway through would
/// leave the book half-updated.
/// </para>
/// <para>
/// It also keeps the decision of *when* to commit with the layer that
/// understands the use case, rather than with the layer that understands storage.
/// </para>
/// </remarks>
public interface IUnitOfWork
{
    /// <summary>
    /// Persists every change tracked since the last commit, as one transaction.
    /// </summary>
    /// <returns>The number of state entries written.</returns>
    /// <exception cref="Domain.Exceptions.ConflictException">
    /// A unique constraint was violated — for example two requests racing to
    /// create the same ISBN. Translated from the provider's
    /// <c>DbUpdateException</c> so callers never see an EF Core type.
    /// </exception>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
