using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Library.Application;

/// <summary>
/// Registers everything the Application layer owns.
/// </summary>
/// <remarks>
/// <para>
/// Each layer exposes one extension method like this, and <c>Program.cs</c> calls
/// them in order. The alternative - a single 200-line <c>Program.cs</c> that
/// knows every type in the solution - couples the host to details it has no
/// business knowing, and makes the composition root the first place every merge
/// conflict happens.
/// </para>
/// <para>
/// Validators are discovered by assembly scanning rather than registered one by
/// one. That is a deliberate trade: adding a validator becomes zero-ceremony, at
/// the cost of registration no longer being greppable. The rule that makes it
/// safe is naming - every validator is <c>&lt;Request&gt;Validator</c> and lives
/// beside its request.
/// </para>
/// </remarks>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        Assembly assembly = typeof(DependencyInjection).Assembly;

        // FineOptions is DECLARED here (the handler depends on FineRateResolver)
        // but BOUND in AddInfrastructure. Reading configuration is an
        // infrastructure concern, and this layer deliberately has no
        // Microsoft.Extensions.Configuration reference to do it with.

        // Scans for every IValidator<T> in this assembly.
        // Registered as Scoped so a validator may depend on scoped services
        // (e.g. a repository, to check uniqueness against the database).
        services.AddValidatorsFromAssembly(assembly, ServiceLifetime.Scoped);

        // Scoped: one instance per HTTP request, matching the DbContext lifetime
        // its repository depends on. Singleton here would be a captive-dependency
        // bug - the service would capture one DbContext and reuse it across every
        // concurrent request, which DbContext is explicitly not safe for.
        services.AddScoped<Books.IBookService, Books.BookService>();
        services.AddScoped<Members.IMemberService, Members.MemberService>();
        services.AddScoped<Loans.ILoanService, Loans.LoanService>();
        services.AddScoped<Loans.IFineService, Loans.FineService>();
        services.AddScoped<Books.Import.IBookImportService, Books.Import.BookImportService>();
        services.AddScoped<Reports.IReportService, Reports.ReportService>();

        // Stateless and thread-safe: the encoder holds no state and is called on
        // every field of every export, so one instance is right.
        services.AddSingleton<
            Reports.Export.ICsvFieldEncoder, Reports.Export.CsvFieldEncoder>();

        // Handlers are resolved by the dispatcher from the closed interface type,
        // so each must be registered against IDomainEventHandler<TEvent> and not
        // only against its own class - GetServices(typeof(IDomainEventHandler<..>))
        // finds nothing otherwise, and the fine would silently never be assessed.
        services.AddScoped<
            Common.Abstractions.IDomainEventHandler<Domain.Events.LoanReturnedEvent>,
            Loans.Handlers.FineAssessmentHandler>();

        return services;
    }
}
