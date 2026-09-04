using System;
using System.Threading;
using System.Threading.Tasks;

public enum MateSpeechInputState { Disabled, Idle, Capturing, Transcribing, Submitting, Unavailable, Error }
public enum MateAsrConnectionStatus { Unknown, Configured, Connected, Unavailable }

public sealed class MatePcmUtterance
{
    public readonly short[] Samples;
    public readonly int SampleRate;
    public readonly int Channels;
    public MatePcmUtterance(short[] samples, int sampleRate, int channels = 1)
    {
        Samples = samples ?? Array.Empty<short>();
        SampleRate = sampleRate;
        Channels = channels;
    }
}

public sealed class MateAsrResult
{
    public readonly string Transcript;
    public readonly string Error;
    public bool Success => Error == null;
    public MateAsrResult(string transcript, string error = null) { Transcript = transcript; Error = error; }
}

public interface IMateMicrophoneCapture
{
    bool IsCapturing { get; }
    string ActiveDeviceName { get; }
    bool Begin(int targetSampleRate, out string error);
    bool TryFinalize(out MatePcmUtterance utterance, out string error);
    void Cancel();
}

public interface IMateAsrProvider
{
    Task<MateAsrResult> TranscribeAsync(MatePcmUtterance utterance, CancellationToken cancellationToken);
    Task<MateAsrConnectionStatus> ProbeAsync(CancellationToken cancellationToken);
}
