using System;

/// <summary>Small PCM16 RIFF/WAV encoder. Metadata always describes the supplied samples.</summary>
public static class MatePcm16WavEncoder
{
    public static byte[] Encode(MatePcmUtterance utterance)
    {
        if (utterance == null) throw new ArgumentNullException(nameof(utterance));
        if (utterance.SampleRate <= 0 || utterance.Channels <= 0) throw new ArgumentException("Invalid PCM format");
        int dataLength = checked(utterance.Samples.Length * sizeof(short));
        var bytes = new byte[44 + dataLength];
        WriteAscii(bytes, 0, "RIFF"); WriteInt32(bytes, 4, 36 + dataLength); WriteAscii(bytes, 8, "WAVE");
        WriteAscii(bytes, 12, "fmt "); WriteInt32(bytes, 16, 16); WriteInt16(bytes, 20, 1);
        WriteInt16(bytes, 22, (short)utterance.Channels); WriteInt32(bytes, 24, utterance.SampleRate);
        int byteRate = checked(utterance.SampleRate * utterance.Channels * 2);
        WriteInt32(bytes, 28, byteRate); WriteInt16(bytes, 32, (short)(utterance.Channels * 2)); WriteInt16(bytes, 34, 16);
        WriteAscii(bytes, 36, "data"); WriteInt32(bytes, 40, dataLength);
        Buffer.BlockCopy(utterance.Samples, 0, bytes, 44, dataLength);
        return bytes;
    }
    static void WriteAscii(byte[] b, int o, string s) { for (int i = 0; i < s.Length; i++) b[o + i] = (byte)s[i]; }
    static void WriteInt16(byte[] b, int o, short v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
    static void WriteInt32(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24); }
}
