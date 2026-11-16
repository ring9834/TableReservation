using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Reservation.Contracts.Pos;

namespace Reservation.Tests.Unit.Pos;

/// <summary>
/// The POS contract's times (docs/api/pos-messages.md, "Envelope"): ISO 8601 in UTC, ending in <c>Z</c>, in every
/// message we write; inbound <c>BillClosed</c> also accepted with <c>+00:00</c> (M11b, Design §17.3). And every example
/// in the contract doc reads and writes back exactly as shown.
/// </summary>
public partial class PosTimestampTests
{
    private static readonly Guid _venue = Guid.Parse("0193a7d0-0000-7000-8000-000000000001");
    private static readonly Guid _table = Guid.Parse("0193a7d0-0000-7000-8000-000000000007");

    /// <summary>18:25 in Auckland (NZDT, +13:00) is 05:25 UTC.</summary>
    private static readonly DateTimeOffset _aucklandEvening = new(2026, 11, 24, 18, 25, 0, TimeSpan.FromHours(13));

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?Z$")]
    private static partial Regex UtcWithZ();

    [Fact]
    public void Every_time_in_an_outbound_message_is_written_in_UTC_ending_in_Z()
    {
        var open = new PosOpenTable
        {
            MessageId = Guid.CreateVersion7(),
            MessageType = PosMessageTypes.OpenTable,
            VenueId = _venue,
            BookingId = Guid.CreateVersion7(),
            TableIds = [_table],
            OccurredAt = _aucklandEvening,
            PartySize = 2,
            SeatedAt = _aucklandEvening,
        };

        var json = JsonSerializer.SerializeToElement(open, PosJson.Options);

        foreach (var field in new[] { "occurredAt", "seatedAt" })
        {
            var text = json.GetProperty(field).GetString()!;
            Assert.Matches(UtcWithZ(), text);
            Assert.Equal("2026-11-24T05:25:00Z", text);
        }
    }

    [Theory]
    [InlineData(0, "2026-11-24T05:25:00Z")]
    [InlineData(500, "2026-11-24T05:25:00.5Z")]
    [InlineData(123, "2026-11-24T05:25:00.123Z")]
    public void Fractional_seconds_appear_only_when_there_are_any(int milliseconds, string expected)
    {
        var at = _aucklandEvening.AddMilliseconds(milliseconds);

        var text = JsonSerializer.Serialize(at, PosJson.Options);

        Assert.Equal($"\"{expected}\"", text);
    }

    [Theory]
    [InlineData("2026-11-24T07:10:00Z")]
    [InlineData("2026-11-24T07:10:00+00:00")]
    [InlineData("2026-11-24T20:10:00+13:00")]
    public void An_inbound_BillClosed_reads_Z_and_offsets_as_the_same_UTC_instant(string closedAt)
    {
        var body = $$"""
            {"version":1,"messageId":"6f1c2a0e-8b7d-4c3e-9a51-0d2e7f4b8c19","messageType":"BillClosed",
             "venueId":"{{_venue}}","tableIds":["{{_table}}"],"occurredAt":"{{closedAt}}",
             "posBillId":"B-1","tableId":"{{_table}}","billTotalCents":31850,"closedAt":"{{closedAt}}"}
            """;

        var bill = JsonSerializer.Deserialize<PosBillClosed>(body, PosJson.Options)!;

        var expected = new DateTimeOffset(2026, 11, 24, 7, 10, 0, TimeSpan.Zero);
        Assert.Equal((expected, TimeSpan.Zero), (bill.ClosedAt, bill.ClosedAt.Offset));
        Assert.Equal((expected, TimeSpan.Zero), (bill.OccurredAt, bill.OccurredAt.Offset));
    }

    [Fact]
    public void Every_example_in_the_contract_doc_reads_and_writes_back_exactly()
    {
        var doc = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "api", "pos-messages.md"));
        var examples = JsonBlock().Matches(doc).Select(m => m.Groups["json"].Value).ToList();
        Assert.Equal(4, examples.Count); // UpsertMember, OpenTable, ApplyDeposit, BillClosed

        foreach (var example in examples)
        {
            var shown = JsonNode.Parse(example)!;
            var type = shown["messageType"]!.GetValue<string>() switch
            {
                PosMessageTypes.UpsertMember => typeof(PosUpsertMember),
                PosMessageTypes.OpenTable => typeof(PosOpenTable),
                PosMessageTypes.ApplyDeposit => typeof(PosApplyDeposit),
                PosMessageTypes.BillClosed => typeof(PosBillClosed),
                var other => throw new InvalidOperationException($"Unknown message type {other} in the contract doc"),
            };

            var message = JsonSerializer.Deserialize(example, type, PosJson.Options)!;
            var written = JsonSerializer.SerializeToNode(message, type, PosJson.Options)!;

            Assert.True(
                JsonNode.DeepEquals(shown, written),
                $"The {type.Name} example in docs/api/pos-messages.md differs from what we write:\n{written.ToJsonString()}");
        }
    }

    [GeneratedRegex(@"```json\s*\n(?<json>.*?)\n```", RegexOptions.Singleline)]
    private static partial Regex JsonBlock();

    /// <summary>The folder holding ReservationDemo.slnx, above the test output or, for a build elsewhere, above this file.</summary>
    private static string RepositoryRoot([CallerFilePath] string sourceFile = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(sourceFile) ?? "" })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ReservationDemo.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new InvalidOperationException("ReservationDemo.slnx not found above the test output or this file.");
    }
}
