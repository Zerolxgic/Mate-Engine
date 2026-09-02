using UnityEngine;

/// <summary>
/// Play Mode bootstrap for the detached technical-chat host.
/// Opens/focuses one OS-level app window when the OpenAI backend is active.
/// </summary>
public static class MateTechnicalChatBootstrap
{
    const string LogPrefix = "[MateTechnicalChat]";
    static bool attempted;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AfterSceneLoad()
    {
        if (attempted) return;
        attempted = true;
        if (!Application.isPlaying) return;

        // Delay one frame so MateOpenAIChatBootstrap can swap the adapter first.
        var go = new GameObject("MateTechnicalChatBootstrap");
        Object.DontDestroyOnLoad(go);
        go.AddComponent<MateTechnicalChatBootstrapBehaviour>();
    }

    sealed class MateTechnicalChatBootstrapBehaviour : MonoBehaviour
    {
        void Start()
        {
            try
            {
                var cfg = MateOpenAIConfig.LoadOrCreateTemplate();
                if (cfg == null || !cfg.enabled)
                {
                    Debug.Log($"{LogPrefix} OpenAI backend disabled — technical chat host not started.");
                    Destroy(gameObject);
                    return;
                }

                var host = MateTechnicalChatHost.Ensure();
                host.StartHost();
                // Open detached window once; user can close/reopen via menu or EnsureHostAndOpen.
                host.OpenOrFocusWindow();
                Debug.Log($"{LogPrefix} Detached technical chat available at {host.BaseUrl}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"{LogPrefix} Bootstrap failed: {ex.Message}");
            }
            finally
            {
                Destroy(gameObject);
            }
        }
    }
}
