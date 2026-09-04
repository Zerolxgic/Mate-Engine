using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>Only Slice 9 ASR provider: Speaches/OpenAI-compatible multipart transcription.</summary>
public sealed class MateSpeachesAsrProvider : IMateAsrProvider
{
    readonly Func<MateSpeechInputConfig> config;
    [Serializable] sealed class TranscriptionResponse { public string text; }
    public MateSpeachesAsrProvider(Func<MateSpeechInputConfig> config) { this.config = config; }

    public async Task<MateAsrResult> TranscribeAsync(MatePcmUtterance utterance, CancellationToken cancellationToken)
    {
        var cfg = config?.Invoke() ?? new MateSpeechInputConfig();
        try
        {
            var form = new WWWForm();
            form.AddBinaryData("file", MatePcm16WavEncoder.Encode(utterance), "mate-utterance.wav", "audio/wav");
            form.AddField("model", cfg.modelId ?? MateSpeechInputConfig.DefaultModelId);
            form.AddField("language", cfg.language ?? MateSpeechInputConfig.DefaultLanguage);
            using (var request = UnityWebRequest.Post(cfg.TranscriptionsUrl, form))
            {
                request.timeout = 60;
                var op = request.SendWebRequest();
                while (!op.isDone)
                {
                    if (cancellationToken.IsCancellationRequested) { request.Abort(); cancellationToken.ThrowIfCancellationRequested(); }
                    await Task.Yield();
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (request.result != UnityWebRequest.Result.Success)
                    return new MateAsrResult(null, "HTTP " + request.responseCode + ": " + (request.error ?? "ASR unavailable"));
                var parsed = JsonUtility.FromJson<TranscriptionResponse>(request.downloadHandler?.text ?? "");
                if (parsed == null || parsed.text == null) return new MateAsrResult(null, "malformed ASR response");
                return new MateAsrResult(parsed.text);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new MateAsrResult(null, ex.Message); }
    }

    public async Task<MateAsrConnectionStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        var cfg = config?.Invoke() ?? new MateSpeechInputConfig();
        try
        {
            using (var request = UnityWebRequest.Get(MateSpeechInputConfig.NormalizeBaseUrl(cfg.baseUrl) + "/health"))
            {
                request.timeout = 3; var op = request.SendWebRequest();
                while (!op.isDone) { if (cancellationToken.IsCancellationRequested) { request.Abort(); cancellationToken.ThrowIfCancellationRequested(); } await Task.Yield(); }
                return request.result == UnityWebRequest.Result.Success ? MateAsrConnectionStatus.Connected : MateAsrConnectionStatus.Unavailable;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { return MateAsrConnectionStatus.Unavailable; }
    }
}
