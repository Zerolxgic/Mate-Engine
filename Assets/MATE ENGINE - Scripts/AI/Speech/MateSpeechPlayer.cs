using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Unity AudioSource-backed speech player. Decodes Kokoro PCM16 WAV bytes in memory.
/// Stop()/cancel uses a monotonic epoch so a late PlayAsync cannot clear an newer cancel.
/// </summary>
public sealed class MateSpeechPlayer : MonoBehaviour, IMateSpeechPlayer
{
    AudioSource source;
    int epoch;
    public bool IsPlaying => source != null && source.isPlaying;

    void Awake()
    {
        EnsureSource();
    }

    void EnsureSource()
    {
        if (source != null) return;
        source = gameObject.GetComponent<AudioSource>();
        if (source == null) source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = false;
        source.spatialBlend = 0f;
    }

    public async Task PlayAsync(byte[] audioBytes, string format, CancellationToken cancellationToken)
    {
        EnsureSource();
        int myEpoch = Volatile.Read(ref epoch);

        if (audioBytes == null || audioBytes.Length == 0)
            throw new InvalidOperationException("empty audio");

        AudioClip clip = null;
        try
        {
            if (IsStopped(myEpoch, cancellationToken))
                throw new OperationCanceledException();

            if (!string.Equals(format, "wav", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("unsupported audio format " + (format ?? "<null>"));

            // This continuation is the same Unity synchronization-context path that previously
            // created/mutated AudioSource state. The decoder creates its AudioClip only here.
            clip = MateWavDecoder.DecodePcm16(audioBytes);

            if (IsStopped(myEpoch, cancellationToken))
                throw new OperationCanceledException();

            source.clip = clip;
            source.Play();

            while (source.isPlaying)
            {
                if (IsStopped(myEpoch, cancellationToken))
                {
                    source.Stop();
                    throw new OperationCanceledException();
                }
                await Task.Yield();
            }
        }
        finally
        {
            if (source != null && source.clip == clip)
                source.clip = null;
            if (clip != null)
                Destroy(clip);
        }
    }

    public void Stop()
    {
        Interlocked.Increment(ref epoch);
        try
        {
            if (source != null && source.isPlaying)
                source.Stop();
        }
        catch { }
    }

    bool IsStopped(int myEpoch, CancellationToken cancellationToken)
    {
        return Volatile.Read(ref epoch) != myEpoch || cancellationToken.IsCancellationRequested;
    }

    void OnDestroy()
    {
        Stop();
    }
}
