namespace Library.Domain.Entities;

/// <summary>
/// Join entity linking a <see cref="Book"/> to an <see cref="Author"/>.
/// </summary>
/// <remarks>
/// <para>
/// EF Core can manage a many-to-many relationship without an explicit join type,
/// generating the table itself. This one is declared explicitly because it
/// carries data of its own: <see cref="AuthorOrder"/> and <see cref="Role"/>.
/// The moment a relationship has attributes, it is an entity in its own right.
/// </para>
/// <para>
/// <see cref="AuthorOrder"/> matters more than it looks. Authorship order is
/// meaningful - "Gamma, Helm, Johnson, Vlissides" is not the same credit as any
/// other permutation - and a plain join table has no inherent ordering, so the
/// database is free to return rows however it likes.
/// </para>
/// <para>
/// The primary key is the composite (BookId, AuthorId). That composite is what
/// makes it impossible to record the same author on the same book twice.
/// </para>
/// </remarks>
public sealed class BookAuthor
{
    private BookAuthor() { }

    private BookAuthor(int bookId, int authorId, int authorOrder, string? role)
    {
        BookId = bookId;
        AuthorId = authorId;
        AuthorOrder = authorOrder;
        Role = role;
    }

    public int BookId { get; private set; }

    public Book Book { get; private set; } = null!;

    public int AuthorId { get; private set; }

    public Author Author { get; private set; } = null!;

    /// <summary>1-based position in the credit list.</summary>
    public int AuthorOrder { get; private set; }

    /// <summary>Optional contribution type: Editor, Translator, Illustrator.</summary>
    public string? Role { get; private set; }

    public static BookAuthor Create(int bookId, int authorId, int authorOrder = 1, string? role = null)
        => new(bookId, authorId, authorOrder, role?.Trim());

    internal void SetOrder(int authorOrder) => AuthorOrder = authorOrder;
}
