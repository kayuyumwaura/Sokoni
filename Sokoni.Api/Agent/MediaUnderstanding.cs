using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Concentus.Oggfile;
using Concentus.Structs;
using Microsoft.Extensions.Options;

namespace Sokoni.Agent;

/// <summary>
/// Voice notes → Muse Voice Transcribe. Photos → Muse Spark vision.
/// Both return plain text that goes into the conversation like any typed message.
/// </summary>
public sealed class MediaUnderstanding
{
    private readonly HttpClient _http;
    private readonly MuseClient _muse;
    private readonly MuseOptions _o;
    private readonly ILogger<MediaUnderstanding> _log;

    public MediaUnderstanding(HttpClient http, MuseClient muse, IOptions<MuseOptions> o, ILogger<MediaUnderstanding> log)
    {
        _http = http;
        _muse = muse;
        _o = o.Value;
        _log = log;
        _http.Timeout = TimeSpan.FromSeconds(120);
    }

    public bool VoiceEnabled => !string.IsNullOrWhiteSpace(_o.TranscribeModel);
    public bool VisionEnabled => _o.VisionEnabled;

    /// <summary>Returns the transcript, or null if the audio couldn't be read or nothing was said.</summary>
    public async Task<string?> TranscribeAsync(byte[] audio, string? mimeType, CancellationToken ct)
    {
        var wav = AudioConverter.ToWav(audio, mimeType);
        if (wav is null)
        {
            _log.LogWarning("Unsupported audio format {Mime}", mimeType);
            return null;
        }

        var settings = new JsonObject
        {
            ["mode"] = "PUSH_TO_TALK",
            ["model"] = _o.TranscribeModel,
            ["audioEncoding"] = "WAV"
        };
        if (_o.TranscribeLanguageBias is { Length: > 0 })
            settings["languageBias"] = new JsonArray(_o.TranscribeLanguageBias.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray());

        using var form = new MultipartFormDataContent();
        var requestPart = new StringContent(settings.ToJsonString(), Encoding.UTF8);
        requestPart.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(requestPart, "request");
        var audioPart = new ByteArrayContent(wav);
        audioPart.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audioPart, "audio", "voice-note.wav");

        using var req = new HttpRequestMessage(HttpMethod.Post, _o.BaseUrl.TrimEnd('/') + "/asr/transcribe") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _o.ApiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var res = await _http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            _log.LogError("Transcription failed {Status}: {Body}", (int)res.StatusCode, body.Length > 400 ? body[..400] : body);
            return null;
        }

        var transcript = JsonNode.Parse(body)?["transcript"]?.ToString()?.Trim();
        _log.LogInformation("Voice note transcribed ({Ms} ms audio): {Text}", JsonNode.Parse(body)?["audioDurationMs"], transcript);
        return string.IsNullOrWhiteSpace(transcript) ? null : transcript;
    }

    /// <summary>One-sentence description of what's in a customer's photo, aimed at matching it to the catalogue.</summary>
    public async Task<string?> DescribeImageAsync(byte[] image, string? mimeType, string? caption, CancellationToken ct)
    {
        var mime = string.IsNullOrWhiteSpace(mimeType) ? "image/jpeg" : mimeType.Split(';')[0].Trim();
        var dataUrl = $"data:{mime};base64,{Convert.ToBase64String(image)}";

        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "system",
                ["content"] = "You help a shop assistant understand photos customers send on WhatsApp. " +
                              "In one or two short sentences, say what product is shown: type, colour or shade, size, and any brand or label text you can read. " +
                              "If it's a screenshot of a product post, say so and describe the product. If it's not a product, say briefly what it is. No guesses about price."
            },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = string.IsNullOrWhiteSpace(caption) ? "What is in this photo?" : $"Customer's caption: {caption}" },
                    new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = dataUrl } }
                }
            }
        };

        try
        {
            var reply = await _muse.ChatAsync(messages, null, ct);
            var text = reply["content"]?.ToString()?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Image description failed");
            return null;
        }
    }
}

/// <summary>
/// WhatsApp voice notes are OGG/Opus; Muse Voice Transcribe wants mono 16-bit PCM WAV at 16 or 24 kHz.
/// Pure C# (Concentus), so it works on shared hosting with no ffmpeg.
/// </summary>
public static class AudioConverter
{
    private const int MaxSeconds = 5 * 60;

    public static byte[]? ToWav(byte[] audio, string? mimeType)
    {
        var mime = (mimeType ?? "").ToLowerInvariant();

        if (IsWav(audio)) return audio; // assume the sender already used a supported WAV format
        if (mime.Contains("ogg") || mime.Contains("opus") || IsOgg(audio)) return OggOpusToWav(audio);
        return null; // mp4/aac/mp3 not handled; agent asks the customer to type instead
    }

    private static byte[] OggOpusToWav(byte[] ogg)
    {
        // Decode at 48 kHz mono (Opus downmixes stereo for us), then halve to 24 kHz.
        var decoder = OpusDecoder.Create(48000, 1);
        using var input = new MemoryStream(ogg);
        var reader = new OpusOggReadStream(decoder, input);

        var samples48 = new List<short>(48000 * 30);
        while (reader.HasNextPacket && samples48.Count < 48000 * MaxSeconds)
        {
            var packet = reader.DecodeNextPacket();
            if (packet is not null) samples48.AddRange(packet);
        }

        // Average each pair: a cheap low-pass + decimation, fine for speech.
        var samples24 = new short[samples48.Count / 2];
        for (var i = 0; i < samples24.Length; i++)
            samples24[i] = (short)((samples48[2 * i] + samples48[2 * i + 1]) / 2);

        return WriteWav(samples24, 24000);
    }

    private static byte[] WriteWav(short[] samples, int sampleRate)
    {
        const short channels = 1, bits = 16;
        var dataBytes = samples.Length * 2;
        using var ms = new MemoryStream(44 + dataBytes);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);                               // PCM
        w.Write(channels);
        w.Write(sampleRate);
        w.Write(sampleRate * channels * bits / 8);       // byte rate
        w.Write((short)(channels * bits / 8));           // block align
        w.Write(bits);
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }

    private static bool IsWav(byte[] b) => b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'A';
    private static bool IsOgg(byte[] b) => b.Length > 4 && b[0] == 'O' && b[1] == 'g' && b[2] == 'g' && b[3] == 'S';
}
