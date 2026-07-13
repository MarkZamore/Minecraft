using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Minecraft;

public sealed class SkinService : IAsyncDisposable
{
    public const string ProtocolName = "MinecraftPortableSkin";
    public const int ProtocolVersion = 1;
    public const int HttpPort = 35658;
    private const int MaxSkinBytes = 128 * 1024 * 1024;
    private readonly AppPaths _paths;
    private readonly Logger _logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, SkinAsset> _assets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SkinPeerDescriptor> _peers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _fetches = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private TcpListener? _httpListener;
    private Task? _httpTask;
    private SkinAsset? _localAsset;
    private string _localUuid = "";
    private string _localSourceState = "";

    public SkinService(AppPaths paths, Logger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    public SkinAnnouncement GetAnnouncement(AppSettings settings, string identityId)
    {
        RefreshLocalSkin(settings, identityId);
        var asset = _localAsset;
        return asset is null
            ? new SkinAnnouncement(false, "", "classic")
            : new SkinAnnouncement(true, asset.Sha256, asset.Model);
    }

    public SkinAnnouncement SelectLocalSkin(AppSettings settings, string identityId, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("Selected skin file was not found.", fullPath);
        var asset = ReadSkin(fullPath, info);
        _localUuid = NormalizeUuid(identityId);
        if (_localUuid.Length == 0) throw new InvalidDataException("Local player UUID is invalid.");
        settings.SkinPath = fullPath;
        _localAsset = asset;
        _localSourceState = BuildSourceState(fullPath, info);
        _assets[_localUuid] = asset;
        WriteRegistry();
        return new SkinAnnouncement(true, asset.Sha256, asset.Model);
    }

    public void RefreshLocalSkin(AppSettings settings, string identityId)
    {
        var normalizedUuid = NormalizeUuid(identityId);
        var path = settings.SkinPath?.Trim();
        FileInfo? info = null;
        if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)) info = new FileInfo(path);
        var sourceState = BuildSourceState(path ?? "", info);
        if (string.Equals(_localUuid, normalizedUuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_localSourceState, sourceState, StringComparison.Ordinal))
        {
            return;
        }
        var previousUuid = _localUuid;
        _localUuid = normalizedUuid;
        _localSourceState = sourceState;
        if (previousUuid.Length > 0 && !string.Equals(previousUuid, _localUuid, StringComparison.OrdinalIgnoreCase))
        {
            _assets.TryRemove(previousUuid, out _);
        }
        if (string.IsNullOrWhiteSpace(path) || info is null || !info.Exists)
        {
            var changed = _localAsset is not null || _assets.ContainsKey(_localUuid);
            _localAsset = null;
            _assets.TryRemove(_localUuid, out _);
            if (changed || !File.Exists(_paths.SkinRegistryFile)) WriteRegistry();
            return;
        }

        try
        {
            var current = _localAsset;
            if (current is not null && string.Equals(current.SourcePath, path, StringComparison.OrdinalIgnoreCase) &&
                current.SizeBytes == info.Length && current.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks)
            {
                return;
            }

            var asset = ReadSkin(path, info);
            _localAsset = asset;
            _assets[_localUuid] = asset;
            WriteRegistry();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
        {
            _localAsset = null;
            _assets.TryRemove(_localUuid, out _);
            WriteRegistry();
            _logger.Warn($"Selected skin is unavailable; the default skin will be used: {ex.Message}");
        }
    }

    public async Task StartAsync(CancellationToken token = default)
    {
        if (_httpTask is not null) return;
        var listener = new TcpListener(IPAddress.Loopback, HttpPort);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            _logger.Warn($"Local skin endpoint could not start on 127.0.0.1:{HttpPort}: {ex.Message}");
            return;
        }

        _httpListener = listener;
        _httpTask = HttpLoopAsync(listener, _shutdown.Token);
        await Task.CompletedTask;
    }

    public void ObservePeer(PeerViewModel peer)
    {
        var uuid = NormalizeUuid(peer.IdentityId);
        if (uuid.Length == 0) return;
        if (!peer.IsSkinAvailable || !IsSha256(peer.SkinSha256))
        {
            _peers.TryRemove(uuid, out _);
            _assets.TryRemove(uuid, out _);
            WriteRegistry();
            return;
        }

        var descriptor = new SkinPeerDescriptor(
            uuid,
            peer.SkinSha256.ToUpperInvariant(),
            NormalizeModel(peer.SkinModel),
            peer.GetCandidateIps().ToArray());
        var metadataChanged = !_peers.TryGetValue(uuid, out var previousDescriptor) ||
                              previousDescriptor.Sha256 != descriptor.Sha256 ||
                              previousDescriptor.Model != descriptor.Model ||
                              !previousDescriptor.Addresses.SequenceEqual(descriptor.Addresses, StringComparer.OrdinalIgnoreCase);
        _peers[uuid] = descriptor;
        if (_assets.TryGetValue(uuid, out var cached) &&
            string.Equals(cached.Sha256, descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            if (metadataChanged) WriteRegistry();
            return;
        }

        _assets.TryRemove(uuid, out _);
        WriteRegistry();
        _ = FetchPeerSkinAsync(descriptor, _shutdown.Token);
    }

    public string PrepareRegistry(AppSettings settings, LocalIdentityContext identity)
    {
        RefreshLocalSkin(settings, identity.IdentityId);
        WriteRegistry();
        return _paths.SkinRegistryFile;
    }

    public async Task HandleIncomingAsync(Stream stream, byte[] initialFrame, CancellationToken token)
    {
        var request = PortableProtocol.Deserialize<SkinRequest>(initialFrame, _jsonOptions);
        if (request is null || request.Protocol != ProtocolName || request.ProtocolVersion != ProtocolVersion ||
            !Guid.TryParse(request.PlayerUuid, out var requestedUuid) || !IsSha256(request.Sha256))
        {
            throw new InvalidDataException("Skin request is invalid.");
        }

        var uuid = requestedUuid.ToString("D");
        if (!_assets.TryGetValue(uuid, out var asset) ||
            !string.Equals(asset.Sha256, request.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            await PortableProtocol.WriteJsonAsync(stream, new SkinResponse
            {
                Protocol = ProtocolName,
                ProtocolVersion = ProtocolVersion,
                Ok = false,
                Message = "skin unavailable"
            }, _jsonOptions, token).ConfigureAwait(false);
            return;
        }

        await PortableProtocol.WriteJsonAsync(stream, new SkinResponse
        {
            Protocol = ProtocolName,
            ProtocolVersion = ProtocolVersion,
            Ok = true,
            PlayerUuid = uuid,
            Sha256 = asset.Sha256,
            Model = asset.Model,
            SizeBytes = asset.Bytes.LongLength
        }, _jsonOptions, token).ConfigureAwait(false);
        await stream.WriteAsync(asset.Bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private async Task FetchPeerSkinAsync(SkinPeerDescriptor descriptor, CancellationToken token)
    {
        var fetchKey = descriptor.PlayerUuid + ":" + descriptor.Sha256;
        if (!_fetches.TryAdd(fetchKey, 0)) return;
        try
        {
            foreach (var endpoint in descriptor.Addresses)
            {
                if (!IPAddress.TryParse(endpoint, out var address)) continue;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    using var client = new TcpClient();
                    await client.ConnectAsync(address, WorldTransferService.TransferPort, timeout.Token).ConfigureAwait(false);
                    await using var stream = client.GetStream();
                    await PortableProtocol.WriteJsonAsync(stream, new SkinRequest
                    {
                        Protocol = ProtocolName,
                        ProtocolVersion = ProtocolVersion,
                        PlayerUuid = descriptor.PlayerUuid,
                        Sha256 = descriptor.Sha256
                    }, _jsonOptions, timeout.Token).ConfigureAwait(false);
                    var responseFrame = await PortableProtocol.ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false);
                    var response = PortableProtocol.Deserialize<SkinResponse>(responseFrame, _jsonOptions);
                    if (response is null || response.Protocol != ProtocolName || response.ProtocolVersion != ProtocolVersion ||
                        !response.Ok || response.SizeBytes <= 0 || response.SizeBytes > MaxSkinBytes ||
                        !string.Equals(response.Sha256, descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var bytes = new byte[checked((int)response.SizeBytes)];
                    await ReadExactAsync(stream, bytes, timeout.Token).ConfigureAwait(false);
                    var hash = Convert.ToHexString(SHA256.HashData(bytes));
                    if (!string.Equals(hash, descriptor.Sha256, StringComparison.OrdinalIgnoreCase) || !IsPng(bytes))
                    {
                        throw new InvalidDataException("Received skin failed integrity validation.");
                    }

                    _assets[descriptor.PlayerUuid] = new SkinAsset(
                        bytes,
                        hash,
                        NormalizeModel(response.Model),
                        "",
                        bytes.LongLength,
                        0);
                    WriteRegistry();
                    return;
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidDataException)
                {
                    if (token.IsCancellationRequested) return;
                    _logger.Warn($"Peer skin request from {endpoint} failed: {ex.Message}");
                }
            }
        }
        finally
        {
            _fetches.TryRemove(fetchKey, out _);
        }
    }

    private async Task HttpLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                _ = ServeHttpClientAsync(client, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warn($"Local skin endpoint failed: {ex.Message}");
            }
        }
    }

    private async Task ServeHttpClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var header = await ReadHttpHeaderAsync(stream, timeout.Token).ConfigureAwait(false);
                var firstLine = header.Split("\r\n", StringSplitOptions.None)[0];
                var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || parts[0] != "GET")
                {
                    await WriteHttpResponseAsync(stream, 400, "text/plain", Encoding.UTF8.GetBytes("Bad Request"), timeout.Token).ConfigureAwait(false);
                    return;
                }

                var path = parts[1].Split('?', 2)[0].Trim('/').Split('/');
                if (path.Length != 3 || path[0] != "skin" || !Guid.TryParse(path[1], out var uuid) || !IsSha256(path[2]))
                {
                    await WriteHttpResponseAsync(stream, 404, "text/plain", Array.Empty<byte>(), timeout.Token).ConfigureAwait(false);
                    return;
                }

                var key = uuid.ToString("D");
                if ((!_assets.TryGetValue(key, out var asset) || !string.Equals(asset.Sha256, path[2], StringComparison.OrdinalIgnoreCase)) &&
                    _peers.TryGetValue(key, out var descriptor) &&
                    string.Equals(descriptor.Sha256, path[2], StringComparison.OrdinalIgnoreCase))
                {
                    await FetchPeerSkinAsync(descriptor, timeout.Token).ConfigureAwait(false);
                    for (var attempt = 0; attempt < 20 && !_assets.ContainsKey(key) &&
                         _fetches.ContainsKey(key + ":" + descriptor.Sha256); attempt++)
                    {
                        await Task.Delay(100, timeout.Token).ConfigureAwait(false);
                    }
                    _assets.TryGetValue(key, out asset);
                }
                if (asset is null || !string.Equals(asset.Sha256, path[2], StringComparison.OrdinalIgnoreCase))
                {
                    await WriteHttpResponseAsync(stream, 404, "text/plain", Array.Empty<byte>(), timeout.Token).ConfigureAwait(false);
                    return;
                }

                await WriteHttpResponseAsync(stream, 200, "image/png", asset.Bytes, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                if (!token.IsCancellationRequested) _logger.Warn($"Local skin response failed: {ex.Message}");
            }
        }
    }

    private void WriteRegistry()
    {
        try
        {
            var entries = new Dictionary<string, (string Hash, string Model)>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _peers)
            {
                entries[pair.Key] = (pair.Value.Sha256, pair.Value.Model);
            }
            foreach (var pair in _assets)
            {
                entries[pair.Key] = (pair.Value.Sha256, pair.Value.Model);
            }
            var lines = entries
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => string.Join('|',
                    pair.Key,
                    pair.Value.Hash,
                    pair.Value.Model,
                    $"http://127.0.0.1:{HttpPort}/skin/{pair.Key}/{pair.Value.Hash}"));
            var contents = string.Join(Environment.NewLine, lines);
            if (File.Exists(_paths.SkinRegistryFile) &&
                string.Equals(File.ReadAllText(_paths.SkinRegistryFile), contents, StringComparison.Ordinal))
            {
                return;
            }
            AtomicFile.WriteAllText(_paths.SkinRegistryFile, contents);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"Skin registry could not be updated: {ex.Message}");
        }
    }

    private static SkinAsset ReadSkin(string path, FileInfo info)
    {
        if (info.Length is <= 0 or > MaxSkinBytes) throw new InvalidDataException("Skin PNG has an unsupported size.");
        var bytes = File.ReadAllBytes(path);
        if (!TryReadPngDimensions(bytes, out var width, out var height) || width < 64 || width % 64 != 0 ||
            (height != width && height * 2 != width))
        {
            throw new InvalidDataException("Skin must be a standard or HD Minecraft PNG.");
        }

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var model = height == width && width <= 4096 && DetectSlim(bytes, width) ? "slim" : "classic";
        return new SkinAsset(bytes, hash, model, path, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static string BuildSourceState(string path, FileInfo? info)
    {
        if (string.IsNullOrWhiteSpace(path)) return "none";
        if (info is null || !info.Exists) return path + "|missing";
        return path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
    }

    private static bool DetectSlim(byte[] bytes, int width)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var source = decoder.Frames[0];
            BitmapSource bitmap = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var stride = checked(bitmap.PixelWidth * 4);
            var pixels = new byte[checked(stride * bitmap.PixelHeight)];
            bitmap.CopyPixels(pixels, stride, 0);
            var scale = width / 64;
            var transparentGuard = RegionIsTransparent(pixels, stride, 54 * scale, 20 * scale, 2 * scale, 12 * scale) &&
                                   RegionIsTransparent(pixels, stride, 46 * scale, 52 * scale, 2 * scale, 12 * scale);
            var adjacentVisible = RegionHasVisiblePixel(pixels, stride, 53 * scale, 20 * scale, scale, 12 * scale) ||
                                  RegionHasVisiblePixel(pixels, stride, 45 * scale, 52 * scale, scale, 12 * scale);
            return transparentGuard && adjacentVisible;
        }
        catch
        {
            return false;
        }
    }

    private static bool RegionIsTransparent(byte[] pixels, int stride, int x, int y, int width, int height)
    {
        for (var row = y; row < y + height; row++)
        for (var column = x; column < x + width; column++)
        {
            if (pixels[row * stride + column * 4 + 3] != 0) return false;
        }
        return true;
    }

    private static bool RegionHasVisiblePixel(byte[] pixels, int stride, int x, int y, int width, int height)
    {
        for (var row = y; row < y + height; row++)
        for (var column = x; column < x + width; column++)
        {
            if (pixels[row * stride + column * 4 + 3] != 0) return true;
        }
        return false;
    }

    private static bool TryReadPngDimensions(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!IsPng(bytes) || bytes.Length < 24 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
        width = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(16, 4));
        height = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(20, 4));
        return width > 0 && height > 0;
    }

    private static bool IsPng(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

    private static bool IsSha256(string? value) =>
        value?.Length == 64 && value.All(Uri.IsHexDigit);

    private static string NormalizeUuid(string? value) =>
        Guid.TryParse(value, out var uuid) && uuid != Guid.Empty ? uuid.ToString("D") : "";

    private static string NormalizeModel(string? value) =>
        string.Equals(value, "slim", StringComparison.OrdinalIgnoreCase) ? "slim" : "classic";

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private static async Task<string> ReadHttpHeaderAsync(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>(512);
        var one = new byte[1];
        while (bytes.Count < 16 * 1024)
        {
            var read = await stream.ReadAsync(one, token).ConfigureAwait(false);
            if (read == 0) break;
            bytes.Add(one[0]);
            var count = bytes.Count;
            if (count >= 4 && bytes[count - 4] == 13 && bytes[count - 3] == 10 && bytes[count - 2] == 13 && bytes[count - 1] == 10)
            {
                return Encoding.ASCII.GetString(bytes.ToArray());
            }
        }
        throw new InvalidDataException("HTTP request header is invalid.");
    }

    private static async Task WriteHttpResponseAsync(Stream stream, int status, string contentType, byte[] body, CancellationToken token)
    {
        var reason = status == 200 ? "OK" : status == 404 ? "Not Found" : "Bad Request";
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        if (body.Length > 0) await stream.WriteAsync(body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _httpListener?.Stop();
        if (_httpTask is not null)
        {
            try { await _httpTask.ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
        }
        _shutdown.Dispose();
    }

    private sealed record SkinAsset(
        byte[] Bytes,
        string Sha256,
        string Model,
        string SourcePath,
        long SizeBytes,
        long LastWriteUtcTicks);

    private sealed record SkinPeerDescriptor(string PlayerUuid, string Sha256, string Model, IReadOnlyList<string> Addresses);
}

public sealed record SkinAnnouncement(bool IsAvailable, string Sha256, string Model);

public sealed class SkinRequest
{
    public string Protocol { get; set; } = SkinService.ProtocolName;
    public int ProtocolVersion { get; set; } = SkinService.ProtocolVersion;
    public string PlayerUuid { get; set; } = "";
    public string Sha256 { get; set; } = "";
}

public sealed class SkinResponse
{
    public string Protocol { get; set; } = SkinService.ProtocolName;
    public int ProtocolVersion { get; set; } = SkinService.ProtocolVersion;
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public string PlayerUuid { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Model { get; set; } = "classic";
    public long SizeBytes { get; set; }
}
