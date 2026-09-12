namespace Library.Domain.Entities;

/// <summary>
/// Join entity linking a <see cref="Book"/> to a <see cref="Genre"/>.
/// </summary>
/// <remarks>
/// Unlike <see cref="BookAuthor"/> this join carries no extra data, so EF Core
/// could have generated it implicitly. It is declared explicitly anyway, for
/// consistency with <see cref="BookAuthor"/> and because an explicit join type
/// can be queried directly - <c>BookGenres.Where(bg =&gt; bg.GenreId == id)</c> -
/// without loading either end of the relationship.
/// </remarks>
public sealed class BookGenre
{
    private BookGenre() { }

    private BookGenre(int bookId, int genreId)
    {
        BookId = bookId;
        GenreId = genreId;
    }

    public int BookId { get; private set; }

    public Book Book { get; private set; } = null!;

    public int GenreId { get; private set; }

    public Genre Genre { get; private set; } = null!;

    public static BookGenre Create(int bookId, int genreId) => new(bookId, genreId);
}
