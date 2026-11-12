using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using Reservation.Api.Auth;
using Reservation.Api.Common;
using Reservation.Api.Features.Bookings;
using Reservation.Api.Features.Bookings.Availability;
using Reservation.Api.Pipeline;
using Reservation.Api.Realtime;

namespace Reservation.Api;

/// <summary>The API's services and its request pipeline, in the ADR-004 order (Design §14.1).</summary>
internal static class ApiComposition
{
    public const string CorsSection = "Cors:AllowedOrigins";

    public static WebApplicationBuilder AddApi(this WebApplicationBuilder builder)
    {
        // JSON logs with scopes; trace and span IDs from the W3C Activity (ADR-004). OpenTelemetry export arrives in M12.
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
        });
        builder.Logging.Configure(options => options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);

        var services = builder.Services;
        // Enums as camelCase strings; numbers as numbers only, so the OpenAPI schema says "integer", not "integer or string".
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
            options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
        });

        // Malformed bodies and parameters throw, so the central handler answers 400 validation_failed.
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.AddProblemDetails(options => options.CustomizeProblemDetails = ApiProblems.Customize);
        services.AddExceptionHandler<GlobalExceptionHandler>();

        services.AddApiRateLimiting();
        services.AddDemoSession();

        var origins = builder.Configuration.GetSection(CorsSection).Get<string[]>() ?? [];
        services.AddCors(options => options.AddDefaultPolicy(policy => policy
            .WithOrigins(origins)
            .AllowCredentials()
            .WithMethods("GET", "POST", "PUT", "DELETE")
            // X-Requested-With and X-SignalR-User-Agent: the browser SignalR client's negotiate and long-polling requests.
            .WithHeaders("Content-Type", IdempotencyKey.Header, RequestLoggingMiddleware.CorrelationHeader, "X-Requested-With", "X-SignalR-User-Agent")
            .WithExposedHeaders(RequestLoggingMiddleware.CorrelationHeader)));

        services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = "Reservation demo API";
                document.Info.Version = "v1";
                document.Servers?.Clear();
                return Task.CompletedTask;
            });

            // Basic FluentValidation rules → schema limits, so the generated zod schemas match (ADR-004, Design §18.4).
            options.AddSchemaTransformer<FluentValidationSchemaTransformer>();

            // SignalR notices as component schemas (Design §15).
            options.AddNoticeSchemas();
        });

        services.AddRealtime();

        services.AddValidatorsFromAssemblyContaining<CreateBookingRequestValidator>(includeInternalTypes: true);
        services.AddFeatures(typeof(ApiComposition).Assembly);
        services.AddScoped<AvailabilityReader>();
        services.AddScoped<BookingCreator>();
        services.AddScoped<IdempotentRequests>();
        return builder;
    }

    /// <summary>
    /// ADR-004 order: exception handler → logging and tracing → (routing, CORS) → rate limiting → sign-in →
    /// venue check (in the policies) → request and venue context → validation filter → handler.
    /// </summary>
    public static WebApplication UseApi(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();

        // No UseHttpsRedirection: in AWS the proxy terminates TLS (M12), and the CI end-to-end job calls the API over
        // plain HTTP (Design §18.4). HSTS is sent outside Development only.
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseMiddleware<RequestLoggingMiddleware>();
        app.UseRouting();
        app.UseCors();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseMiddleware<RequestContextMiddleware>();
        return app;
    }

    public static WebApplication MapApi(this WebApplication app)
    {
        app.MapOpenApi().AllowAnonymous().DisableRateLimiting();
        app.MapFeatures();
        app.MapRealtime();
        return app;
    }
}
