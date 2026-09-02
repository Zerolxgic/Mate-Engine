using System;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Routes detached technical-chat Send/Cancel through the accepted MateOpenAICharacter path.
/// Does not duplicate provider request logic.
/// </summary>
public static class MateTechnicalChatController
{
    const string LogPrefix = "[MateTechnicalChat]";

    /// <summary>
    /// Optional test seam: when set, invoked instead of MateOpenAICharacter.Chat after validation.
    /// Production leaves this null.
    /// </summary>
    public static Action<string> SendEntryHookForTests;

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

    /// <summary>
    /// Optional: mirror a bubble-originated user message is automatic via MateOpenAICharacter.Chat hooks.
    /// </summary>
    public static void EnsureHostAndOpen()
    {
        var host = MateTechnicalChatHost.Ensure();
        host.OpenOrFocusWindow();
    }
}
