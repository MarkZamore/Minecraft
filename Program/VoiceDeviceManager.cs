using NAudio.CoreAudioApi;

namespace Minecraft;

public sealed record VoiceAudioDevice(string Id, string Name);

public sealed class VoiceDeviceManager : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();

    public IReadOnlyList<VoiceAudioDevice> GetInputDevices() => GetDevices(DataFlow.Capture);

    public IReadOnlyList<VoiceAudioDevice> GetOutputDevices() => GetDevices(DataFlow.Render);

    public VoiceAudioDevice? FindById(IEnumerable<VoiceAudioDevice> devices, string? deviceId)
    {
        var available = devices.ToArray();
        return available.FirstOrDefault(device =>
                   string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase)) ??
               available.FirstOrDefault();
    }

    public MMDevice OpenInputDevice(string? deviceId) => OpenDevice(DataFlow.Capture, deviceId);

    public MMDevice OpenOutputDevice(string? deviceId) => OpenDevice(DataFlow.Render, deviceId);

    private VoiceAudioDevice[] GetDevices(DataFlow flow)
    {
        var defaultId = TryGetDefaultDeviceId(flow);
        var endpoints = _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        var devices = new List<VoiceAudioDevice>(endpoints.Count);
        foreach (var device in endpoints)
        {
            try
            {
                devices.Add(new VoiceAudioDevice(device.ID, NormalizeName(device.FriendlyName)));
            }
            finally
            {
                device.Dispose();
            }
        }

        return devices
            .OrderByDescending(device => string.Equals(device.Id, defaultId, StringComparison.OrdinalIgnoreCase))
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private MMDevice OpenDevice(DataFlow flow, string? deviceId)
    {
        if (!string.IsNullOrWhiteSpace(deviceId) &&
            !deviceId.StartsWith("input:", StringComparison.OrdinalIgnoreCase) &&
            !deviceId.StartsWith("output:", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var selected = _enumerator.GetDevice(deviceId);
                if (selected.State == DeviceState.Active)
                {
                    return selected;
                }
                selected.Dispose();
            }
            catch
            {
            }
        }

        foreach (var role in new[] { Role.Communications, Role.Multimedia, Role.Console })
        {
            try
            {
                if (_enumerator.HasDefaultAudioEndpoint(flow, role))
                {
                    return _enumerator.GetDefaultAudioEndpoint(flow, role);
                }
            }
            catch
            {
            }
        }

        var endpoints = _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        string? firstId = null;
        foreach (var endpoint in endpoints)
        {
            try
            {
                firstId ??= endpoint.ID;
            }
            finally
            {
                endpoint.Dispose();
            }
        }

        if (firstId is null)
        {
            throw new InvalidOperationException(flow == DataFlow.Capture
                ? "No microphone input device found."
                : "No audio output device found.");
        }
        return _enumerator.GetDevice(firstId);
    }

    private string? TryGetDefaultDeviceId(DataFlow flow)
    {
        foreach (var role in new[] { Role.Communications, Role.Multimedia, Role.Console })
        {
            try
            {
                if (!_enumerator.HasDefaultAudioEndpoint(flow, role)) continue;
                using var device = _enumerator.GetDefaultAudioEndpoint(flow, role);
                return device.ID;
            }
            catch
            {
            }
        }
        return null;
    }

    private static string NormalizeName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Audio device" : name.Trim();

    public void Dispose()
    {
        _enumerator.Dispose();
        GC.SuppressFinalize(this);
    }
}
