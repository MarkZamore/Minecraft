using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Minecraft;

internal static class PortableProtocol
{
    public const int MaxJsonFrameBytes = 12 * 1024 * 1024;

    public static async Task WriteJsonAsync<T>(Stream stream, T value, JsonSerializerOptions options, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, options));
        if (bytes.Length > MaxJsonFrameBytes)
        {
            throw new InvalidDataException("Protocol JSON frame is too large.");
        }

        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        await stream.WriteAsync(length, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        var lengthBytes = new byte[4];
        await ReadExactAsync(stream, lengthBytes, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        if (length <= 0 || length > MaxJsonFrameBytes)
        {
            throw new InvalidDataException("Invalid protocol JSON frame length.");
        }

        var bytes = new byte[length];
        await ReadExactAsync(stream, bytes, token).ConfigureAwait(false);
        return bytes;
    }

    public static T? Deserialize<T>(byte[] frame, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<T>(frame, options);

    public static string ReadProtocol(byte[] frame)
    {
        using var document = JsonDocument.Parse(frame);
        return document.RootElement.TryGetProperty("protocol", out var protocol)
            ? protocol.GetString() ?? string.Empty
            : string.Empty;
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }
}
