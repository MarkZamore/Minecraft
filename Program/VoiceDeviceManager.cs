using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NAudio.Wave;

namespace Minecraft;

public sealed record VoiceAudioDevice(string Id, string Name, int WaveInDeviceIndex, int WaveOutDeviceIndex);

public sealed partial class VoiceDeviceManager
{
    public IReadOnlyList<VoiceAudioDevice> GetInputDevices()
    {
        var devices = new List<VoiceAudioDevice>();
        for (var index = 0; index < WaveIn.DeviceCount; index++)
        {
            var capabilities = WaveIn.GetCapabilities(index);
            devices.Add(new VoiceAudioDevice(
                $"input:{index}",
                CleanDeviceName(capabilities.ProductName),
                index,
                -1));
        }

        return devices;
    }

    public IReadOnlyList<VoiceAudioDevice> GetOutputDevices()
    {
        var devices = new List<VoiceAudioDevice>();
        for (var index = 0; index < WaveOut.DeviceCount; index++)
        {
            var capabilities = WaveOut.GetCapabilities(index);
            devices.Add(new VoiceAudioDevice(
                $"output:{index}",
                CleanDeviceName(capabilities.ProductName),
                -1,
                index));
        }

        return devices;
    }

    public VoiceAudioDevice? FindById(IEnumerable<VoiceAudioDevice> devices, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return devices.FirstOrDefault();
        }

        return devices.FirstOrDefault(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase))
               ?? devices.FirstOrDefault();
    }

    public static string GetFallbackInputId(int count) => count > 0 ? $"input:0" : "";
    public static string GetFallbackOutputId(int count) => count > 0 ? $"output:0" : "";

    private static string CleanDeviceName(string? rawName)
    {
        var original = string.IsNullOrWhiteSpace(rawName) ? "Устройство" : rawName.Trim();
        var name = BracketContentRegex().Replace(original, " ");
        name = PrefixRegex().Replace(name, "");
        name = SuffixRegex().Replace(name, "");
        name = SeparatorRegex().Replace(name, " ");
        name = name.Trim(' ', '-', ':', ';', ',', '.', '_');
        return string.IsNullOrWhiteSpace(name) ? original.Trim() : name;
    }

    [GeneratedRegex(@"\s*[\(\[\{][^\)\]\}]*[\)\]\}]\s*")]
    private static partial Regex BracketContentRegex();

    [GeneratedRegex(@"^\s*(?:набор\s+микрофонов|микрофон|наушники|гарнитура|динамики|speakers?|microphone|headphones?|headset|line\s+in)\s*[-:–—]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex PrefixRegex();

    [GeneratedRegex(@"\s*[-:–—]?\s*(?:вход|выход|input|output)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SuffixRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex SeparatorRegex();
}
