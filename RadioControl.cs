using Windows.Devices.Radios;

namespace Glint;

/// <summary>Turns the Wi-Fi and Bluetooth radios on and off, the same switches as in Quick Settings.</summary>
internal static class RadioControl
{
    static volatile Dictionary<RadioKind, bool> _state = new();

    /// <summary>Raised (on a background thread) when a refresh finds a radio in a different state than before.</summary>
    public static event Action? Changed;

    /// <summary>Last known state, or null if there is no such radio (or it hasn't been read yet).</summary>
    public static bool? IsOn(RadioKind kind) => _state.TryGetValue(kind, out bool on) ? on : null;

    /// <summary>Reads the radio states in the background so command rows can say "currently on".</summary>
    public static void Refresh() => Task.Run(async () =>
    {
        try
        {
            var states = new Dictionary<RadioKind, bool>();
            foreach (var radio in await Radio.GetRadiosAsync())
                states[radio.Kind] = states.GetValueOrDefault(radio.Kind) || radio.State == RadioState.On;
            bool changed = states.Count != _state.Count || states.Any(kv => _state.GetValueOrDefault(kv.Key) != kv.Value);
            _state = states;
            if (changed) Changed?.Invoke();
        }
        catch { }
    });

    /// <summary>Returns null on success, otherwise a message explaining what went wrong.</summary>
    public static async Task<string?> SetAsync(RadioKind kind, bool on)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed)
                return "Windows didn't allow Glint to control radios.";

            var radios = (await Radio.GetRadiosAsync()).Where(r => r.Kind == kind).ToList();
            if (radios.Count == 0) return $"No {Name(kind)} adapter was found.";

            foreach (var radio in radios)
            {
                var result = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
                if (result != RadioAccessStatus.Allowed) return $"Windows refused the change ({result}).";
            }
            Refresh();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public static string Name(RadioKind kind) => kind == RadioKind.WiFi ? "Wi-Fi" : "Bluetooth";
}
