using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Reservation.Domain.Common;

namespace Reservation.Infrastructure.Persistence.Converters;

// Strongly typed IDs are stored as plain uuid columns (Design §1, ADR-003 DM-02).

public sealed class VenueIdConverter() : ValueConverter<VenueId, Guid>(id => id.Value, value => new VenueId(value));

public sealed class AreaIdConverter() : ValueConverter<AreaId, Guid>(id => id.Value, value => new AreaId(value));

public sealed class TableIdConverter() : ValueConverter<TableId, Guid>(id => id.Value, value => new TableId(value));

public sealed class GuestIdConverter() : ValueConverter<GuestId, Guid>(id => id.Value, value => new GuestId(value));

public sealed class BookingIdConverter() : ValueConverter<BookingId, Guid>(id => id.Value, value => new BookingId(value));

public sealed class WaitlistEntryIdConverter() : ValueConverter<WaitlistEntryId, Guid>(id => id.Value, value => new WaitlistEntryId(value));

public sealed class MessageIdConverter() : ValueConverter<MessageId, Guid>(id => id.Value, value => new MessageId(value));

public sealed class PosMessageIdConverter() : ValueConverter<PosMessageId, Guid>(id => id.Value, value => new PosMessageId(value));
