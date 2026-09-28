using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Treering.Cli.Tests;

/// <summary>
/// The local server runs indexers, which run other people's build scripts, and it can open windows
/// on this screen. Any web page open in the browser can send a POST to 127.0.0.1. The gate lets
/// through only JSON sent from Treering's own page. If it opens, a site someone merely visits can
/// start work on their machine.
///
/// These start the real server, in its own home folder, and knock on the sample endpoint. That one
/// sits behind the same gate as import, but runs no indexer and opens no window.
/// </summary>
public sealed class ServerGateTests : IClassFixture<ServerGateTests.Server>
{
    private readonly Server _server;

    public ServerGateTests(Server server) => _server = server;

    public sealed class Server : IAsyncLifetime
    {
        private readonly string _home = Path.Combine(Path.GetTempPath(), $"tr-home-{Guid.NewGuid():N}");
        private Process? _process;

        public int Port { get; } = FreePort();
        public HttpClient Http { get; } = new();

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public async Task InitializeAsync()
        {
            var cli = Path.Combine(AppContext.BaseDirectory, "Treering.Cli.dll");
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in new[] { cli, "serve", "--no-watch", "--port", Port.ToString() }) start.ArgumentList.Add(argument);
            start.Environment["TREERING_HOME"] = _home;
            _process = Process.Start(start)!;
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            for (var tries = 0; tries < 100; tries++)
            {
                try
                {
                    using var reply = await Http.GetAsync($"http://127.0.0.1:{Port}/api/projects");
                    if (reply.IsSuccessStatusCode) return;
                }
                catch (HttpRequestException) { /* not listening yet */ }
                await Task.Delay(100);
            }

            throw new InvalidOperationException("the server did not come up");
        }

        public Task DisposeAsync()
        {
            try { _process?.Kill(entireProcessTree: true); } catch { /* already gone */ }
            _process?.Dispose();
            Http.Dispose();
            try { Directory.Delete(_home, recursive: true); } catch { /* a temp folder left behind is harmless */ }
            return Task.CompletedTask;
        }
    }

    private Task<HttpStatusCode> PostSample(string? origin, string contentType, string body = "{}") =>
        Post("/api/sample", origin, contentType, body);

    private async Task<HttpStatusCode> Post(string path, string? origin, string contentType, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_server.Port}{path}")
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
        if (origin is not null) request.Headers.Add("Origin", origin);
        using var reply = await _server.Http.SendAsync(request);
        return reply.StatusCode;
    }

    [Fact]
    public async Task Json_from_its_own_page_gets_through()
    {
        Assert.Equal(HttpStatusCode.OK, await PostSample($"http://127.0.0.1:{_server.Port}", "application/json"));
    }

    [Fact]
    public async Task Localhost_is_its_own_page_too()
    {
        Assert.Equal(HttpStatusCode.OK, await PostSample($"http://localhost:{_server.Port}", "application/json"));
    }

    [Fact]
    public async Task Another_site_is_turned_away()
    {
        Assert.Equal(HttpStatusCode.Forbidden, await PostSample("https://shop.example", "application/json"));
    }

    [Fact]
    public async Task Its_own_host_on_another_port_is_another_site()
    {
        Assert.Equal(HttpStatusCode.Forbidden, await PostSample($"http://127.0.0.1:{_server.Port + 1}", "application/json"));
    }

    [Fact]
    public async Task A_sandboxed_page_is_turned_away()
    {
        // Sandboxed frames and file:// pages send the literal origin "null".
        Assert.Equal(HttpStatusCode.Forbidden, await PostSample("null", "application/json"));
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data")]
    public async Task A_form_a_page_could_send_without_asking_is_turned_away(string contentType)
    {
        // These three are what a browser sends cross-site without a preflight. With no Origin at all
        // the gate still wants JSON.
        Assert.Equal(HttpStatusCode.Forbidden, await PostSample(null, contentType));
    }

    [Theory]
    // Bodies that would do nothing even if they got through: a folder that is not there, and the
    // watcher switched to the off it already is.
    [InlineData("/api/import", "{\"path\": \"/no/such/folder/for/treering\"}")]
    [InlineData("/api/watch", "{\"on\": false}")]
    public async Task Every_door_that_starts_work_has_the_same_gate(string path, string body)
    {
        Assert.Equal(HttpStatusCode.Forbidden, await Post(path, "https://shop.example", "application/json", body));
    }
}
