using System;
using UnityEngine;

/// <summary>
/// DontDestroy host that owns the speech orchestrator + Unity player.
/// </summary>
public sealed class MateSpeechService : MonoBehaviour
{
    const string LogPrefix = "[MateSpeech]";

    static MateSpeechService s_instance;

    public static MateSpeechService Instance => s_instance;

    public MateSpeechOrchestrator Orchestrator { get; private set; }

    public static MateSpeechService Ensure()
    {
        if (s_instance != null) return s_instance;
        var go = new GameObject("MateSpeechService");
        DontDestroyOnLoad(go);
        return go.AddComponent<MateSpeechService>();
    }

    public static MateSpeechOrchestrator SharedOrchestrator
    {
        get
        {
            var svc = Ensure();
            return svc.Orchestrator;
        }
    }

    /// <summary>Test seam: replace the shared orchestrator without a scene host.</summary>
    public static MateSpeechOrchestrator OverrideForTests { get; set; }

    public static MateSpeechOrchestrator Current
    {
        get
        {
            if (OverrideForTests != null) return OverrideForTests;
            if (!Application.isPlaying)
                return s_instance != null ? s_instance.Orchestrator : null;
            if (s_instance != null && s_instance.Orchestrator != null) return s_instance.Orchestrator;
            return SharedOrchestrator;
        }
    }

    void Awake()
    {
        if (s_instance != null && s_instance != this)
        {
            Destroy(gameObject);
            return;
        }
        s_instance = this;
        DontDestroyOnLoad(gameObject);

        var cfg = MateSpeechConfig.LoadOrCreateTemplate();
        var player = gameObject.GetComponent<MateSpeechPlayer>() ?? gameObject.AddComponent<MateSpeechPlayer>();
        Orchestrator = new MateSpeechOrchestrator(
            provider: null,
            player: player,
            config: cfg,
            autoPump: true);
        Orchestrator.SetProvider(new MateKokoroTtsProvider(() => Orchestrator.Config));
        Debug.Log($"{LogPrefix} service ready (provider={Orchestrator.ProviderId}, enabled={cfg.speechOutputEnabled})");
    }

    void OnDestroy()
    {
        try { Orchestrator?.CancelActiveSpeech(); } catch { }
        if (s_instance == this) s_instance = null;
    }

    void OnDisable()
    {
        try { Orchestrator?.CancelActiveSpeech(); } catch { }
    }
}
