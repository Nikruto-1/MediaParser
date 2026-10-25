using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

public record FixtureResponse(int Status, string Body, string ContentType, int BodyDelayMs = 0);

public sealed class FixtureServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentBag<Task> clients = new();
    private readonly ConcurrentDictionary<string, int> hits = new();
    private readonly Task loop;
    public Func<string, int, FixtureResponse> Route { get; set; } = (_, _) => new(200, "", "text/html");
    public string Url { get; }

    public FixtureServer()
    {
        listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
        loop = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(stop.Token);
                    clients.Add(Serve(client));
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                string line = await reader.ReadLineAsync(stop.Token) ?? "";
                if (line.Length == 0) return;
                string path = line.Split(' ')[1];
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))) { }
                var response = Route(path, hits.AddOrUpdate(path, 1, (_, value) => value + 1));
                byte[] body = Encoding.UTF8.GetBytes(response.Body);
                string header = $"HTTP/1.1 {response.Status} Fixture\r\nContent-Type: {response.ContentType}; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), stop.Token);
                if (response.BodyDelayMs > 0) await Task.Delay(response.BodyDelayMs, stop.Token);
                await stream.WriteAsync(body, stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (IOException) { /* Client cancellation and browser speculative connections are normal. */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await loop;
        await Task.WhenAll(clients);
        listener.Stop();
        stop.Dispose();
    }
}
