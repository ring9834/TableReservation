using Reservation.Domain.Common;

namespace Reservation.Domain.Venues;

/// <summary>Venue aggregate: layout (areas, tables) and settings (Design §2.1, §2.2).</summary>
public sealed class Venue : AggregateRoot<VenueId>
{
    private readonly List<Area> _areas;

    private Venue(VenueId id, string name, string timeZone, string currency, VenueSettings settings, List<Area> areas)
    {
        Id = id;
        Name = name;
        TimeZone = timeZone;
        Currency = currency;
        Settings = settings;
        SettingsVersion = 1;
        _areas = areas;
    }

    public string Name { get; private set; }

    /// <summary>IANA time zone, e.g. <c>Pacific/Auckland</c>.</summary>
    public string TimeZone { get; private set; }

    public string Currency { get; private set; }

    public VenueSettings Settings { get; private set; }

    /// <summary>Goes up by 1 on every settings change; copied into <c>BookingTerms</c> (§2.6).</summary>
    public int SettingsVersion { get; private set; }

    public IReadOnlyList<Area> Areas => _areas;

    public IEnumerable<Table> Tables => _areas.SelectMany(a => a.Tables);

    public static Result<Venue> Create(
        VenueId id, string name, string timeZone, string currency, VenueSettings settings, IEnumerable<Area> areas)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(areas);

        if (string.IsNullOrWhiteSpace(name))
        {
            return Error.Validation("name", "name_length");
        }

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _))
        {
            return Error.Validation("timeZone", "time_zone_invalid");
        }

        var currencyCheck = Money.Create(0, currency);
        if (currencyCheck.IsFailure)
        {
            return currencyCheck.Error!;
        }

        var settingsCheck = settings.Validate();
        if (settingsCheck.IsFailure)
        {
            return settingsCheck.Error!;
        }

        var areaList = areas.ToList();
        if (areaList.Count == 0)
        {
            return Error.Validation("areas", "areas_required");
        }

        var tableIds = areaList.SelectMany(a => a.Tables).Select(t => t.Id).ToList();
        if (tableIds.Count != tableIds.Distinct().Count())
        {
            return Error.Validation("tables", "table_duplicate");
        }

        return new Venue(id, name.Trim(), timeZone, currency, settings, areaList);
    }

    public TimeZoneInfo FindTimeZone() => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    public Table? FindTable(TableId tableId) => Tables.FirstOrDefault(t => t.Id == tableId);

    /// <summary>Replaces the settings. Existing bookings keep their own terms (§2.6).</summary>
    public Result UpdateSettings(VenueSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var check = settings.Validate();
        if (check.IsFailure)
        {
            return check;
        }

        Settings = settings;
        SettingsVersion++;
        return Result.Success();
    }
}

/// <summary>An area of the floor (e.g. Main, Private Room).</summary>
public sealed class Area
{
    private readonly List<Table> _tables;

    public Area(AreaId id, string name, int sortOrder, IEnumerable<Table> tables)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(tables);
        Id = id;
        Name = name;
        SortOrder = sortOrder;
        _tables = [.. tables];
    }

    public AreaId Id { get; }

    public string Name { get; }

    public int SortOrder { get; }

    public IReadOnlyList<Table> Tables => _tables;
}

/// <summary>A table. Its state is derived from bookings, never stored (§2.5).</summary>
public sealed class Table
{
    public Table(TableId id, string name, int seats, FloorPosition position)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seats);
        ArgumentNullException.ThrowIfNull(position);
        Id = id;
        Name = name;
        Seats = seats;
        Position = position;
    }

    public TableId Id { get; }

    public string Name { get; }

    public int Seats { get; }

    public FloorPosition Position { get; }

    public bool Fits(PartySize partySize) => Seats >= partySize.Value;
}

public enum TableShape
{
    Round,
    Rect,
}

/// <summary>Where a table is drawn on the floor plan, in floor units (ADR-001).</summary>
public sealed record FloorPosition(decimal X, decimal Y, decimal Rotation, TableShape Shape, decimal Width, decimal Height);
