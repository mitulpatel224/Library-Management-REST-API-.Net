using System.ComponentModel.DataAnnotations;

namespace Library.Infrastructure.Persistence;

/// <summary>
/// Strongly-typed binding of the <c>Database</c> section of configuration.
/// </summary>
/// <remarks>
/// <para>
/// This is the Options pattern. The alternative -
/// <c>configuration["Database:Provider"]</c> scattered through the codebase -
/// has three problems it fixes: the key is a magic string that no compiler
/// checks, a typo produces <c>null</c> at runtime rather than an error at
/// startup, and there is nowhere to put validation.
/// </para>
/// <para>
/// Registered with <c>ValidateOnStart()</c>, so a missing connection string
/// fails the process at boot with a clear message instead of surfacing as a
/// <c>NullReferenceException</c> on the first request that touches the database.
/// Fail fast and loudly at startup; never fail obscurely under load.
/// </para>
/// </remarks>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>Which EF Core provider to use: <c>Sqlite</c> or <c>SqlServer</c>.</summary>
    [Required]
    public string Provider { get; set; } = DatabaseProviders.Sqlite;

    /// <summary>The connection string for the selected <see cref="Provider"/>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Echo generated SQL and parameter VALUES to the log.
    /// Development only - parameter values include password hashes and personal
    /// data, so enabling this in production would write credentials to disk.
    /// Guarded in code, not just by convention.
    /// </summary>
    public bool EnableSensitiveDataLogging { get; set; }

    /// <summary>Apply pending migrations automatically at startup.</summary>
    /// <remarks>
    /// Convenient in development, dangerous in production: it grants the running
    /// application schema-modification rights and makes deployments non-atomic.
    /// Real deployments run <c>dotnet ef database update</c> as a separate,
    /// reviewable step.
    /// </remarks>
    public bool ApplyMigrationsOnStartup { get; set; }

    /// <summary>Seconds before a command is cancelled. Guards against a runaway query.</summary>
    [Range(1, 300)]
    public int CommandTimeoutSeconds { get; set; } = 30;
}

/// <summary>Supported provider keys, so the strings are declared exactly once.</summary>
public static class DatabaseProviders
{
    public const string Sqlite = "Sqlite";
    public const string SqlServer = "SqlServer";
}
