using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Minecraft;

public sealed class NetworkProviderCatalog
{
    private const string ResourceName = "Minecraft.NetworkProviders.json";
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions CatalogJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex IPv4Pattern = new(
        @"(?<![0-9.])(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}(?:/\d{1,2})?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IPv6Pattern = new(
        @"(?<![0-9A-Fa-f:])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?:%\d+)?(?:/\d{1,3})?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Logger _logger;
    private readonly IReadOnlyList<NetworkProviderDescriptor> _providers;
    private DateTimeOffset _lastCommandWarningUtc = DateTimeOffset.MinValue;

    public NetworkProviderCatalog(Logger logger)
    {
        _logger = logger;
        _providers = LoadProviders();
    }

    public IReadOnlyList<NetworkProviderDescriptor> Providers => _providers;

    public NetworkProviderDescriptor? MatchAdapter(string name, string description)
    {
        return _providers.FirstOrDefault(provider => provider.AdapterPatterns.Any(pattern =>
            name.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
            description.Contains(pattern, StringComparison.OrdinalIgnoreCase)));
    }

    public IReadOnlyDictionary<string, DateTimeOffset> GetLatestClientStarts()
    {
        var processStarts = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var name = NormalizeProcessName(process.ProcessName);
                    var started = process.StartTime.ToUniversalTime();
                    if (!processStarts.TryGetValue(name, out var previous) || started > previous)
                    {
                        processStarts[name] = started;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
                {
                    // Protected and exiting processes are irrelevant to provider selection.
                }
            }
        }

        var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in _providers)
        {
            var latest = provider.ClientProcesses
                .Select(NormalizeProcessName)
                .Where(processStarts.ContainsKey)
                .Select(name => processStarts[name])
                .DefaultIfEmpty(DateTimeOffset.MinValue)
                .Max();
            if (latest != DateTimeOffset.MinValue)
            {
                result[provider.Id] = latest;
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<IPAddress>> GetDynamicPeerTargetsAsync(
        NetworkEnvironmentSnapshot snapshot,
        CancellationToken token)
    {
        var localAddresses = snapshot.Endpoints
            .Select(endpoint => ParseAddress(endpoint.NetworkAddress))
            .Where(address => address is not null)
            .Cast<IPAddress>()
            .ToHashSet();
        var result = new HashSet<IPAddress>();

        foreach (var provider in _providers)
        {
            var providerEndpoints = snapshot.Endpoints
                .Where(endpoint => string.Equals(endpoint.ProviderId, provider.Id, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (providerEndpoints.Length == 0) continue;

            foreach (var source in provider.PeerSources)
            {
                var output = await TryRunPeerSourceAsync(source, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(output)) continue;
                foreach (var address in ExtractAddresses(output))
                {
                    if (!IsUsableTarget(address) || localAddresses.Contains(address)) continue;
                    if (RouteUsesEndpoint(address, providerEndpoints)) result.Add(address);
                }
            }
        }

        foreach (var address in ReadHostsFile())
        {
            if (IsUsableTarget(address) && !localAddresses.Contains(address) &&
                RouteUsesEndpoint(address, snapshot.Endpoints))
            {
                result.Add(address);
            }
        }

        var routeOutput = await TryRunCommandAsync(
            Path.Combine(Environment.SystemDirectory, "route.exe"),
            "PRINT",
            logFailures: false,
            token).ConfigureAwait(false);
        foreach (var address in ExtractHostRoutes(routeOutput))
        {
            if (IsUsableTarget(address) && !localAddresses.Contains(address) &&
                RouteUsesEndpoint(address, snapshot.Endpoints))
            {
                result.Add(address);
            }
        }

        return result.OrderBy(address => address.AddressFamily).ThenBy(address => address.ToString(), StringComparer.Ordinal).ToArray();
    }

    private async Task<string> TryRunPeerSourceAsync(NetworkPeerSourceDescriptor source, CancellationToken token)
    {
        foreach (var executable in source.ExecutableNames)
        {
            var candidate = ExpandExecutable(executable);
            var output = await TryRunCommandAsync(candidate, source.Arguments, logFailures: false, token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(output)) return output;
        }
        return string.Empty;
    }

    private async Task<string> TryRunCommandAsync(
        string executable,
        string arguments,
        bool logFailures,
        CancellationToken token)
    {
        if ((executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar)) &&
            !File.Exists(executable))
        {
            return string.Empty;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(CommandTimeout);
        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null) return string.Empty;
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            _ = await errorTask.ConfigureAwait(false);
            return output;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
            if (logFailures && DateTimeOffset.UtcNow - _lastCommandWarningUtc > TimeSpan.FromMinutes(1))
            {
                _lastCommandWarningUtc = DateTimeOffset.UtcNow;
                _logger.Warn($"Network peer source failed: {ex.Message}");
            }
            return string.Empty;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static IPAddress[] ExtractAddresses(string text)
    {
        var result = new HashSet<IPAddress>();
        foreach (Match match in IPv4Pattern.Matches(text))
        {
            var parsed = ParseAddress(match.Value);
            if (parsed is not null) result.Add(parsed);
        }
        foreach (Match match in IPv6Pattern.Matches(text))
        {
            var parsed = ParseAddress(match.Value);
            if (parsed is not null) result.Add(parsed);
        }
        return result.ToArray();
    }

    private static IPAddress[] ExtractHostRoutes(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<IPAddress>();
        var result = new HashSet<IPAddress>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 2 && columns[1] == "255.255.255.255")
            {
                var address = ParseAddress(columns[0]);
                if (address is not null) result.Add(address);
            }

            foreach (var column in columns.Where(column => column.EndsWith("/128", StringComparison.Ordinal)))
            {
                var address = ParseAddress(column);
                if (address is not null) result.Add(address);
            }
        }
        return result.ToArray();
    }

    private static IEnumerable<IPAddress> ReadHostsFile()
    {
        var hostsPath = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
        if (!File.Exists(hostsPath)) yield break;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(hostsPath);
        }
        catch
        {
            yield break;
        }

        foreach (var line in lines)
        {
            var content = line.Split('#', 2)[0].Trim();
            if (string.IsNullOrWhiteSpace(content)) continue;
            var first = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            var address = ParseAddress(first);
            if (address is not null) yield return address;
        }
    }

    private static bool RouteUsesEndpoint(IPAddress target, IEnumerable<NetworkEndpointInfo> endpoints)
    {
        var candidates = endpoints.Where(endpoint => endpoint.AddressFamily == target.AddressFamily).ToArray();
        if (candidates.Length == 0) return false;
        try
        {
            using var socket = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(target, 9));
            if (socket.LocalEndPoint is not IPEndPoint local) return false;
            return candidates.Any(endpoint =>
                ParseAddress(endpoint.NetworkAddress)?.Equals(local.Address) == true);
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static bool IsUsableTarget(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.IsIPv6Multicast)
        {
            return false;
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] is > 0 and < 224 && !(bytes[0] == 169 && bytes[1] == 254);
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv6LinkLocal;
    }

    private static IPAddress? ParseAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim().Trim('[', ']', '"', '\'', ',', ';');
        var slash = candidate.LastIndexOf('/');
        if (slash > 0) candidate = candidate[..slash];
        return IPAddress.TryParse(candidate, out var address) ? address : null;
    }

    private static string ExpandExecutable(string value)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value);
        return expanded.Replace("%ProgramFiles(x86)%", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), StringComparison.OrdinalIgnoreCase)
            .Replace("%ProgramFiles%", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeProcessName(string value) =>
        Path.GetFileNameWithoutExtension(value).Trim();

    private static List<NetworkProviderDescriptor> LoadProviders()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Embedded network provider catalog was not found.");
        var catalog = JsonSerializer.Deserialize<NetworkProviderCatalogDocument>(stream, CatalogJsonOptions)
            ?? throw new InvalidOperationException("Embedded network provider catalog is invalid.");
        if (catalog.SchemaVersion != 1 || catalog.Providers.Count == 0 ||
            catalog.Providers.Any(provider => string.IsNullOrWhiteSpace(provider.Id) || provider.AdapterPatterns.Count == 0))
        {
            throw new InvalidOperationException("Embedded network provider catalog is incomplete.");
        }
        return catalog.Providers;
    }
}

public sealed class NetworkProviderDescriptor
{
    public string Id { get; set; } = "";
    public List<string> ClientProcesses { get; set; } = [];
    public List<string> AdapterPatterns { get; set; } = [];
    public List<NetworkPeerSourceDescriptor> PeerSources { get; set; } = [];
}

public sealed class NetworkPeerSourceDescriptor
{
    public List<string> ExecutableNames { get; set; } = [];
    public string Arguments { get; set; } = "";
}

internal sealed class NetworkProviderCatalogDocument
{
    public int SchemaVersion { get; set; }
    public List<NetworkProviderDescriptor> Providers { get; set; } = [];
}
