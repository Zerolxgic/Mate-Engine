using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Deterministic in-memory TTS provider for EditMode verification.</summary>
public sealed class MateFakeTtsProvider : IMateTtsProvider
{
    public string ProviderId => "fake";

    public bool FailNext { get; set; }
    public string FailMessage { get; set; } = "fake provider failure";
    public int SynthesizeCalls { get; private set; }
    public List<MateTtsRequest> Requests { get; } = new List<MateTtsRequest>();
    public Func<MateTtsRequest, CancellationToken, Task> BeforeSynthesize;

    /// <summary>When set, synthesis waits until the token is cancelled or this delay elapses.</summary>
    public int ArtificialDelayMs { get; set; }

    public Task<MateTtsSynthesisResult> SynthesizeAsync(MateTtsRequest request, CancellationToken cancellationToken)
    {
        return SynthesizeInternal(request, cancellationToken);
    }

    async Task<MateTtsSynthesisResult> SynthesizeInternal(MateTtsRequest request, CancellationToken cancellationToken)
    {
        SynthesizeCalls++;
        Requests.Add(request);
        if (BeforeSynthesize != null)
            await BeforeSynthesize(request, cancellationToken);

        if (ArtificialDelayMs > 0)
            await Task.Delay(ArtificialDelayMs, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (FailNext)
        {
            FailNext = false;
            return new MateTtsSynthesisResult(request.TurnId, request.ChunkIndex, FailMessage);
        }

        // Minimal valid-looking WAV header + silence payload for decode-agnostic tests.
        byte[] bytes = BuildTinyWav();
        return new MateTtsSynthesisResult(request.TurnId, request.ChunkIndex, bytes, "wav");
    }

    public Task<IReadOnlyList<MateTtsVoice>> ListVoicesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<MateTtsVoice> voices = new List<MateTtsVoice>
        {
            new MateTtsVoice("fake_a", "Fake A"),
            new MateTtsVoice("fake_b", "Fake B"),
        };
        return Task.FromResult(voices);
    }

    public Task<MateTtsConnectionStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(FailNext ? MateTtsConnectionStatus.Unavailable : MateTtsConnectionStatus.Connected);
    }

    static byte[] BuildTinyWav()
    {
        // 8-byte "RIFF" stub is enough for fake player; real Unity player is not used in verify.
        return new byte[]
        {
            (byte)'R', (byte)'I', (byte)'F', (byte)'F',
            36, 0, 0, 0,
            (byte)'W', (byte)'A', (byte)'V', (byte)'E',
            (byte)'f', (byte)'m', (byte)'t', (byte)' ',
            16, 0, 0, 0,
            1, 0, 1, 0,
            0x22, 0x56, 0, 0,
            0x44, 0xAC, 0, 0,
            2, 0, 16, 0,
            (byte)'d', (byte)'a', (byte)'t', (byte)'a',
            0, 0, 0, 0
        };
    }
}

/// <summary>Records play/stop without Unity AudioSource.</summary>
public sealed class MateFakeSpeechPlayer : IMateSpeechPlayer
{
    public bool IsPlaying { get; private set; }
    public int PlayCount { get; private set; }
    public int StopCount { get; private set; }
    public List<(Guid turnId, int chunkIndex)> PlayedJobs { get; } = new List<(Guid, int)>();
    public List<byte[]> PlayedAudio { get; } = new List<byte[]>();
    public Func<CancellationToken, Task> OnPlay;
    public bool RejectPlay { get; set; }

    Guid lastTurn;
    int lastChunk;

    public void RememberJob(Guid turnId, int chunkIndex)
    {
        lastTurn = turnId;
        lastChunk = chunkIndex;
    }

    public async Task PlayAsync(byte[] audioBytes, string format, CancellationToken cancellationToken)
    {
        if (RejectPlay)
            throw new InvalidOperationException("fake playback failure");

        PlayCount++;
        PlayedAudio.Add(audioBytes ?? Array.Empty<byte>());
        PlayedJobs.Add((lastTurn, lastChunk));
        IsPlaying = true;
        try
        {
            if (OnPlay != null)
                await OnPlay(cancellationToken);
            else
                await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            IsPlaying = false;
        }
    }

    public void Stop()
    {
        StopCount++;
        IsPlaying = false;
    }
}
