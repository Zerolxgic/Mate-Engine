using UnityEngine;

/// <summary>DontDestroy host for the Unity microphone and Slice 9 speech-input orchestrator.</summary>
public sealed class MateSpeechInputService : MonoBehaviour
{
    static MateSpeechInputService instance;
    public static MateSpeechInputService Instance => instance;
    public static MateSpeechInputOrchestrator OverrideForTests { get; set; }
    public MateSpeechInputOrchestrator Orchestrator { get; private set; }
    public MateSpeechInputConfig Config { get; private set; }
    public static MateSpeechInputOrchestrator Current
    {
        get
        {
            if (OverrideForTests != null) return OverrideForTests;
            if (!Application.isPlaying) return instance?.Orchestrator;
            return Ensure().Orchestrator;
        }
    }
    public static MateSpeechInputService Ensure()
    {
        if (instance != null) return instance;
        var go = new GameObject("MateSpeechInputService"); DontDestroyOnLoad(go);
        return go.AddComponent<MateSpeechInputService>();
    }
    void Awake()
    {
        if (instance != null && instance != this) { Destroy(gameObject); return; }
        instance = this; DontDestroyOnLoad(gameObject);
        Config = MateSpeechInputConfig.LoadOrCreateTemplate();
        Orchestrator = new MateSpeechInputOrchestrator(
            new MateUnityMicrophoneCapture(), new MateSpeachesAsrProvider(() => Config), () => Config,
            transcript => MateTechnicalChatController.TrySendFromUi(transcript, out _),
            MateTechnicalChatController.CancelFromUi);
    }
    void Update() { Orchestrator?.Tick(); }
    void OnDisable() { Orchestrator?.Cancel("service disabled"); }
    void OnDestroy() { Orchestrator?.Cancel("service destroyed"); if (instance == this) instance = null; }
}
