using Library.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Library.Infrastructure.Persistence.Configurations;

public sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name).HasMaxLength(100).IsRequired();
        builder.Property(c => c.Description).HasMaxLength(500);

        builder.HasIndex(c => c.Name)
            .IsUnique()
            .HasDatabaseName("IX_Categories_Name");

        // Self-reference (adjacency list). Restrict so deleting "Fiction" cannot
        // silently take "Science Fiction" - and everything filed under it - with it.
        builder.HasOne(c => c.ParentCategory)
            .WithMany(c => c.Children)
            .HasForeignKey(c => c.ParentCategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Metadata.FindNavigation(nameof(Category.Children))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
        builder.Metadata.FindNavigation(nameof(Category.Books))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class PublisherConfiguration : IEntityTypeConfiguration<Publisher>
{
    public void Configure(EntityTypeBuilder<Publisher> builder)
    {
        builder.ToTable("Publishers");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Name).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Country).HasMaxLength(100);
        builder.Property(p => p.Website).HasMaxLength(500);

        builder.HasIndex(p => p.Name)
            .IsUnique()
            .HasDatabaseName("IX_Publishers_Name");

        builder.Metadata.FindNavigation(nameof(Publisher.Books))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class AuthorConfiguration : IEntityTypeConfiguration<Author>
{
    public void Configure(EntityTypeBuilder<Author> builder)
    {
        builder.ToTable("Authors");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.FirstName).HasMaxLength(100).IsRequired();
        builder.Property(a => a.LastName).HasMaxLength(100).IsRequired();
        builder.Property(a => a.Biography).HasMaxLength(4000);

        // Author names are NOT unique - two different people genuinely share a
        // name, and collapsing them would merge their bibliographies. The index
        // is non-unique and exists purely to speed up author search.
        builder.HasIndex(a => new { a.LastName, a.FirstName })
            .HasDatabaseName("IX_Authors_LastName_FirstName");

        // Derived from the two name columns; persisting it would duplicate data
        // that drifts the moment an author is renamed.
        builder.Ignore(a => a.FullName);

        builder.Metadata.FindNavigation(nameof(Author.BookAuthors))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class GenreConfiguration : IEntityTypeConfiguration<Genre>
{
    public void Configure(EntityTypeBuilder<Genre> builder)
    {
        builder.ToTable("Genres");
        builder.HasKey(g => g.Id);

        builder.Property(g => g.Name).HasMaxLength(100).IsRequired();
        builder.Property(g => g.Slug).HasMaxLength(120).IsRequired();
        builder.Property(g => g.Description).HasMaxLength(500);

        builder.HasIndex(g => g.Name).IsUnique().HasDatabaseName("IX_Genres_Name");
        builder.HasIndex(g => g.Slug).IsUnique().HasDatabaseName("IX_Genres_Slug");

        builder.Metadata.FindNavigation(nameof(Genre.BookGenres))!
            .SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

/// <summary>
/// The Book-Author join. Composite primary key, which is what makes it
/// impossible to credit the same author twice on one book.
/// </summary>
public sealed class BookAuthorConfiguration : IEntityTypeConfiguration<BookAuthor>
{
    public void Configure(EntityTypeBuilder<BookAuthor> builder)
    {
        builder.ToTable("BookAuthors");

        // Composite key: the pair IS the identity. No surrogate Id is needed,
        // and adding one would allow duplicate pairs.
        builder.HasKey(ba => new { ba.BookId, ba.AuthorId });

        builder.Property(ba => ba.Role).HasMaxLength(50);

        builder.HasOne(ba => ba.Book)
            .WithMany(b => b.BookAuthors)
            .HasForeignKey(ba => ba.BookId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(ba => ba.Author)
            .WithMany(a => a.BookAuthors)
            .HasForeignKey(ba => ba.AuthorId)
            .OnDelete(DeleteBehavior.Cascade);

        // The PK covers BookId-first lookups. This index covers the other
        // direction: "every book by author X", which the search endpoint needs.
        builder.HasIndex(ba => ba.AuthorId)
            .HasDatabaseName("IX_BookAuthors_AuthorId");
    }
}

public sealed class BookGenreConfiguration : IEntityTypeConfiguration<BookGenre>
{
    public void Configure(EntityTypeBuilder<BookGenre> builder)
    {
        builder.ToTable("BookGenres");

        builder.HasKey(bg => new { bg.BookId, bg.GenreId });

        builder.HasOne(bg => bg.Book)
            .WithMany(b => b.BookGenres)
            .HasForeignKey(bg => bg.BookId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(bg => bg.Genre)
            .WithMany(g => g.BookGenres)
            .HasForeignKey(bg => bg.GenreId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(bg => bg.GenreId)
            .HasDatabaseName("IX_BookGenres_GenreId");
    }
}
