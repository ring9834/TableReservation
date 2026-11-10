using System.Reflection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Reservation.Domain.Common;

namespace Reservation.Api.Common;

/// <summary>One small class per feature that maps its routes (Design §9.1). Found through DI, so tests can add their own.</summary>
internal interface IEndpoint
{
    /// <summary>Maps the feature's routes onto the <c>/api/v1</c> group.</summary>
    void Map(IEndpointRouteBuilder api);
}

/// <summary>Handles one action that changes data (Design §9.1).</summary>
internal interface ICommandHandler<in TCommand, TResult>
{
    Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken);
}

/// <summary>Handles one read; reads straight into response objects (Design §9.1, §9.3).</summary>
internal interface IQueryHandler<in TQuery, TResult>
{
    Task<Result<TResult>> Handle(TQuery query, CancellationToken cancellationToken);
}

internal static class EndpointRegistration
{
    public const string BasePath = "/api/v1";

    /// <summary>Registers every <see cref="IEndpoint"/> and every command and query handler in <paramref name="assembly"/>.</summary>
    public static IServiceCollection AddFeatures(this IServiceCollection services, Assembly assembly)
    {
        foreach (var type in assembly.GetTypes().Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false }))
        {
            if (typeof(IEndpoint).IsAssignableFrom(type))
            {
                services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IEndpoint), type));
            }

            var isHandler = type.GetInterfaces().Any(i => i.IsGenericType
                && (i.GetGenericTypeDefinition() == typeof(ICommandHandler<,>) || i.GetGenericTypeDefinition() == typeof(IQueryHandler<,>)));
            if (isHandler)
            {
                services.TryAddScoped(type);
            }
        }

        return services;
    }

    /// <summary>Maps every registered <see cref="IEndpoint"/> under <see cref="BasePath"/>.</summary>
    public static IEndpointRouteBuilder MapFeatures(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(BasePath);
        foreach (var endpoint in app.ServiceProvider.GetServices<IEndpoint>())
        {
            endpoint.Map(api);
        }

        return app;
    }
}
