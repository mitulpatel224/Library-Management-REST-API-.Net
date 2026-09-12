using Library.Domain.Common;
using Library.Domain.Exceptions;

namespace Library.Domain.Entities;

/// <summary>
/// A shelving classification: Fiction, Reference, Children's, and so on.
/// </summary>
/// <remarks>
/// <para>
/// Self-referencing via <see cref="ParentCategoryId"/>, so "Fiction" can contain
/// "Science Fiction". This is the adjacency-list model: one nullable FK back to
/// the same table. It is cheap to write and to update, and the trade-off is that
/// "give me every descendant of Fiction" needs a recursive query rather than a
/// single index seek. For a library's category tree - shallow and rarely
/// changing - that is the right side of the trade.
/// </para>
/// <para>
/// A book has exactly one Category (1:N) but may have many Genres (M:N). That
/// asymmetry is deliberate and mirrors how a library actually works: a physical
/// book sits on exactly one shelf, but it can be described as both Mystery and
/// Historical.
/// </para>
/// </remarks>
public sealed class Category : AuditableEntity
{
    // EF Core needs a parameterless constructor to materialise entities. Keeping
    // it private means application code cannot bypass the factory below and
    // create a Category in an invalid state.
    private Category() { }

    private Category(string name, string? description, int? parentCategoryId)
    {
        Name = name;
        Description = description;
        ParentCategoryId = parentCategoryId;
    }

    public string Name { get; private set; } = null!;

    public string? Description { get; private set; }

    public int? ParentCategoryId { get; private set; }

    public Category? ParentCategory { get; private set; }

    private readonly List<Category> _children = [];
    public IReadOnlyCollection<Category> Children => _children.AsReadOnly();

    private readonly List<Book> _books = [];
    public IReadOnlyCollection<Book> Books => _books.AsReadOnly();

    /// <summary>Creates a category, rejecting a blank name.</summary>
    public static Category Create(string name, string? description = null, int? parentCategoryId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new BusinessRuleViolationException(
                "category.name_required", "Category name is required.");
        }

        return new Category(name.Trim(), description?.Trim(), parentCategoryId);
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new BusinessRuleViolationException(
                "category.name_required", "Category name is required.");
        }

        Name = name.Trim();
    }

    public void UpdateDescription(string? description) => Description = description?.Trim();

    /// <summary>
    /// Re-parents this category, refusing to make it its own parent.
    /// </summary>
    /// <remarks>
    /// This guards only the direct self-reference. A longer cycle (A -> B -> A)
    /// needs a walk up the ancestor chain, which the application service does
    /// because it is the layer that can load the ancestors.
    /// </remarks>
    public void MoveUnder(int? parentCategoryId)
    {
        if (parentCategoryId is not null && parentCategoryId == Id)
        {
            throw new BusinessRuleViolationException(
                "category.cannot_parent_itself", "A category cannot be its own parent.");
        }

        ParentCategoryId = parentCategoryId;
    }
}
