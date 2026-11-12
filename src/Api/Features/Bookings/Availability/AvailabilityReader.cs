using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Reservation.Api.Features.Seating;
using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Venues;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Api.Features.Bookings.Availability;

/// <summary>
/// Loads one service day for <see cref="AvailabilityCalculator"/> (Design §11.1): the venue's tables, the active
/// table assignments overlapping the service window (<c>&amp;&amp;</c> on <c>tstzrange</c>), and the party sizes of
/// active bookings (walk-ins excluded) starting in it, which the calculator sums per slot. Two small queries, run on
/// the caller's connection and transaction, so the create handler reads under its pacing lock.
/// </summary>
internal sealed class AvailabilityReader(ReservationsDbContext db)
{
    private const string ActiveStatuses =
        $"'{nameof(BookingStatus.PendingPayment)}', '{nameof(BookingStatus.Booked)}', '{nameof(BookingStatus.Confirmed)}', '{nameof(BookingStatus.Seated)}'";

    private const string BusySql =
        "SELECT table_id, lower(period), upper(period) FROM table_assignments " +
        "WHERE venue_id = @venue AND active AND period && tstzrange(@from, @to)";

    private const string CoversSql =
        "SELECT lower(period), party_size FROM bookings " +
        $"WHERE venue_id = @venue AND status IN ({ActiveStatuses}) AND channel <> '{nameof(BookingChannel.WalkIn)}' " +
        "AND lower(period) >= @from AND lower(period) < @to";

    public async Task<AvailabilityInput> LoadAsync(Venue venue, DateOnly date, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(venue);
        var timeZone = venue.FindTimeZone();
        var window = AvailabilityCalculator.ServiceWindow(venue.Settings, timeZone, date);
        var tables = TablesInFloorOrder(venue);

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var busy = await QueryAsync(
                BusySql, venue.Id, window, r => new BusyPeriod(
                    new TableId(r.GetGuid(0)), new TimeRange(r.GetFieldValue<DateTimeOffset>(1), r.GetFieldValue<DateTimeOffset>(2))),
                cancellationToken);
            var covers = await QueryAsync(
                CoversSql, venue.Id, window, r => new BookedCovers(r.GetFieldValue<DateTimeOffset>(0), r.GetInt32(1)),
                cancellationToken);
            return new AvailabilityInput(venue.Settings, timeZone, tables, busy, covers, now);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private const string NowSql =
        "SELECT ta.booking_id, ta.table_id, lower(ta.period), upper(ta.period), b.status = 'Seated' " +
        "FROM table_assignments ta JOIN bookings b ON b.id = ta.booking_id " +
        "WHERE ta.venue_id = @venue AND ta.active AND (b.status = 'Seated' OR ta.period && tstzrange(@from, @to))";

    /// <summary>
    /// The floor at <paramref name="now"/> for walk-ins, seating and the waitlist (Design §11.5): every seated party
    /// (even past its turn) and every active assignment overlapping <c>[now, now + longest turn)</c>. Runs on the
    /// caller's connection and transaction, so a handler that holds table locks reads under them.
    /// </summary>
    public async Task<FloorNow> LoadNowAsync(Venue venue, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(venue);
        var window = new TimeRange(now, now.AddMinutes(venue.Settings.TurnTimes.Max(t => t.Minutes)));
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var occupancy = await QueryAsync(
                NowSql, venue.Id, window, r => new TableOccupancy(
                    new BookingId(r.GetGuid(0)),
                    new TableId(r.GetGuid(1)),
                    new TimeRange(r.GetFieldValue<DateTimeOffset>(2), r.GetFieldValue<DateTimeOffset>(3)),
                    r.GetBoolean(4)),
                cancellationToken);
            return new FloorNow(venue.Settings, TablesInFloorOrder(venue), occupancy, now);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Area order, then table ID: the order BR-05 breaks ties in.</summary>
    public static List<TableInfo> TablesInFloorOrder(Venue venue)
    {
        ArgumentNullException.ThrowIfNull(venue);
        return [.. venue.Areas.OrderBy(a => a.SortOrder)
            .SelectMany(a => a.Tables.OrderBy(t => t.Id.Value))
            .Select(t => new TableInfo(t.Id, t.Name, t.Seats))];
    }

    private async Task<List<T>> QueryAsync<T>(
        string sql, VenueId venueId, TimeRange window, Func<DbDataReader, T> read, CancellationToken cancellationToken)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.Parameters.Add(new NpgsqlParameter("venue", venueId.Value));
        command.Parameters.Add(new NpgsqlParameter("from", window.Start));
        command.Parameters.Add(new NpgsqlParameter("to", window.End));

        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(read(reader));
        }

        return rows;
    }
}
