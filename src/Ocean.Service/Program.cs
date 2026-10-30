using System.Net;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Hosting.WindowsServices;
using Ocean.Core;
using Ocean.Core.Crypto;
using Ocean.Core.Media;
using Ocean.Service.Access;
using Ocean.Service.Api;
using Ocean.Service.Catalog;
using Ocean.Service.Discovery;
using Ocean.Service.Downloads;
using Ocean.Service.Net;
using Ocean.Service.Peers;
using Ocean.Service.State;

// ---------------------------------------------------------------------------------------------
// Ocean background service: discovery, catalog, encrypted peer API, loopback API for the UI.
// Runs as a Windows service (install-service.ps1) or as a plain console/background process
// (the UI starts it automatically when the service is not installed).
// ---------------------------------------------------------------------------------------------

var dataDir = OceanDefaults.DataDir;
Directory.CreateDirectory(dataDir);
var isService = WindowsServiceHelpers.IsWindowsService();

// One Ocean service per device: a second copy (e.g. started by hand while the Windows service runs) exits.
using var instance = new SingleInstance("Ocean.Service");
if (!instance.IsFirst)
{
    var msg = $"{DateTime.Now:HH:mm:ss.fff} INF Program: another Ocean service is already running on this PC; exiting.{Environment.NewLine}";
    try { Directory.CreateDirectory(Path.Combine(dataDir, "logs")); File.AppendAllText(Path.Combine(dataDir, "logs", $"service-{DateTime.Now:yyyyMMdd}.log"), msg); } catch { }
    Console.Error.WriteLine("Another Ocean service is already running on this PC.");
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
if (isService) builder.Host.UseWindowsService(o => o.ServiceName = OceanDefaults.ServiceName);

builder.Logging.ClearProviders();
if (!isService) builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(dataDir, "logs"),
    args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Information));
builder.Logging.SetMinimumLevel(args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Information);
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

var config = new ConfigStore(dataDir);
var identity = NodeIdentity.LoadOrCreate(Path.Combine(dataDir, "node.pfx"));
var localAuth = new LocalAuth();
var cfg = config.Current;

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(identity);
builder.Services.AddSingleton(localAuth);
builder.Services.AddSingleton(new HostInfo(isService));
builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<StaWorker>();
builder.Services.AddSingleton<CatalogService>();
builder.Services.AddSingleton<ThumbnailService>();
builder.Services.AddSingleton<PeerRegistry>();
builder.Services.AddSingleton<AccessService>();
builder.Services.AddSingleton<PeerClient>();
builder.Services.AddSingleton<PeerService>();
builder.Services.AddSingleton<DownloadManager>();
builder.Services.AddSingleton(new BandwidthLimiter { BytesPerSecond = cfg.UploadLimitKBps * 1024L });
builder.Services.AddHostedService<DiscoveryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DownloadManager>());
builder.Services.AddHostedService<CatalogMaintenance>();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
});

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 1 << 20;
    k.Limits.MinResponseDataRate = null; // slow players / throttled uploads are fine
    k.Limits.MinRequestBodyDataRate = null;
    // Loopback API for the UI
    k.Listen(IPAddress.Loopback, cfg.LocalPort);
    // Encrypted peer API: our self-signed node certificate + mandatory client certificate
    k.ListenAnyIP(cfg.PeerPort, l => l.UseHttps(h =>
    {
        h.ServerCertificate = identity.Certificate;
        h.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
        h.AllowAnyClientCertificate();
        h.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
        h.HandshakeTimeout = TimeSpan.FromSeconds(10);
    }));
});

var app = builder.Build();
var log = app.Services.GetRequiredService<ILogger<Program>>();

// Gate: which listener + which credential each route family requires.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    var port = ctx.Connection.LocalPort;
    var current = config.Current;
    if (path.StartsWithSegments("/p"))
    {
        var cert = ctx.Connection.ClientCertificate;
        var clientId = cert is null ? null : NodeIdentity.ComputeNodeId(cert);
        if (port != current.PeerPort || clientId is null)
        {
            ctx.Response.StatusCode = 403; return;
        }
        if (!Realm.ProofMatches(current.Secret, clientId, identity.NodeId, ctx.Request.Headers["X-Ocean-Realm"]))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new Ocean.Core.Protocol.ErrorDto { Error = "realm", Message = "Different shared secret" }, Json.Options);
            return;
        }
        ctx.Items["ocean.peer"] = PeerContext.FromRequest(ctx, clientId);
    }
    else if (path.StartsWithSegments("/api"))
    {
        var remote = ctx.Connection.RemoteIpAddress;
        if (port != current.LocalPort || remote is null || !IPAddress.IsLoopback(remote)) { ctx.Response.StatusCode = 403; return; }
        var token = ctx.Request.Headers["X-Ocean-Local"].ToString();
        if (string.IsNullOrEmpty(token)) token = ctx.Request.Query["k"].ToString();
        if (!localAuth.Matches(token)) { ctx.Response.StatusCode = 401; return; }
    }
    else
    {
        ctx.Response.StatusCode = 404; return;
    }
    try { await next(); }
    catch (System.Text.Json.JsonException ex) when (!ctx.Response.HasStarted)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new Ocean.Core.Protocol.ErrorDto { Error = "bad_json", Message = ex.Message }, Json.Options);
    }
});

PeerApi.Map(app);
LocalApi.Map(app);

// Warm up singletons that subscribe to events (access service hooks catalog lock changes, etc.)
app.Services.GetRequiredService<AccessService>();
app.Services.GetRequiredService<PeerService>();

log.LogInformation("Ocean {Version} starting as {Mode}; node {Node} ({Name}); data {Dir}; local {Local}, peers {Peer}, discovery {Disc}",
    OceanDefaults.AppVersion, isService ? "Windows service" : "background process", NodeIdentity.ShortId(identity.NodeId), cfg.NodeName,
    dataDir, cfg.LocalPort, cfg.PeerPort, cfg.DiscoveryPort);

try
{
    app.Run();
}
catch (Exception ex)
{
    log.LogCritical(ex, "fatal");
    throw;
}
