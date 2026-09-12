using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Library.IntegrationTests;

/// <summary>
/// Phase 1 acceptance: the host boots, the pipeline is wired, and errors come
/// back in the agreed shape.
/// </summary>
/// <remarks>
/// There are no business endpoints yet. What these verify is the scaffolding
/// every later phase depends on - and each of them has failed for real on some
/// project at some point, which is why they are worth asserting now rather than
/// discovering in Phase 4.
/// </remarks>
public sealed class SmokeTests : IClassFixture<LibraryApiFactory>
{
    private readonly LibraryApiFactory _factory;

    public SmokeTests(LibraryApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Application_starts_and_the_liveness_probe_responds()
    {
        // If the DI container has a missing or circular registration, the host
        // throws here - which makes this the single most valuable test in the
        // suite right now.
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Readiness_probe_confirms_the_database_is_reachable()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OpenAPI_document_is_generated_and_describes_this_API()
    {
        // Guards the .NET 10 OpenAPI wiring: the document is produced by the
        // framework's MapOpenApi(), not by Swashbuckle's generator.
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        document.RootElement.GetProperty("info").GetProperty("title")
            .GetString().ShouldBe("Library Management API");
    }

    [Fact]
    public async Task Swagger_UI_is_served_in_development()
    {
        HttpClient client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        // /swagger redirects to /swagger/index.html - a 301/302 here is success,
        // not failure.
        HttpResponseMessage response = await client.GetAsync("/swagger/index.html", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_unmatched_route_returns_ProblemDetails_not_an_empty_body()
    {
        // UseStatusCodePages is what makes this work. Without it the framework
        // returns a bare 404 with no body, forcing clients to handle two
        // different error shapes.
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/does-not-exist", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType
            .ShouldBe("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        problem.GetProperty("status").GetInt32().ShouldBe(404);
    }
}
