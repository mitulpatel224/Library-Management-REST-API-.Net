using Library.Domain.Exceptions;
using Library.Domain.ValueObjects;

namespace Library.UnitTests.Domain;

/// <summary>
/// The value objects, whose job is to <b>normalise</b> as much as to validate.
/// </summary>
/// <remarks>
/// Normalisation is what makes <c>IX_Members_Email UNIQUE</c> mean what it
/// appears to mean. Stored as typed, the index would happily accept
/// <c>A@example.com</c> and <c>a@example.com</c> as two members — who then
/// diverge. These tests pin that behaviour rather than the regex.
/// </remarks>
public sealed class EmailTests
{
    [Theory]
    [InlineData("Asha.Nair@Example.COM", "asha.nair@example.com")]
    [InlineData("  spaced@example.com  ", "spaced@example.com")]
    [InlineData("MIXED@EXAMPLE.ORG", "mixed@example.org")]
    public void An_address_is_trimmed_and_lower_cased(string input, string expected)
    {
        Email.Create(input).Value.ShouldBe(expected);
    }

    [Fact]
    public void Two_addresses_differing_only_in_case_are_equal()
    {
        // The whole point: this is what the unique index relies on.
        Email.Create("Asha@Example.com").ShouldBe(Email.Create("asha@example.com"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("notanemail")]
    [InlineData("no@domain")]
    [InlineData("has space@example.com")]
    [InlineData("@example.com")]
    public void A_malformed_address_is_rejected(string input)
    {
        Email.TryCreate(input, out _, out string? error).ShouldBeFalse();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void An_over_long_address_is_rejected()
    {
        string tooLong = new string('x', Email.MaxLength) + "@example.com";

        Email.TryCreate(tooLong, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void Create_throws_with_a_stable_error_code()
    {
        Action act = () => Email.Create("nonsense");

        act.ShouldThrow<BusinessRuleViolationException>()
            .ErrorCode.ShouldBe("member.invalid_email");
    }
}

/// <summary>
/// Phone numbers, where normalisation stops formatting differences becoming data
/// differences.
/// </summary>
public sealed class PhoneNumberTests
{
    [Theory]
    [InlineData("+91 98250 12345", "+919825012345")]
    [InlineData("(079) 2630-1234", "07926301234")]
    [InlineData("079.2630.1234", "07926301234")]
    public void Formatting_is_stripped(string input, string expected)
    {
        PhoneNumber.Create(input).Value.ShouldBe(expected);
    }

    [Fact]
    public void Two_numbers_differing_only_in_formatting_are_equal()
    {
        PhoneNumber.Create("+91 98250 12345")
            .ShouldBe(PhoneNumber.Create("+919825012345"));
    }

    [Theory]
    [InlineData("not-a-phone")]
    [InlineData("12")]
    [InlineData("abcdefghij")]
    public void A_malformed_number_is_rejected(string input)
    {
        PhoneNumber.TryCreate(input, out _, out _).ShouldBeFalse();
    }
}
