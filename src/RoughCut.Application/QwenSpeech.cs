using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using RoughCut.Core;
using RoughCut.Media;

namespace RoughCut.Application;

public sealed record VoiceSynthesisOutput(byte[] Wav, string Runtime);

public interface IVoiceSynthesizer
{
    Task<VoiceSynthesisOutput> SynthesizeAsync(VoiceMapping mapping, string text,
        CancellationToken cancellationToken = default);
}

public sealed class QwenSpeechClient : IVoiceSynthesizer, IDisposable
{
    private const int MaxStatusBytes = 64 * 1024;
    private readonly HttpClient _http;
    private readonly Uri _serviceUri;
    private readonly string? _apiKey;
    private readonly TimeSpan _timeout;

    public QwenSpeechClient(string endpoint, string? apiKey = null, TimeSpan? timeout = null,
        HttpMessageHandler? handler = null)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !uri.IsLoopback || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Qwen endpoint must be an absolute loopback HTTP(S) service URL.");
        _serviceUri = new(uri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/", UriKind.Absolute);
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(30))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Qwen timeout must be between zero and 30 minutes.");
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<VoiceSynthesisOutput> SynthesizeAsync(VoiceMapping mapping, string text,
        CancellationToken cancellationToken = default)
    {
        if (mapping.Provider != "qwen-tts") throw new ArgumentException("Qwen client requires a qwen-tts voice mapping.");
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096) throw new ArgumentException("Synthesis text must contain 1 to 4096 characters.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            var status = await ReadStatusAsync(mapping, timeout.Token);
            using var body = new MemoryStream();
            using (var writer = new Utf8JsonWriter(body))
            {
                writer.WriteStartObject();
                writer.WriteString("model", "qwen-tts");
                writer.WriteString("input", text);
                writer.WriteString("voice", mapping.Voice);
                writer.WriteString("response_format", "wav");
                writer.WriteEndObject();
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_serviceUri, "v1/audio/speech"))
            {
                Content = new ByteArrayContent(body.ToArray())
            };
            request.Content.Headers.ContentType = new("application/json");
            AddAuthorization(request);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Qwen synthesis failed with HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentType?.MediaType is not "audio/wav" and not "audio/x-wav")
                throw new InvalidDataException("Qwen response is not PCM WAVE audio.");
            var wav = await ReadBoundedAsync(response.Content, WaveAudio.MaxBytes, timeout.Token);
            WaveAudio.Inspect(wav);
            return new(wav, $"faster-qwen-tts-aio@{status.Version}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Qwen synthesis exceeded the configured timeout.");
        }
    }

    private async Task<QwenStatus> ReadStatusAsync(VoiceMapping mapping, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_serviceUri, "api/status"));
        AddAuthorization(request);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Qwen status failed with HTTP {(int)response.StatusCode}.");
        var bytes = await ReadBoundedAsync(response.Content, MaxStatusBytes, token);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var version = root.GetProperty("version").GetString();
        var language = root.GetProperty("settings").GetProperty("default_language").GetString();
        var engine = root.GetProperty("engine");
        var baseModel = engine.GetProperty("base_model_id").GetString();
        var customModel = engine.GetProperty("custom_voice_model_id").GetString();
        var baseLoaded = engine.GetProperty("base_loaded").GetBoolean();
        var customLoaded = engine.GetProperty("custom_loaded").GetBoolean();
        if (string.IsNullOrWhiteSpace(version)) throw new InvalidDataException("Qwen status omitted its runtime version.");
        if (!string.Equals(language, mapping.Language, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Qwen service language does not match the voice mapping.");
        var matchingModelLoaded = string.Equals(baseModel, mapping.Model, StringComparison.Ordinal) && baseLoaded ||
            string.Equals(customModel, mapping.Model, StringComparison.Ordinal) && customLoaded;
        if (!matchingModelLoaded) throw new InvalidOperationException("Qwen service model does not match the loaded voice mapping model.");
        return new(version);
    }

    private void AddAuthorization(HttpRequestMessage request)
    {
        if (_apiKey is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes, CancellationToken token)
    {
        if (content.Headers.ContentLength is > 0 and var length && length > maximumBytes)
            throw new InvalidDataException("Qwen response exceeds its byte limit.");
        await using var input = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await input.ReadAsync(buffer, token);
            if (read == 0) break;
            if (output.Length + read > maximumBytes) throw new InvalidDataException("Qwen response exceeds its byte limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    public void Dispose() => _http.Dispose();

    private sealed record QwenStatus(string Version);
}
