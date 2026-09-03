using System;
using UnityEngine;

/// <summary>
/// Bounded decoder for the PCM16 WAV responses used by the pinned Kokoro runtime.
/// It deliberately supports no compressed or third-party codec formats.
/// </summary>
public static class MateWavDecoder
{
    const uint UnspecifiedLength = uint.MaxValue;

    public static AudioClip DecodePcm16(byte[] wavBytes)
    {
        if (wavBytes == null || wavBytes.Length < 12)
            throw new InvalidOperationException("WAV response is too short");
        if (!HasFourCc(wavBytes, 0, "RIFF") || !HasFourCc(wavBytes, 8, "WAVE"))
            throw new InvalidOperationException("WAV response is missing RIFF/WAVE signature");

        int position = 12;
        bool foundFormat = false;
        int channels = 0;
        int sampleRate = 0;
        int blockAlign = 0;
        int bitsPerSample = 0;
        int dataOffset = -1;
        int dataLength = 0;

        while (position <= wavBytes.Length - 8)
        {
            uint declaredLength = ReadUInt32(wavBytes, position + 4);
            int payloadOffset = position + 8;
            int available = wavBytes.Length - payloadOffset;

            if (HasFourCc(wavBytes, position, "fmt "))
            {
                if (declaredLength < 16 || declaredLength > available)
                    throw new InvalidOperationException("WAV fmt chunk is invalid");

                ushort format = ReadUInt16(wavBytes, payloadOffset);
                channels = ReadUInt16(wavBytes, payloadOffset + 2);
                sampleRate = checked((int)ReadUInt32(wavBytes, payloadOffset + 4));
                blockAlign = ReadUInt16(wavBytes, payloadOffset + 12);
                bitsPerSample = ReadUInt16(wavBytes, payloadOffset + 14);

                if (format != 1)
                    throw new InvalidOperationException("unsupported WAV format " + format);
                foundFormat = true;
            }
            else if (HasFourCc(wavBytes, position, "data"))
            {
                // Kokoro's streamed WAV declares 0xFFFFFFFF for RIFF/data sizes. The
                // response is already complete here, so the remaining bytes are data.
                if (declaredLength != UnspecifiedLength && declaredLength > available)
                    throw new InvalidOperationException("WAV data chunk is truncated");

                dataOffset = payloadOffset;
                dataLength = declaredLength == UnspecifiedLength ? available : checked((int)declaredLength);
                break;
            }

            if (declaredLength > available)
                throw new InvalidOperationException("WAV chunk is truncated");

            long next = (long)payloadOffset + declaredLength + (declaredLength & 1);
            if (next > wavBytes.Length)
                throw new InvalidOperationException("WAV chunk padding is truncated");
            position = (int)next;
        }

        if (!foundFormat)
            throw new InvalidOperationException("WAV response has no fmt chunk");
        if (dataOffset < 0 || dataLength == 0)
            throw new InvalidOperationException("WAV response has no audio data");
        if (channels < 1 || channels > 2)
            throw new InvalidOperationException("unsupported WAV channel count " + channels);
        if (sampleRate < 8000 || sampleRate > 192000)
            throw new InvalidOperationException("unsupported WAV sample rate " + sampleRate);
        if (bitsPerSample != 16 || blockAlign != channels * 2 || dataLength % blockAlign != 0)
            throw new InvalidOperationException("unsupported PCM16 WAV layout");

        int sampleCount = dataLength / 2;
        int frames = dataLength / blockAlign;
        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            int offset = dataOffset + (i * 2);
            short sample = unchecked((short)(wavBytes[offset] | (wavBytes[offset + 1] << 8)));
            samples[i] = sample / 32768f;
        }

        AudioClip clip = null;
        try
        {
            clip = AudioClip.Create("MateKokoroSpeech", frames, channels, sampleRate, false);
            if (clip == null || !clip.SetData(samples, 0))
                throw new InvalidOperationException("Unity could not create PCM16 AudioClip");
            return clip;
        }
        catch
        {
            if (clip != null) UnityEngine.Object.Destroy(clip);
            throw;
        }
    }

    static bool HasFourCc(byte[] bytes, int offset, string value)
    {
        return offset >= 0 && offset + 4 <= bytes.Length &&
               bytes[offset] == value[0] && bytes[offset + 1] == value[1] &&
               bytes[offset + 2] == value[2] && bytes[offset + 3] == value[3];
    }

    static ushort ReadUInt16(byte[] bytes, int offset)
    {
        return (ushort)(bytes[offset] | (bytes[offset + 1] << 8));
    }

    static uint ReadUInt32(byte[] bytes, int offset)
    {
        return (uint)(bytes[offset] | (bytes[offset + 1] << 8) |
                      (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
    }
}
