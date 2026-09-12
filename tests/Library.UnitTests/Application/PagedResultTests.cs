using Library.Application.Common.Models;

namespace Library.UnitTests.Application;

public sealed class PagedResultTests
{
    [Theory]
    [InlineData(0, 20, 0)]    // no results at all
    [InlineData(1, 20, 1)]    // a single item still makes one page
    [InlineData(20, 20, 1)]   // exactly full
    [InlineData(21, 20, 2)]   // one over - the classic off-by-one
    [InlineData(100, 20, 5)]
    public void Total_pages_rounds_up(int totalCount, int pageSize, int expectedPages)
    {
        new PagedResult<string>([], page: 1, pageSize, totalCount)
            .TotalPages.ShouldBe(expectedPages);
    }

    [Fact]
    public void Middle_page_reports_neighbours_on_both_sides()
    {
        var result = new PagedResult<string>(["a"], page: 3, pageSize: 10, totalCount: 100);

        result.HasPreviousPage.ShouldBeTrue();
        result.HasNextPage.ShouldBeTrue();
    }

    [Fact]
    public void First_page_has_no_previous()
    {
        var result = new PagedResult<string>(["a"], page: 1, pageSize: 10, totalCount: 100);

        result.HasPreviousPage.ShouldBeFalse();
        result.HasNextPage.ShouldBeTrue();
    }

    [Fact]
    public void Last_page_has_no_next()
    {
        var result = new PagedResult<string>(["a"], page: 10, pageSize: 10, totalCount: 100);

        result.HasPreviousPage.ShouldBeTrue();
        result.HasNextPage.ShouldBeFalse();
    }

    [Fact]
    public void An_empty_result_offers_no_navigation()
    {
        PagedResult<string> result = PagedResult.Empty<string>(page: 1, pageSize: 20);

        result.Items.ShouldBeEmpty();
        result.TotalCount.ShouldBe(0);
        result.TotalPages.ShouldBe(0);
        result.HasPreviousPage.ShouldBeFalse();
        result.HasNextPage.ShouldBeFalse();
    }

    [Fact]
    public void A_zero_page_size_does_not_divide_by_zero()
    {
        // Normalize() should make this unreachable, but TotalPages must not
        // throw if a PagedResult is ever constructed directly.
        new PagedResult<string>([], page: 1, pageSize: 0, totalCount: 10)
            .TotalPages.ShouldBe(0);
    }
}
