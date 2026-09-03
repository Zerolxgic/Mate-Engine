using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Kokoro-FastAPI TTS provider (pinned external runtime; sentence-level synthesis).
/// </summary>
public sealed class MateKokoroTtsProvider : IMateTtsProvider
{
    const string LogPrefix = "[MateKokoro]";

    readonly Func<MateSpeechConfig> configProvider;

    public string ProviderId => MateSpeechConfig.DefaultProviderId;

    public MateKokoroTtsProvider(Func<MateSpeechConfig> configProvider = null)
    {
        this.configProvider = configProvider ?? (() => MateSpeechConfig.LoadOrCreateTemplate());
    }

    public async Task<MateTtsSynthesisResult> SynthesizeAsync(MateTtsRequest request, CancellationToken cancellationToken)
    {
        var cfg = configProvider() ?? new MateSpeechConfig();
        string url = cfg.SpeechUrl;
        string voice = string.IsNullOrWhiteSpace(request.VoiceId) ? cfg.selectedVoice : request.VoiceId;
        if (string.IsNullOrWhiteSpace(voice)) voice = MateSpeechConfig.DefaultVoice;

        string json = BuildSpeechJson(request.Text, voice);
        byte[] body = Encoding.UTF8.GetBytes(json);

        using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
        {
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.timeout = cfg.GetTimeoutSeconds();

            var op = req.SendWebRequest();
            while (!op.isDone)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    try { req.Abort(); } catch { }
                    cancellationToken.ThrowIfCancellationRequested();
                }
                await Task.Yield();
            }

            if (cancellationToken.IsCancellationRequested)
                cancellationToken.ThrowIfCancellationRequested();

            if (req.result != UnityWebRequest.Result.Success)
            {
                string err = DescribeError(req);
                Debug.LogWarning($"{LogPrefix} synthesize failed: {err}");
                return new MateTtsSynthesisResult(request.TurnId, request.ChunkIndex, err);
            }

            byte[] audio = req.downloadHandler?.data;
            if (audio == null || audio.Length == 0)
                return new MateTtsSynthesisResult(request.TurnId, request.ChunkIndex, "empty audio response");

            return new MateTtsSynthesisResult(request.TurnId, request.ChunkIndex, audio, "wav");
        }
    }

    public async Task<IReadOnlyList<MateTtsVoice>> ListVoicesAsync(CancellationToken cancellationToken)
    {
        var cfg = configProvider() ?? new MateSpeechConfig();
        string url = cfg.VoicesUrl;

        using (var req = UnityWebRequest.Get(url))
        {
            req.timeout = Mathf.Clamp(cfg.GetTimeoutSeconds(), 5, 30);
            var op = req.SendWebRequest();
            while (!op.isDone)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    try { req.Abort(); } catch { }
                    cancellationToken.ThrowIfCancellationRequested();
                }
                await Task.Yield();
            }

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"{LogPrefix} voices failed: {DescribeError(req)}");
                return Array.Empty<MateTtsVoice>();
            }

            string text = req.downloadHandler?.text ?? "";
            return ParseVoices(text);
        }
    }

    public async Task<MateTtsConnectionStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        var cfg = configProvider() ?? new MateSpeechConfig();
        string url = cfg.VoicesUrl;
        using (var req = UnityWebRequest.Get(url))
        {
            req.timeout = 5;
            var op = req.SendWebRequest();
            while (!op.isDone)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    try { req.Abort(); } catch { }
                    return MateTtsConnectionStatus.Unavailable;
                }
                await Task.Yield();
            }

            if (req.result != UnityWebRequest.Result.Success)
                return MateTtsConnectionStatus.Unavailable;
            return MateTtsConnectionStatus.Connected;
        }
    }

    public static string BuildSpeechJson(string input, string voice)
    {
        var sb = new StringBuilder(128 + (input?.Length ?? 0));
        sb.Append("{\"model\":\"kokoro\",\"input\":");
        AppendJsonString(sb, input ?? "");
        sb.Append(",\"voice\":");
        AppendJsonString(sb, voice ?? MateSpeechConfig.DefaultVoice);
        sb.Append(",\"response_format\":\"wav\"}");
        return sb.ToString();
    }

    public static IReadOnlyList<MateTtsVoice> ParseVoices(string json)
    {
        var list = new List<MateTtsVoice>();
        if (string.IsNullOrEmpty(json)) return list;

        // Kokoro returns {"voices":[{"id":"af_bella",...}, ...]} or {"voices":["af_bella", ...]}.
        // Keep parsing bounded and dependency-free (no Newtonsoft required on this path).
        int voicesKey = json.IndexOf("\"voices\"", StringComparison.OrdinalIgnoreCase);
        if (voicesKey < 0) return list;
        int arrStart = json.IndexOf('[', voicesKey);
        int arrEnd = json.IndexOf(']', arrStart + 1);
        if (arrStart < 0 || arrEnd < 0) return list;

        string arr = json.Substring(arrStart + 1, arrEnd - arrStart - 1);
        if (arr.IndexOf('{') >= 0)
        {
            int pos = 0;
            while (pos < arr.Length)
            {
                int idKey = arr.IndexOf("\"id\"", pos, StringComparison.OrdinalIgnoreCase);
                if (idKey < 0) break;
                int colon = arr.IndexOf(':', idKey);
                if (colon < 0) break;
                int q1 = arr.IndexOf('"', colon + 1);
                if (q1 < 0) break;
                int q2 = arr.IndexOf('"', q1 + 1);
                if (q2 < 0) break;
                string id = arr.Substring(q1 + 1, q2 - q1 - 1);
                if (!string.IsNullOrWhiteSpace(id))
                    list.Add(new MateTtsVoice(id));
                pos = q2 + 1;
            }
        }
        else
        {
            int pos = 0;
            while (pos < arr.Length)
            {
                int q1 = arr.IndexOf('"', pos);
                if (q1 < 0) break;
                int q2 = arr.IndexOf('"', q1 + 1);
                if (q2 < 0) break;
                string id = arr.Substring(q1 + 1, q2 - q1 - 1);
                if (!string.IsNullOrWhiteSpace(id))
                    list.Add(new MateTtsVoice(id));
                pos = q2 + 1;
            }
        }

        return list;
    }

    static void AppendJsonString(StringBuilder sb, string value)
    {
        sb.Append('"');
        if (!string.IsNullOrEmpty(value))
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
        }
        sb.Append('"');
    }

    static string DescribeError(UnityWebRequest req)
    {
        if (req == null) return "null request";
        string body = req.downloadHandler != null ? req.downloadHandler.text : null;
        if (!string.IsNullOrEmpty(body))
        {
            if (body.Length > 200) body = body.Substring(0, 200) + "...";
            return $"HTTP {(long)req.responseCode}: {body}";
        }
        return string.IsNullOrEmpty(req.error) ? $"HTTP {(long)req.responseCode}" : req.error;
    }
}
