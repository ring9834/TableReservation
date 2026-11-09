namespace Reservation.Infrastructure.Outbox;

/// <summary>Settings for the outbox workers (configuration section <c>Outbox</c>).</summary>
public sealed class OutboxOptions
{
    public const string Section = "Outbox";

    /// <summary>How long the dispatcher waits for a signal before polling anyway (infrastructure clock).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Rows claimed per transaction.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>False stops the dispatcher and housekeeping from running (tests that host the API without a database).</summary>
    public bool RunWorkers { get; set; } = true;
}
