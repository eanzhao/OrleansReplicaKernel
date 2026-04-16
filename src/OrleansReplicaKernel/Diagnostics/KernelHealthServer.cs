using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OrleansReplicaKernel.Diagnostics;

internal sealed class KernelHealthServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpListener _listener;
    private readonly Func<KernelHealthSnapshot> _snapshotProvider;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _serveLoop;

    public KernelHealthServer(
        string urlPrefix,
        Func<KernelHealthSnapshot> snapshotProvider,
        ILoggerFactory loggerFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(urlPrefix);

        UrlPrefix = urlPrefix.EndsWith("/", StringComparison.Ordinal)
            ? urlPrefix
            : urlPrefix + "/";
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
        _listener = new HttpListener();
        _listener.Prefixes.Add(UrlPrefix);
        _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger("OrleansReplicaKernel.Health");
    }

    public string UrlPrefix { get; }

    public void Start()
    {
        _listener.Start();
        _serveLoop = Task.Run(ServeAsync);
        _logger.LogInformation("Health endpoint listening on {UrlPrefix}", UrlPrefix);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();

        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        if (_serveLoop is not null)
        {
            await _serveLoop;
        }

        _shutdown.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            HttpListenerContext? context = null;

            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException) when (_shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Health endpoint accept failed.");
                await Task.Delay(TimeSpan.FromMilliseconds(25), _shutdown.Token);
                continue;
            }

            _ = Task.Run(() => HandleAsync(context), _shutdown.Token);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (!string.Equals(path, "/health", StringComparison.Ordinal)
                && !string.Equals(path, "/healthz", StringComparison.Ordinal))
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
                return;
            }

            var snapshot = _snapshotProvider();
            context.Response.StatusCode = snapshot.IsHealthy
                ? (int)HttpStatusCode.OK
                : (int)HttpStatusCode.ServiceUnavailable;
            context.Response.ContentType = "application/json";

            await JsonSerializer.SerializeAsync(
                context.Response.OutputStream,
                snapshot,
                JsonOptions,
                _shutdown.Token);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Health endpoint request failed.");
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
        }
        finally
        {
            try
            {
                context.Response.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
