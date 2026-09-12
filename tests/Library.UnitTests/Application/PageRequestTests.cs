using Library.Application.Common.Models;

namespace Library.UnitTests.Application;

/// <summary>
/// Verifies that paging input is clamped into a safe range.
/// </summary>
/// <remarks>
/// The <c>pageSize=1000000</c> case is the one that matters. Without a
/// server-side ceiling that single query materialises a million rows, and one
/// unauthenticated request becomes a denial of service. This test is the
/// regression guard on that control.
/// </remarks>
public sealed class PageRequestTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Page_below_one_is_clamped_to_the_first_page(int page)
    {
        new PageRequest { Page = page }.Normalize().Page.ShouldBe(1);
    }

    [Theory]
    [InlineData(1_000_000)]
    [InlineData(101)]
    [InlineData(int.MaxValue)]
    public void Page_size_above_the_ceiling_is_clamped(int pageSize)
    {
        new PageRequest { PageSize = pageSize }.Normalize()
            .PageSize.ShouldBe(PageRequest.MaxPageSize);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Page_size_below_one_falls_back_to_the_default(int pageSize)
    {
        new PageRequest { PageSize = pageSize }.Normalize()
            .PageSize.ShouldBe(PageRequest.DefaultPageSize);
    }

    [Fact]
    public void Values_already_in_range_are_left_alone()
    {
        PageRequest normalized = new PageRequest { Page = 3, PageSize = 25 }.Normalize();

        normalized.Page.ShouldBe(3);
        normalized.PageSize.ShouldBe(25);
    }

    [Fact]
    public void Skip_is_derived_from_page_and_size()
    {
        // Page 4 at 20 per page skips the first 60 rows, not 80 - Page is 1-based.
        new PageRequest { Page = 4, PageSize = 20 }.Normalize().Skip.ShouldBe(60);
    }

    [Fact]
    public void First_page_skips_nothing()
    {
        new PageRequest { Page = 1, PageSize = 20 }.Normalize().Skip.ShouldBe(0);
    }
}
