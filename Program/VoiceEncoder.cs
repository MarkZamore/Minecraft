using System;
using System.IO;
using Concentus;
using Concentus.Enums;

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

    public short[] Decode(byte[] encoded)
    {
        if (encoded is null || encoded.Length == 0)
        {
            return Array.Empty<short>();
        }

        var decoded = new short[VoiceEncoder.FrameSamples * VoiceEncoder.Channels];
        var decodedSamples = _decoder.Decode(encoded, decoded, VoiceEncoder.FrameSamples, false);
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
