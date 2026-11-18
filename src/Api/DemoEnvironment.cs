namespace Reservation.Api;

/// <summary>
/// The hosting environment of the AWS demo deployment (M12a, Design §17.4): <c>ASPNETCORE_ENVIRONMENT=Demo</c> with
/// <c>appsettings.Demo.json</c>. Never Development in AWS.
/// </summary>
internal static class DemoEnvironment
{
    public const string Name = "Demo";

    /// <summary>
    /// Where the API migrates and seeds at start-up when <c>Database:MigrateOnStartup</c> is set: Development, and the
    /// single-instance AWS demo. The pilot uses a migration bundle instead (ADR-005).
    /// </summary>
    public static bool MayMigrateOnStartup(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.IsDevelopment() || environment.IsEnvironment(Name);
    }
}
