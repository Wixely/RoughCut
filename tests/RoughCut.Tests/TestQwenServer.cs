using System.Net;
using System.Net.Sockets;
using System.Text;

internal sealed class TestQwenServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly byte[] _wav;
    private readonly TimeSpan _synthesisDelay;
    private readonly bool _customModelLoaded;
    private readonly Task _worker;

    public TestQwenServer(byte[] wav, TimeSpan synthesisDelay = default, bool customModelLoaded = true)
    {
        _wav = wav;
        _synthesisDelay = synthesisDelay;
        _customModelLoaded = customModelLoaded;
        _listener.Start();
        Endpoint = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
        _worker = RunAsync();
    }

    public string Endpoint { get; }
    public int StatusRequests { get; private set; }
    public int SynthesisRequests { get; private set; }

    private async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await HandleAsync(client, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken token)
    {
        await using var stream = client.GetStream();
        using var request = new MemoryStream();
        var buffer = new byte[4096];
        int headerEnd;
        while ((headerEnd = FindHeaderEnd(request.GetBuffer().AsSpan(0, checked((int)request.Length)))) < 0)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0 || request.Length + read > 64 * 1024) throw new InvalidDataException("Invalid fixture HTTP request.");
            request.Write(buffer, 0, read);
        }
        var header = Encoding.ASCII.GetString(request.GetBuffer(), 0, headerEnd);
        var requestLine = header.Split("\r\n", StringSplitOptions.None)[0];
        var contentLength = 0;
        foreach (var line in header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(line[(line.IndexOf(':') + 1)..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        var bodyStart = headerEnd + 4;
        while (request.Length - bodyStart < contentLength)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) throw new InvalidDataException("Incomplete fixture HTTP request.");
            request.Write(buffer, 0, read);
        }

        byte[] body;
        string contentType;
        string status;
        if (requestLine.StartsWith("GET /api/status ", StringComparison.Ordinal))
        {
            StatusRequests++;
            body = Encoding.UTF8.GetBytes("""{"version":"0.1.0-fixture","settings":{"default_language":"English"},"engine":{"base_model_id":null,"custom_voice_model_id":"Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice","base_loaded":false,"custom_loaded":""" +
                (_customModelLoaded ? "true" : "false") + "}}");
            contentType = "application/json";
            status = "200 OK";
        }
        else if (requestLine.StartsWith("POST /v1/audio/speech ", StringComparison.Ordinal))
        {
            SynthesisRequests++;
            if (_synthesisDelay > TimeSpan.Zero) await Task.Delay(_synthesisDelay, token);
            var json = Encoding.UTF8.GetString(request.GetBuffer(), bodyStart, contentLength);
            if (!json.Contains("\"voice\":\"aiden\"", StringComparison.Ordinal) ||
                !json.Contains("\"response_format\":\"wav\"", StringComparison.Ordinal))
                throw new InvalidDataException("Qwen fixture request lost voice or WAVE format.");
            body = _wav;
            contentType = "audio/wav";
            status = "200 OK";
        }
        else
        {
            body = "not found"u8.ToArray();
            contentType = "text/plain";
            status = "404 Not Found";
        }
        var responseHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(responseHeader, token);
        await stream.WriteAsync(body, token);
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> value)
    {
        for (var index = 0; index <= value.Length - 4; index++)
            if (value[index] == 13 && value[index + 1] == 10 && value[index + 2] == 13 && value[index + 3] == 10)
                return index;
        return -1;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try { _worker.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
