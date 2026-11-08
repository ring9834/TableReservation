using System.Reflection;

namespace Reservation.Tests.Unit;

public class DomainDependencyTests
{
    private static readonly string[] _forbiddenPrefixes =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Npgsql",
    ];

    [Fact]
    public void Domain_does_not_reference_persistence_or_web_frameworks()
    {
        var domain = Assembly.Load("Reservation.Domain");

        var forbidden = domain.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name => _forbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(forbidden);
    }
}
