using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Reservation.Api.Realtime;

namespace Reservation.Tests.Integration.Realtime;

/// <summary>
/// A SignalR client on the in-memory API (long polling through the test server's handler). The session cookie is
/// sent as a header, the way a browser sends it with each request; there is no <c>access_token</c> query string.
/// Every notice is kept as raw JSON, so tests can check what went over the wire.
/// </summary>
public sealed class HubClient : IAsyncDisposable
{
    private static readonly string[] _methods =
        [NoticeMethods.FloorPlanChanged, NoticeMethods.WaitlistChanged, NoticeMethods.MessageLogChanged, NoticeMethods.PosLogChanged];

    private readonly ConcurrentDictionary<string, Channel<JsonElement>> _received = new(StringComparer.Ordinal);

    private HubClient(HubConnection connection)
    {
        Connection = connection;
        foreach (var method in _methods)
        {
            var channel = Channel.CreateUnbounded<JsonElement>();
            _received[method] = channel;
            connection.On<JsonElement>(method, payload => channel.Writer.TryWrite(payload.Clone()));
        }
    }

    public HubConnection Connection { get; }

    /// <summary>Builds a connection; <paramref name="cookie"/> is <c>name=value</c> or <c>null</c> for an anonymous caller.</summary>
    public static HubClient Create(ApiFactory factory, string? cookie)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri($"https://localhost{VenueHub.Path}"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                if (cookie is not null)
                {
                    options.Headers["Cookie"] = cookie;
                }
            })
            .Build();
        return new HubClient(connection);
    }

    public static async Task<HubClient> ConnectAsync(ApiFactory factory, string cookie, CancellationToken cancellationToken)
    {
        var client = Create(factory, cookie);
        await client.Connection.StartAsync(cancellationToken);
        return client;
    }

    /// <summary>Signs in through <paramref name="path"/> and returns the session cookie as <c>name=value</c>.</summary>
    public static async Task<string> SignInAsync(ApiFactory factory, string path, object? body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(factory);
        using var http = factory.Server.CreateClient();
        http.BaseAddress = new Uri("https://localhost");
        using var response = body is null
            ? await http.PostAsync(new Uri(path, UriKind.Relative), null, cancellationToken)
            : await http.PostAsJsonAsync(path, body, cancellationToken);
        response.EnsureSuccessStatusCode();
        var setCookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-demo-session=", StringComparison.Ordinal));
        return setCookie.Split(';')[0];
    }

    /// <summary>The next <paramref name="method"/> notice, or <c>null</c> when none arrives within <paramref name="timeout"/>.</summary>
    public async Task<JsonElement?> NextAsync(string method, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await _received[method].Reader.ReadAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
}
