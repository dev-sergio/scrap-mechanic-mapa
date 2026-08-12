using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using ScrapMap.Core.Models;

namespace ScrapMap.Desktop;

internal sealed class LanMapHost : IAsyncDisposable
{
    private const int FirstPort = 51873;
    private const int PortAttempts = 10;

    private readonly WebApplication _application;
    private readonly object _contentLock = new();
    private string _html = MapHtmlBuilder.BuildEmpty(
        "Mapa sendo preparado",
        "A versão desktop ainda está carregando o save.",
        waitForLanMap: true);
    private long _revision;
    private long _stateRevision;
    private MapViewState? _mirroredState;

    private LanMapHost(WebApplication application, int port)
    {
        _application = application;
        Port = port;
        NetworkUrls = FindNetworkUrls(port);
    }

    public int Port { get; }

    public IReadOnlyList<string> NetworkUrls { get; }

    public string PrimaryUrl => NetworkUrls.FirstOrDefault() ?? $"http://localhost:{Port}";

    public static async Task<LanMapHost> StartAsync(CancellationToken cancellationToken = default)
    {
        Exception? lastException = null;
        for (var port = FirstPort; port < FirstPort + PortAttempts; port++)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>(),
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(port));

            var application = builder.Build();
            var host = new LanMapHost(application, port);
            host.ConfigureRoutes();
            try
            {
                await application.StartAsync(cancellationToken);
                return host;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                lastException = exception;
                await application.DisposeAsync();
            }
        }

        throw new InvalidOperationException(
            $"Não foi possível iniciar o mapa de rede nas portas {FirstPort}–{FirstPort + PortAttempts - 1}.",
            lastException);
    }

    public void Publish(WorldSnapshot snapshot, TerrainOverlayData? terrain)
    {
        var nextRevision = Interlocked.Read(ref _revision) + 1;
        MapViewState? mirroredState;
        lock (_contentLock) mirroredState = _mirroredState;
        var html = MapHtmlBuilder.Build(
            snapshot,
            terrain,
            viewState: mirroredState,
            assetBaseUrl: string.Empty,
            hostRevision: nextRevision,
            presentationMode: true);
        lock (_contentLock)
        {
            _html = html;
            Interlocked.Exchange(ref _revision, nextRevision);
        }
    }

    public void UpdateViewState(MapViewState state)
    {
        lock (_contentLock)
        {
            _mirroredState = state;
            _stateRevision++;
        }
    }

    public void PublishEmpty(string title, string? detail = null)
    {
        lock (_contentLock)
        {
            _html = MapHtmlBuilder.BuildEmpty(title, detail);
            Interlocked.Increment(ref _revision);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _application.StopAsync(stopTimeout.Token);
        }
        finally
        {
            await _application.DisposeAsync();
        }
    }

    private void ConfigureRoutes()
    {
        var assetsPath = Path.Combine(AppContext.BaseDirectory, "Assets");
        if (Directory.Exists(assetsPath))
        {
            _application.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(assetsPath),
                RequestPath = "/Assets"
            });
        }

        _application.MapGet("/", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            string html;
            lock (_contentLock) html = _html;
            return Results.Text(html, "text/html", Encoding.UTF8);
        });
        _application.MapGet("/api/revision", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { revision = Interlocked.Read(ref _revision) });
        });
        _application.MapGet("/api/status", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            lock (_contentLock)
            {
                return Results.Json(new
                {
                    revision = _revision,
                    stateRevision = _stateRevision,
                    state = _mirroredState
                });
            }
        });
        _application.MapGet("/favicon.ico", () => Results.NoContent());
    }

    private static IReadOnlyList<string> FindNetworkUrls(int port)
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up)
            .Where(network => network.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Where(address => !IPAddress.IsLoopback(address))
            .Where(address => !address.GetAddressBytes().Take(2).SequenceEqual(new byte[] { 169, 254 }))
            .Distinct()
            .OrderByDescending(IsPrivateAddress)
            .ThenBy(address => address.ToString(), StringComparer.Ordinal)
            .Select(address => $"http://{address}:{port}")
            .ToArray();
        return addresses;
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || bytes[0] == 192 && bytes[1] == 168
            || bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
    }
}
