using System.Globalization;
using System.Text.Json.Nodes;
using Pillion.Contracts;

namespace Pillion.FakeHost;

/// <summary>
/// An in-memory <see cref="ISettingsService"/> with the host's key-path rules: dotted paths, numeric segments index
/// arrays (the array length appends), intermediate objects are created, and keys under <c>secrets.</c> are refused.
/// Starts with two providers matching <see cref="FakeModelService"/>'s models. <see cref="TestProviderAsync"/>
/// succeeds for any provider id in the <c>providers</c> list.
/// </summary>
internal sealed class FakeSettingsService : ISettingsService
{
    private const string InitialSettings = """
        {
          "version": 1,
          "providers": [
            { "id": "ollama", "kind": "ollama", "displayName": "Ollama (fake)", "endpoint": "http://localhost:11434", "models": [ "fake-local-coder" ], "enabled": true },
            { "id": "fake-cloud", "kind": "openai-compatible", "displayName": "Fake Cloud", "endpoint": "https://fake-cloud.invalid/v1", "models": [ "fake-cloud-chat" ], "enabled": true }
          ],
          "cloudSwitches": { "chat": true, "inlineEdit": true, "autocomplete": false, "agent": false, "selfHealing": false, "vision": true, "embedding": false },
          "privacy": { "localOnly": false, "telemetryOptIn": false, "redactForLocalProviders": false }
        }
        """;

    private readonly Lock _gate = new();

    /// <summary>Raised after a successful <see cref="SetAsync"/> with the top-level section it changed (the host's <c>settingsChanged</c>).</summary>
    public event Action<string>? SectionChanged;

    private readonly JsonObject _root = (JsonObject)JsonNode.Parse(InitialSettings)!;

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var segments = Parse(key);
        lock (_gate)
        {
            JsonNode? current = _root;
            foreach (var segment in segments)
            {
                current = current switch
                {
                    JsonObject obj => obj.TryGetPropertyValue(segment, out var next) ? next : null,
                    JsonArray array when TryIndex(segment, out var index) && index < array.Count => array[index],
                    _ => null,
                };

                if (current is null)
                {
                    break;
                }
            }

            Console.Error.WriteLine("fakehost: settings get " + key + (current is null ? " (not set)" : string.Empty));
            return Task.FromResult(current?.ToJsonString());
        }
    }

    public Task SetAsync(string key, string json, CancellationToken cancellationToken)
    {
        var segments = Parse(key);
        var value = JsonNode.Parse(json);
        lock (_gate)
        {
            JsonNode current = _root;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var segment = segments[i];
                JsonNode? next;
                if (current is JsonObject obj)
                {
                    if (!obj.TryGetPropertyValue(segment, out next) || next is null)
                    {
                        next = new JsonObject();
                        obj[segment] = next;
                    }
                }
                else if (current is JsonArray array && TryIndex(segment, out var index) && index < array.Count && array[index] is { } item)
                {
                    next = item;
                }
                else
                {
                    throw Invalid("'" + key + "' cannot be set.");
                }

                current = next;
            }

            var last = segments[^1];
            switch (current)
            {
                case JsonObject target:
                    target[last] = value;
                    break;
                case JsonArray list when TryIndex(last, out var position) && position <= list.Count:
                    if (position == list.Count)
                    {
                        list.Add(value);
                    }
                    else
                    {
                        list[position] = value;
                    }

                    break;
                default:
                    throw Invalid("'" + key + "' cannot be set.");
            }
        }

        Console.Error.WriteLine("fakehost: settings set " + key);
        SectionChanged?.Invoke(segments[0]);
        return Task.CompletedTask;
    }

    public Task<ProviderTestResult> TestProviderAsync(string providerId, CancellationToken cancellationToken)
    {
        bool known;
        lock (_gate)
        {
            known = _root["providers"] is JsonArray providers
                && providers.OfType<JsonObject>().Any(p => string.Equals((string?)p["id"], providerId, StringComparison.Ordinal));
        }

        Console.Error.WriteLine("fakehost: test provider " + providerId + ", known " + known);
        return Task.FromResult(new ProviderTestResult
        {
            ProviderId = providerId,
            IsSuccess = known,
            Message = known ? "The fake host reached provider '" + providerId + "'." : "Unknown provider '" + providerId + "'.",
        });
    }

    private static string[] Parse(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Split('.').Any(s => s.Length == 0))
        {
            throw Invalid("A settings key must not be empty or have an empty segment.");
        }

        if (key.Equals("secrets", StringComparison.OrdinalIgnoreCase) || key.StartsWith("secrets.", StringComparison.OrdinalIgnoreCase))
        {
            throw new PillionRpcException(new PillionError
            {
                Code = PillionErrorCode.PolicyDenied,
                Message = "Secrets cannot be read or written through the settings service.",
            });
        }

        return key.Split('.');
    }

    private static bool TryIndex(string segment, out int index) =>
        int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out index);

    private static ArgumentException Invalid(string message) => new(message);
}
