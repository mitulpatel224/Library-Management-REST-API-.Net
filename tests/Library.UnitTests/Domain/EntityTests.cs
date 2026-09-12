using Library.Domain.Common;

namespace Library.UnitTests.Domain;

/// <summary>
/// Verifies identity semantics on <see cref="Entity"/>.
/// </summary>
/// <remarks>
/// These look like tests of trivia, but every one of them prevents a specific
/// bug: silently de-duplicating two unsaved entities in a <c>HashSet</c>,
/// treating a Book and a Member with the same id as equal, or losing an entity
/// from a dictionary because its hash changed when the database assigned its key.
/// </remarks>
public sealed class EntityTests
{
    private sealed class TestBook : Entity
    {
        public static TestBook WithId(int id) => new() { Id = id };

        // Shadows the protected setter so tests can assign an id directly,
        // standing in for what the database does on insert.
        public new int Id
        {
            get => base.Id;
            init => base.Id = value;
        }
    }

    private sealed class TestMember : Entity
    {
        public static TestMember WithId(int id) => new() { Id = id };

        public new int Id
        {
            get => base.Id;
            init => base.Id = value;
        }
    }

    [Fact]
    public void Entities_of_the_same_type_with_the_same_id_are_equal()
    {
        TestBook left = TestBook.WithId(7);
        TestBook right = TestBook.WithId(7);

        left.ShouldBe(right);
        (left == right).ShouldBeTrue();
        left.GetHashCode().ShouldBe(right.GetHashCode());
    }

    [Fact]
    public void Entities_of_different_types_are_never_equal_even_with_the_same_id()
    {
        // Book 7 is not Member 7. Without the GetType() check in Entity.Equals,
        // a mixed collection would silently treat them as the same object.
        Entity book = TestBook.WithId(7);
        Entity member = TestMember.WithId(7);

        book.ShouldNotBe(member);
    }

    [Fact]
    public void Transient_entities_are_equal_only_to_themselves()
    {
        // Two unsaved entities both have Id == 0. If Id alone decided equality,
        // adding three new books to a HashSet would keep exactly one of them.
        TestBook first = new();
        TestBook second = new();

        first.IsTransient.ShouldBeTrue();
        second.IsTransient.ShouldBeTrue();

        first.ShouldNotBe(second);
        first.ShouldBe(first);

        new HashSet<TestBook> { first, second }.Count.ShouldBe(2);
    }

    [Fact]
    public void Comparing_with_null_is_false_and_does_not_throw()
    {
        TestBook book = TestBook.WithId(1);

        (book == null).ShouldBeFalse();
        (book != null).ShouldBeTrue();
        book.Equals(null).ShouldBeFalse();
    }

    [Fact]
    public void Two_nulls_are_equal()
    {
        TestBook? left = null;
        TestBook? right = null;

        (left == right).ShouldBeTrue();
    }
}
