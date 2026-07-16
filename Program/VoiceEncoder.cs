using System;
using System.IO;
using Concentus;
using Concentus.Enums;
using Concentus.Structs;

namespace Minecraft;

public sealed class VoiceEncoder : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 1;
    public const int FrameSamples = 960;
    public const int MaxPacketSize = 4000;

    private readonly IOpusEncoder _encoder;

    public VoiceEncoder()
    {
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_VOIP, TextWriter.Null);
        _encoder.UseDTX = true;
        _encoder.UseInbandFEC = true;
        _encoder.UseVBR = true;
        ConfigureNetwork(64000, 0);
    }

    public void ConfigureNetwork(int bitrate, int packetLossPercent)
    {
        _encoder.Bitrate = Math.Clamp(bitrate, 32000, 64000);
        _encoder.PacketLossPercent = Math.Clamp(packetLossPercent, 0, 30);
    }

    public byte[] Encode(short[] frame)
    {
        if (frame.Length == 0) return Array.Empty<byte>();

        var encoded = new byte[MaxPacketSize];
        var frameSamples = Math.Min(FrameSamples, frame.Length);
        var encodedLength = _encoder.Encode(frame.AsSpan(0, frameSamples), frameSamples, encoded, encoded.Length);
        if (encodedLength <= 0)
        {
            return Array.Empty<byte>();
        }

        var payload = new byte[encodedLength];
        Buffer.BlockCopy(encoded, 0, payload, 0, encodedLength);
        return payload;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}

public sealed class VoiceDecoder : IDisposable
{
    private readonly IOpusDecoder _decoder;

    public VoiceDecoder()
    {
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        _decoder = OpusCodecFactory.CreateDecoder(
            VoiceEncoder.SampleRate,
            VoiceEncoder.Channels,
            TextWriter.Null);
    }

    public static bool IsTwentyMillisecondPacket(byte[] encoded)
    {
        if (encoded is null || encoded.Length == 0) return false;
        try
        {
            return OpusPacketInfo.GetNumSamples(encoded, VoiceEncoder.SampleRate) ==
                   VoiceEncoder.FrameSamples;
        }
        catch
        {
            return false;
        }
    }

    public short[] DecodePacket(byte[] encoded)
    {
        if (!IsTwentyMillisecondPacket(encoded))
        {
            throw new InvalidDataException("Voice packet is not a 20 ms Opus frame.");
        }
        return DecodeCore(encoded, useFec: false);
    }

    public short[] DecodeFec(byte[] encoded)
    {
        if (!IsTwentyMillisecondPacket(encoded))
        {
            throw new InvalidDataException("Voice FEC packet is not a 20 ms Opus frame.");
        }
        return DecodeCore(encoded, useFec: true);
    }

    public short[] DecodeMissing() => DecodeCore(ReadOnlySpan<byte>.Empty, useFec: false);

    public void Reset() => _decoder.ResetState();

    private short[] DecodeCore(ReadOnlySpan<byte> encoded, bool useFec)
    {
        var decoded = new short[VoiceEncoder.FrameSamples * VoiceEncoder.Channels];
        var decodedSamples = _decoder.Decode(encoded, decoded, VoiceEncoder.FrameSamples, useFec);
        var outputSamples = Math.Max(0, decodedSamples) * VoiceEncoder.Channels;
        if (outputSamples <= 0 || outputSamples > decoded.Length)
        {
            return Array.Empty<short>();
        }

        if (outputSamples == decoded.Length)
        {
            return decoded;
        }

        var trimmed = new short[outputSamples];
        Buffer.BlockCopy(decoded, 0, trimmed, 0, outputSamples * sizeof(short));
        return trimmed;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
