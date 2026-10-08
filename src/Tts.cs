using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Jplus;

/// <summary>
/// tts.py — Fish Audio synthesis, one request per sentence chunk.
///
/// The response is streamed so time-to-first-byte can be measured; the chunk is
/// returned whole because the browser decodes one complete MP3 per chunk.
/// </summary>
public static partial class Tts
{
    public static readonly ILogger Log = Py.Log("jarvis.tts");

    public const string FishTtsUrl = "https://api.fish.audio/v1/tts";

    /// <summary>The one shared client (httpx.AsyncClient). Its own timeout is off:
    /// `timeout` is applied per operation below, as httpx does, so a long reply
    /// that keeps streaming is never cut off mid-chunk.</summary>
    public static readonly HttpClient SharedClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    public class SynthResult
    {
        public byte[] Audio { get; set; }
        public double FirstByteSec { get; set; }
        public double TotalSec { get; set; }

        public SynthResult(byte[] audio, double firstByteSec, double totalSec)
        {
            Audio = audio;
            FirstByteSec = firstByteSec;
            TotalSec = totalSec;
        }
    }

    /// <summary>One chunk of speech as mp3, or null on any failure (logged).
    /// `client` defaults to <see cref="SharedClient"/>.</summary>
    public static async Task<SynthResult?> SynthesizeChunk(string? text, string apiKey, string voiceId,
        HttpClient? client = null, string latency = "balanced", double timeout = 15.0)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0 || string.IsNullOrEmpty(apiKey)) return null;
        client ??= SharedClient;
        var t0 = Py.Monotonic();
        double? first = null;
        using var buf = new MemoryStream();
        var per = TimeSpan.FromSeconds(timeout);
        try
        {
            var body = new JsonObject
            {
                ["text"] = text,
                ["reference_id"] = voiceId,
                ["format"] = "mp3",
                ["mp3_bitrate"] = 128,
                ["latency"] = latency,
            }.ToJsonString();
            using var req = new HttpRequestMessage(HttpMethod.Post, FishTtsUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var cts = new CancellationTokenSource(per);
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if ((int)resp.StatusCode != 200)
            {
                Log.LogError("TTS {Status} for {Text}", (int)resp.StatusCode, Speech.Repr(text.Length > 40 ? text[..40] : text));
                return null;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            var part = new byte[16 * 1024];
            while (true)
            {
                cts.CancelAfter(per);                     // httpx's timeout is per read, not total
                int n = await stream.ReadAsync(part, cts.Token);
                if (n == 0) break;
                first ??= Py.Monotonic() - t0;
                buf.Write(part, 0, n);
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
        {
            Log.LogError("TTS error: {Error}", e is OperationCanceledException ? "timed out" : e.Message);
            return null;
        }
        if (buf.Length == 0) return null;
        return new SynthResult(buf.ToArray(), first ?? 0.0, Py.Monotonic() - t0);
    }
}
