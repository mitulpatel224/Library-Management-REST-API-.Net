using Microsoft.OpenApi;

namespace Library.Api.OpenApi;

/// <summary>
/// Wires up the OpenAPI document and the Swagger UI that renders it.
/// </summary>
/// <remarks>
/// <para>
/// <b>What changed, and why this looks unfamiliar.</b> Through .NET 8 the
/// <c>webapi</c> template shipped Swashbuckle, which both generated the OpenAPI
/// document and served the UI. From .NET 9 the template dropped Swashbuckle:
/// document generation moved into the framework as
/// <c>Microsoft.AspNetCore.OpenApi</c> (<c>AddOpenApi</c> / <c>MapOpenApi</c>),
/// which is trimming- and AOT-friendly and understands minimal APIs natively.
/// </para>
/// <para>
/// The framework ships no UI, though. So this project uses each tool for the
/// half it is now best at: the framework produces <c>/openapi/v1.json</c>, and
/// <c>Swashbuckle.AspNetCore.SwaggerUI</c> - the UI package alone, not the full
/// Swashbuckle - renders it at <c>/swagger</c>. That is why there is no
/// <c>AddSwaggerGen</c> call anywhere in this solution.
/// </para>
/// <para>
/// A document transformer fills in the title, version, and contact metadata.
/// The JWT security scheme is added in Phase 5, which is what puts the
/// <i>Authorize</i> button in the UI.
/// </para>
/// </remarks>
public static class OpenApiConfiguration
{
    public const string DocumentName = "v1";

    public static IServiceCollection AddOpenApiDocument(this IServiceCollection services)
    {
        services.AddOpenApi(DocumentName, options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Library Management API",
                    Version = "v1",
                    Description =
                        "REST API for managing a library catalogue, members, and lending. "
                        + "Built on .NET 10 with Clean Architecture and EF Core.\n\n"
                        + "Errors follow RFC 9457 (ProblemDetails); every error response "
                        + "carries a stable `errorCode` and a `traceId`.",
                    Contact = new OpenApiContact
                    {
                        Name = "Library Management - Assessment",
                    },
                };

                return Task.CompletedTask;
            });
        });

        return services;
    }

    /// <summary>
    /// Serves Swagger UI at <c>/swagger</c> against the framework-generated document.
    /// </summary>
    public static WebApplication UseSwaggerUiForOpenApi(this WebApplication app)
    {
        app.UseSwaggerUI(options =>
        {
            // Points the UI at the document produced by MapOpenApi(), NOT at a
            // Swashbuckle-generated one.
            options.SwaggerEndpoint($"/openapi/{DocumentName}.json", "Library Management API v1");
            options.RoutePrefix = "swagger";
            options.DocumentTitle = "Library Management API";

            // Collapsed by default: with ~40 endpoints an expanded list is
            // unusable on first load.
            options.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.List);
            options.DisplayRequestDuration();
            options.EnableTryItOutByDefault();
        });

        return app;
    }
}
