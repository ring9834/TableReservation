using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reservation.Domain.Common;
using Reservation.Infrastructure;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Tests.Integration.Support;

/// <summary>
/// The real infrastructure registrations (<see cref="DependencyInjection.AddInfrastructure"/>) against a test database,
/// with two test clocks (business and infrastructure), captured logs and the test consumers below registered for
/// every event type: <c>recording</c> (idempotent), <c>probe</c> (not wrapped, sees every delivery) and <c>failing</c>
/// (idempotent, throws while <see cref="EventRecorder.Fail"/> says so). Workers are not started unless a test does.
/// </summary>
public sealed class EventHarness : IAsyncDisposable
{
    private EventHarness(TestDatabase database)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Outbox:PollInterval"] = "00:00:01",
                ["Outbox:BatchSize"] = "50",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        services.AddSingleton<TimeProvider>(BusinessClock);
        services.AddKeyedSingleton<TimeProvider>(Clocks.Infrastructure, InfrastructureClock);
        services.AddSingleton(database.DataSource);
        services.AddSingleton<IVenueContext>(new FixedVenueContext(Infrastructure.Demo.GoldenDragon.VenueId));
        services.AddInfrastructure(database.ConnectionString, o => o.ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));

        services.AddSingleton(Recorder);
        foreach (var eventType in new EventTypeRegistry().All.Select(e => e.Type))
        {
            // Failing before the others, so the tests show the handlers after a failure still run.
            services.AddEventHandler(eventType, typeof(FailingHandler<>).MakeGenericType(eventType));
            services.AddEventHandler(eventType, typeof(RecordingHandler<>).MakeGenericType(eventType));
            services.AddEventHandler(eventType, typeof(ProbeHandler<>).MakeGenericType(eventType), idempotent: false);
        }

        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public TestClock BusinessClock { get; } = new(DemoTime.Now);

    public TestClock InfrastructureClock { get; } = new(DemoTime.InfrastructureStart);

    public LogCapture Logs { get; } = new();

    public EventRecorder Recorder { get; } = new();

    public ServiceProvider Services { get; }

    public OutboxDispatcher Dispatcher => Services.GetRequiredService<OutboxDispatcher>();

    public OutboxSignal Signal => Services.GetRequiredService<OutboxSignal>();

    public static EventHarness Create(TestDatabase database) => new(database);

    /// <summary>A second, independent dispatcher sharing the same services (two workers at once).</summary>
    public OutboxDispatcher NewDispatcher() => new(
        Services.GetRequiredService<IServiceScopeFactory>(),
        Signal,
        Services.GetRequiredService<IOptions<OutboxOptions>>(),
        InfrastructureClock,
        Services.GetRequiredService<ILogger<OutboxDispatcher>>());

    /// <summary>Runs batches until none is left that is due now.</summary>
    public async Task<int> DispatchAllAsync()
    {
        var total = 0;
        int handled;
        while ((handled = await Dispatcher.DispatchBatchAsync(TestContext.Current.CancellationToken)) > 0)
        {
            total += handled;
        }

        return total;
    }

    /// <summary>A scope with its own context (with the interceptor and a fresh request context).</summary>
    public AsyncServiceScope Scope() => Services.CreateAsyncScope();

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}

/// <summary>What the test consumers saw, and which aggregates make the failing consumer throw.</summary>
public sealed class EventRecorder
{
    private readonly ConcurrentQueue<Delivery> _deliveries = new();
    private readonly ConcurrentDictionary<Guid, bool> _failing = new();

    public IReadOnlyList<Delivery> Deliveries => [.. _deliveries];

    public void Record(string consumer, EventContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _deliveries.Enqueue(new Delivery(consumer, context.EventId, context.AggregateId, context.Sequence));
    }

    public void Fail(Guid aggregateId) => _failing[aggregateId] = true;

    public void Recover(Guid aggregateId) => _failing.TryRemove(aggregateId, out _);

    public bool ShouldFail(Guid aggregateId) => _failing.ContainsKey(aggregateId);

    public IReadOnlyList<Delivery> For(string consumer) => [.. _deliveries.Where(d => d.Consumer == consumer)];
}

public sealed record Delivery(string Consumer, Guid EventId, Guid AggregateId, long Sequence);

public sealed class RecordingHandler<T>(EventRecorder recorder) : IEventHandler<T>
    where T : IDomainEvent
{
    public const string Consumer = "recording";

    public string ConsumerName => Consumer;

    public Task HandleAsync(T domainEvent, EventContext context, CancellationToken cancellationToken)
    {
        recorder.Record(Consumer, context);
        return Task.CompletedTask;
    }
}

/// <summary>Not wrapped in the inbox decorator, so it sees every delivery, repeats included.</summary>
public sealed class ProbeHandler<T>(EventRecorder recorder) : IEventHandler<T>
    where T : IDomainEvent
{
    public const string Consumer = "probe";

    public string ConsumerName => Consumer;

    public Task HandleAsync(T domainEvent, EventContext context, CancellationToken cancellationToken)
    {
        recorder.Record(Consumer, context);
        return Task.CompletedTask;
    }
}

/// <summary>Writes an inbox row of its own, then throws while its aggregate is marked failing.</summary>
public sealed class FailingHandler<T>(EventRecorder recorder, ReservationsDbContext db) : IEventHandler<T>
    where T : IDomainEvent
{
    public const string Consumer = "failing";

    public string ConsumerName => Consumer;

    public async Task HandleAsync(T domainEvent, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!recorder.ShouldFail(context.AggregateId))
        {
            recorder.Record(Consumer, context);
            return;
        }

        // Some work that the savepoint must undo.
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO inbox (consumer, message_id, processed_at) VALUES ('failing-side-effect', {context.EventId.ToString()}, now())",
            cancellationToken);
        throw new InvalidOperationException("Simulated handler failure.");
    }
}

/// <summary>Collects every log line (message, exception text and scope-free state) for scrubbing checks.</summary>
/// <summary>
/// Captures every log line with its message, structured properties, exception and the values of every open scope
/// (the API's request scope included), so tests can check that no personal data reaches the logs.
/// </summary>
public sealed class LogCapture : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<LogLine> _lines = new();
    private IExternalScopeProvider? _scopes;

    public IReadOnlyList<LogLine> Lines => [.. _lines];

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private static string Describe(object? state) => state is IEnumerable<KeyValuePair<string, object?>> pairs
        ? string.Join(" ", pairs.Select(p => $"{p.Key}={p.Value}"))
        : state?.ToString() ?? string.Empty;

    private sealed class CapturingLogger(string category, LogCapture owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => owner._scopes?.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            var scopes = new List<string>();
            owner._scopes?.ForEachScope((scope, list) => list.Add(Describe(scope)), scopes);
            owner._lines.Enqueue(new LogLine(
                category, logLevel, $"{formatter(state, exception)} {Describe(state)} {string.Join(" | ", scopes)} {exception}"));
        }
    }
}

public sealed record LogLine(string Category, LogLevel Level, string Text);
