using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public enum MateSpeechLifecycleState
{
    Idle = 0,
    Synthesizing = 1,
    Speaking = 2,
}

public enum MateTtsConnectionStatus
{
    Unknown = 0,
    Connected = 1,
    Unavailable = 2,
}

public sealed class MateTtsVoice
{
    public string Id { get; }
    public string Name { get; }

    public MateTtsVoice(string id, string name = null)
    {
        Id = id ?? "";
        Name = string.IsNullOrEmpty(name) ? Id : name;
    }
}

public sealed class MateTtsRequest
{
    public Guid TurnId { get; }
    public int ChunkIndex { get; }
    public string Text { get; }
    public string VoiceId { get; }

    public MateTtsRequest(Guid turnId, int chunkIndex, string text, string voiceId)
    {
        TurnId = turnId;
        ChunkIndex = chunkIndex;
        Text = text ?? "";
        VoiceId = voiceId ?? "";
    }
}

public sealed class MateTtsSynthesisResult
{
    public Guid TurnId { get; }
    public int ChunkIndex { get; }
    public byte[] AudioBytes { get; }
    public string Format { get; }
    public bool Success { get; }
    public string Error { get; }

    public MateTtsSynthesisResult(Guid turnId, int chunkIndex, byte[] audioBytes, string format)
    {
        TurnId = turnId;
        ChunkIndex = chunkIndex;
        AudioBytes = audioBytes ?? Array.Empty<byte>();
        Format = format ?? "wav";
        Success = AudioBytes.Length > 0;
        Error = null;
    }

    public MateTtsSynthesisResult(Guid turnId, int chunkIndex, string error)
    {
        TurnId = turnId;
        ChunkIndex = chunkIndex;
        AudioBytes = Array.Empty<byte>();
        Format = "";
        Success = false;
        Error = error ?? "unknown error";
    }
}

/// <summary>Small Mate-owned TTS provider seam. Kokoro is Slice 7's only implementation.</summary>
public interface IMateTtsProvider
{
    string ProviderId { get; }
    Task<MateTtsSynthesisResult> SynthesizeAsync(MateTtsRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<MateTtsVoice>> ListVoicesAsync(CancellationToken cancellationToken);
    Task<MateTtsConnectionStatus> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>Playback ownership seam (Unity AudioSource or test double).</summary>
public interface IMateSpeechPlayer
{
    bool IsPlaying { get; }
    Task PlayAsync(byte[] audioBytes, string format, CancellationToken cancellationToken);
    void Stop();
}
