using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Sokoni.Agent;

/// <summary>
/// Thin client for Meta Model API (Muse Spark), using the OpenAI-compatible Chat Completions endpoint.
/// Raw JSON rather than an SDK keeps tool-calling fully visible and easy to debug.
/// </summary>
public sealed class MuseClient
{
    private readonly HttpClient _http;
    private readonly MuseOptions _o;
    private readonly ILogger<MuseClient> _log;

    public MuseClient(HttpClient http, IOptions<MuseOptions> o, ILogger<MuseClient> log)
    {
        _http = http;
        _o = o.Value;
        _log = log;
        _http.Timeout = TimeSpan.FromSeconds(90);
    }

    /// <summary>Sends the conversation and returns the assistant message object (content and/or tool_calls).</summary>
    public async Task<JsonObject> ChatAsync(JsonArray messages, JsonArray? tools, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_o.ApiKey))
            throw new InvalidOperationException("MetaModelApi:ApiKey is not set.");

        var body = new JsonObject
        {
            ["model"] = _o.Model,
            ["messages"] = messages.DeepClone()
        };
        if (tools is { Count: > 0 })
        {
            body["tools"] = tools.DeepClone();
            body["tool_choice"] = "auto";
        }
        var json = body.ToJsonString();

        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _o.BaseUrl.TrimEnd('/') + "/chat/completions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _o.ApiKey);

            using var res = await _http.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);

            if (res.IsSuccessStatusCode)
            {
                var root = JsonNode.Parse(text)!.AsObject();
                var message = root["choices"]?[0]?["message"]?.AsObject()
                    ?? throw new InvalidOperationException($"Unexpected Model API response: {Truncate(text)}");
                LogUsage(root);
                return (JsonObject)message.DeepClone();
            }

            var retryable = res.StatusCode == HttpStatusCode.TooManyRequests || (int)res.StatusCode >= 500;
            if (retryable && attempt < 3)
            {
                _log.LogWarning("Model API {Status}, retrying (attempt {Attempt}).", (int)res.StatusCode, attempt);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
                continue;
            }
            throw new HttpRequestException($"Model API error {(int)res.StatusCode}: {Truncate(text)}");
        }
    }

    private void LogUsage(JsonObject root)
    {
        var usage = root["usage"];
        if (usage is not null)
            _log.LogInformation("Muse Spark usage: prompt={P} completion={C}",
                usage["prompt_tokens"]?.ToString(), usage["completion_tokens"]?.ToString());
    }

    private static string Truncate(string s) => s.Length > 500 ? s[..500] + "…" : s;
}
