using System.Net;
using Reservation.Api.Pipeline;

namespace Reservation.Tests.Unit.Common;

/// <summary>Reading <c>CloudFront-Viewer-Address</c> (M12a, Design §17.4): the IP before the last colon, the port after.</summary>
public class EdgeOriginTests
{
    [Theory]
    [InlineData("198.51.100.10:46532", "198.51.100.10")]
    [InlineData(" 203.0.113.7:443 ", "203.0.113.7")]
    [InlineData("2001:db8::7:51000", "2001:db8::7")]
    [InlineData("[2001:db8::1]:8443", "2001:db8::1")]
    [InlineData("::ffff:192.0.2.5:1234", "192.0.2.5")]
    public void A_viewer_address_gives_the_ip(string header, string expected)
    {
        Assert.True(EdgeOriginMiddleware.TryParseViewerAddress(header, out var address));
        Assert.Equal(IPAddress.Parse(expected), address);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("198.51.100.10")]
    [InlineData("198.51.100.10:")]
    [InlineData("198.51.100.10:port")]
    [InlineData("198.51.100.10:70000")]
    [InlineData("not-an-ip:443")]
    [InlineData(":443")]
    public void Anything_else_is_not_used(string? header)
    {
        Assert.False(EdgeOriginMiddleware.TryParseViewerAddress(header, out var address));
        Assert.Null(address);
    }
}
