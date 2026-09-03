using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Routes detached technical-chat Send/Cancel through the accepted MateOpenAICharacter path.
/// Does not duplicate provider request logic. Owns speech config bridge for Voice UI.
/// </summary>
public static class MateTechnicalChatController
{
    const string LogPrefix = "[MateTechnicalChat]";

    /// <summary>
    /// Optional test seam: when set, invoked instead of MateOpenAICharacter.Chat after validation.
    /// Production leaves this null.
    /// </summary>
    public static Action<string> SendEntryHookForTests;

    static IReadOnlyList<MateTtsVoice> cachedVoices = Array.Empty<MateTtsVoice>();
    static bool voicesRefreshInFlight;

    public static MateOpenAICharacter FindAdapter()
    {
#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
        return UnityEngine.Object.FindFirstObjectByType<MateOpenAICharacter>(FindObjectsInactive.Include);
#else
        return UnityEngine.Object.FindObjectOfType<MateOpenAICharacter>();
#endif
    }

    public static void SendFromUi(string text)
    {
        TrySendFromUi(text, out _);
    }

    /// <summary>
    /// Validate and enter the shared conversation path. Returns false when rejected
    /// (blank, busy, missing adapter) without starting a provider turn.
    /// </summary>
    public static bool TrySendFromUi(string text, out string error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "empty";
            Debug.LogWarning($"{LogPrefix} Ignoring blank send.");
            return false;
        }

        if (MateChatPresentationModel.Session.HasRunningTurn)
        {
            error = "busy";
            Debug.LogWarning($"{LogPrefix} Send blocked — a turn is already running.");
            return false;
        }

        string trimmed = text.TrimEnd();
        Debug.Log($"{LogPrefix} controller invoked");

        if (SendEntryHookForTests != null)
        {
            SendEntryHookForTests(trimmed);
            return true;
        }

        var adapter = FindAdapter();
        if (adapter == null)
        {
            error = "no adapter";
            Debug.LogError($"{LogPrefix} MateOpenAICharacter not found. Enable OpenAI backend (MateOpenAIConfig.enabled).");
            return false;
        }

        Debug.Log($"{LogPrefix} MateOpenAICharacter resolved");

        // Fire-and-forget on Unity sync context; Chat is async and updates presentation model internally.
        _ = SendAsync(adapter, trimmed);
        return true;
    }

    static async Task SendAsync(MateOpenAICharacter adapter, string text)
    {
        try
        {
            Debug.Log($"{LogPrefix} Chat entered");
            await adapter.Chat(text, callback: null, completionCallback: null, addToHistory: true);
        }
        catch (Exception ex)
        {
            Debug.LogError($"{LogPrefix} Chat exception: {ex.Message}");
        }
    }

    public static void CancelFromUi()
    {
        var adapter = FindAdapter();
        if (adapter == null)
        {
            Debug.LogWarning($"{LogPrefix} Cancel: no adapter.");
            return;
        }
        adapter.CancelRequests();
    }

    public static MateSpeechOrchestrator Speech => MateSpeechService.Current;

    public static string BuildSpeechStateJson()
    {
        var orch = Speech;
        if (orch == null)
        {
            var cfg = MateSpeechConfig.LoadOrCreateTemplate();
            return "{\"speechOutputEnabled\":" + (cfg.speechOutputEnabled ? "true" : "false") +
                   ",\"providerId\":\"" + Escape(cfg.providerId) +
                   "\",\"kokoroEndpoint\":\"" + Escape(cfg.kokoroEndpoint) +
                   "\",\"selectedVoice\":\"" + Escape(cfg.selectedVoice) +
                   "\",\"status\":\"Unknown\",\"lifecycle\":\"Idle\",\"lastError\":\"\",\"queued\":0,\"voices\":[]}";
        }
        return orch.ToJsonState(cachedVoices);
    }

    public static bool TryApplySpeechConfig(string body, out string error)
    {
        error = null;
        var orch = Speech;
        if (orch == null)
        {
            error = "speech service unavailable";
            return false;
        }

        var current = orch.Config;
        var next = new MateSpeechConfig
        {
            speechOutputEnabled = current.speechOutputEnabled,
            providerId = current.providerId,
            kokoroEndpoint = current.kokoroEndpoint,
            selectedVoice = current.selectedVoice,
            timeoutSeconds = current.timeoutSeconds,
        };

        bool? enabled = ExtractJsonBool(body, "speechOutputEnabled");
        string voice = ExtractJsonString(body, "selectedVoice");
        string endpoint = ExtractJsonString(body, "kokoroEndpoint");
        string provider = ExtractJsonString(body, "providerId");

        if (enabled.HasValue)
            next.speechOutputEnabled = enabled.Value;
        if (!string.IsNullOrWhiteSpace(voice))
            next.selectedVoice = voice.Trim();
        if (!string.IsNullOrWhiteSpace(endpoint))
            next.kokoroEndpoint = endpoint.Trim();
        if (!string.IsNullOrWhiteSpace(provider))
            next.providerId = provider.Trim();

        orch.ApplyConfig(next, save: true);
        return true;
    }

    public static void RequestVoiceRefresh()
    {
        if (voicesRefreshInFlight) return;
        voicesRefreshInFlight = true;
        _ = RefreshVoicesAsync();
    }

    static async Task RefreshVoicesAsync()
    {
        try
        {
            var orch = Speech;
            if (orch == null) return;
            cachedVoices = await orch.ListVoicesAsync();
            await orch.RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"{LogPrefix} voice refresh failed: {ex.Message}");
        }
        finally
        {
            voicesRefreshInFlight = false;
        }
    }

    public static void TestVoiceFromUi()
    {
        var orch = Speech;
        if (orch == null) return;
        _ = orch.TestVoiceAsync();
    }

    public static IReadOnlyList<MateTtsVoice> CachedVoicesForTests
    {
        get => cachedVoices;
        set => cachedVoices = value ?? Array.Empty<MateTtsVoice>();
    }

    /// <summary>
    /// Optional: mirror a bubble-originated user message is automatic via MateOpenAICharacter.Chat hooks.
    /// </summary>
    public static void EnsureHostAndOpen()
    {
        var host = MateTechnicalChatHost.Ensure();
        host.OpenOrFocusWindow();
    }

    static string ExtractJsonString(string json, string key)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
        string pattern = "\"" + key + "\"";
        int idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        int colon = json.IndexOf(':', idx + pattern.Length);
        if (colon < 0) return null;
        int q1 = json.IndexOf('"', colon + 1);
        if (q1 < 0) return null;
        int q2 = q1 + 1;
        var sb = new System.Text.StringBuilder();
        while (q2 < json.Length)
        {
            char c = json[q2];
            if (c == '\\' && q2 + 1 < json.Length)
            {
                sb.Append(json[q2 + 1]);
                q2 += 2;
                continue;
            }
            if (c == '"') break;
            sb.Append(c);
            q2++;
        }
        return sb.ToString();
    }

    static bool? ExtractJsonBool(string json, string key)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
        string pattern = "\"" + key + "\"";
        int idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        int colon = json.IndexOf(':', idx + pattern.Length);
        if (colon < 0) return null;
        string tail = json.Substring(colon + 1).TrimStart();
        if (tail.StartsWith("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (tail.StartsWith("false", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }

    static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
    }
}
