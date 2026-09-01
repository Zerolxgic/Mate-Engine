using System;
using LLMUnity;
using LLMUnitySamples;
using UnityEngine;

/// <summary>
/// Runtime bootstrap: when MateOpenAIConfig.enabled is true, swap ChatBot onto MateOpenAICharacter
/// before ChatBot.Start() without editing the main scene or vendored LLMUnity sources.
/// </summary>
public static class MateOpenAIChatBootstrap
{
    const string LogPrefix = "[MateOpenAI]";
    static bool attempted;
    static bool activated;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AfterSceneLoad()
    {
        if (attempted) return;
        attempted = true;

        if (!Application.isPlaying) return;

        try
        {
            if (!ActivateIfConfigured())
            {
                // ChatBot lives under ChatMenuPanel (inactive at load). Include-inactive find
                // should succeed; if anything races, a one-shot waiter retries before Start.
                EnsureWaiter();
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"{LogPrefix} Bootstrap failed: {ex.Message}");
        }
    }

    /// <returns>true when activation finished (success, disabled, or hard error that should not retry).</returns>
    static bool ActivateIfConfigured()
    {
        if (activated) return true;

        var config = MateOpenAIConfig.LoadOrCreateTemplate();
        config.LogSafeSummary();

        if (!config.enabled)
        {
            Debug.Log($"{LogPrefix} OpenAI backend disabled — preserving existing LLMUnity ChatBot wiring.");
            activated = true;
            return true;
        }

        ChatBot chatBot = FindChatBot();
        if (chatBot == null)
        {
            Debug.LogWarning($"{LogPrefix} ChatBot not found yet; will retry once scene objects settle.");
            return false;
        }

        if (chatBot.llmCharacter is MateOpenAICharacter)
        {
            Debug.Log($"{LogPrefix} MateOpenAICharacter already active on ChatBot.");
            activated = true;
            return true;
        }

        LLMCharacter original = chatBot.llmCharacter;
        if (original == null)
        {
            Debug.LogError($"{LogPrefix} ChatBot.llmCharacter is null; cannot copy prompt/state.");
            activated = true;
            return true;
        }

        // Root host only — no parenting. Persistence across scenes is not required for this slice.
        var host = new GameObject("MateOpenAICharacter");

        // Prefill static pending fields so MateOpenAICharacter.Awake can configure before any chat use.
        MateOpenAICharacter.PendingConfig = config;
        MateOpenAICharacter.PendingPrompt = original.prompt;
        MateOpenAICharacter.PendingPlayerName = string.IsNullOrEmpty(original.playerName) ? "user" : original.playerName;
        MateOpenAICharacter.PendingAIName = string.IsNullOrEmpty(original.AIName) ? "assistant" : original.AIName;

        var adapter = host.AddComponent<MateOpenAICharacter>();
        if (adapter.chat == null || adapter.chat.Count == 0)
            adapter.ClearChat();

        chatBot.llmCharacter = adapter;
        RetargetPromptBinders(adapter);

        // Keep original as fallback reference but prevent it from driving chat / further LLM work.
        original.enabled = false;
        if (original.llm != null)
        {
            // Best-effort: stop bundled llama.cpp ownership when OpenAI mode is explicit.
            try
            {
                original.llm.enabled = false;
                original.llm.gameObject.SetActive(false);
                Debug.Log($"{LogPrefix} Disabled original LLM GameObject '{original.llm.gameObject.name}' for OpenAI mode.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} Could not disable original LLM object: {ex.Message}");
            }
        }

        activated = true;
        Debug.Log($"{LogPrefix} OpenAI backend ACTIVE for ChatBot '{chatBot.gameObject.name}' (hierarchyActive={chatBot.gameObject.activeInHierarchy}). Target={config.ChatCompletionsUrl} model={config.model}");
        return true;
    }

    static void EnsureWaiter()
    {
        var go = new GameObject("MateOpenAIBootstrapWaiter");
        go.AddComponent<MateOpenAIBootstrapWaiter>();
    }

    static ChatBot FindChatBot()
    {
#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
        // ChatBot sits under ChatMenuPanel which is inactive at scene load — must Include.
        var bots = UnityEngine.Object.FindObjectsByType<ChatBot>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
        var bots = UnityEngine.Object.FindObjectsOfType<ChatBot>(true);
#endif
        if (bots == null || bots.Length == 0) return null;

        ChatBot named = null;
        ChatBot withCharacter = null;
        for (int i = 0; i < bots.Length; i++)
        {
            var b = bots[i];
            if (b == null) continue;
            if (b.gameObject.name == "ChatBot" && b.llmCharacter != null)
                named = b;
            if (withCharacter == null && b.llmCharacter != null)
                withCharacter = b;
        }

        return named != null ? named : (withCharacter != null ? withCharacter : bots[0]);
    }

    static void RetargetPromptBinders(LLMCharacter target)
    {
#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
        var binders = UnityEngine.Object.FindObjectsByType<AISystemPromptBinder>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
        var binders = UnityEngine.Object.FindObjectsOfType<AISystemPromptBinder>(true);
#endif
        if (binders == null) return;
        for (int i = 0; i < binders.Length; i++)
        {
            if (binders[i] == null) continue;
            binders[i].target = target;
            Debug.Log($"{LogPrefix} Retargeted AISystemPromptBinder on '{binders[i].gameObject.name}' to MateOpenAICharacter.");
        }
    }

    /// <summary>
    /// One-shot retry host used only when the immediate AfterSceneLoad find misses ChatBot.
    /// Runs early (DefaultExecutionOrder -10000) so the swap still beats ChatBot.Start.
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    sealed class MateOpenAIBootstrapWaiter : MonoBehaviour
    {
        void Awake()
        {
            try
            {
                if (!ActivateIfConfigured())
                    Debug.LogError($"{LogPrefix} ChatBot not found in loaded scene; cannot activate OpenAI backend.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogPrefix} Bootstrap waiter failed: {ex.Message}");
            }
            finally
            {
                Destroy(gameObject);
            }
        }
    }
}

