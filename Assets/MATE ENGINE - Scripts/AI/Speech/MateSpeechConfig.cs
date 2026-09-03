using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Mate-owned speech/TTS configuration. Persisted outside the repo.
/// Browser UI is not the configuration authority.
/// </summary>
[Serializable]
public class MateSpeechConfig
{
    public const string DefaultProviderId = "kokoro";
    public const string DefaultKokoroEndpoint = "http://127.0.0.1:8880";
    public const string DefaultVoice = "af_bella";
    public const int DefaultTimeoutSeconds = 60;

    /// <summary>When false, speech jobs are not started and active speech is stopped.</summary>
    public bool speechOutputEnabled = true;

    /// <summary>Provider seam identifier (only kokoro implemented in Slice 7).</summary>
    public string providerId = DefaultProviderId;

    /// <summary>Kokoro-FastAPI base URL (no trailing /v1 required).</summary>
    public string kokoroEndpoint = DefaultKokoroEndpoint;

    public string selectedVoice = DefaultVoice;

    public int timeoutSeconds = DefaultTimeoutSeconds;

    public static string ConfigPath =>
        Path.Combine(Application.persistentDataPath, "MateSpeechConfig.json");

    public string SpeechUrl
    {
        get
        {
            string root = NormalizeEndpoint(kokoroEndpoint);
            return root + "/v1/audio/speech";
        }
    }

    public string VoicesUrl
    {
        get
        {
            string root = NormalizeEndpoint(kokoroEndpoint);
            return root + "/v1/audio/voices";
        }
    }

    public static string NormalizeEndpoint(string endpoint)
    {
        string root = (endpoint ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(root))
            root = DefaultKokoroEndpoint;
        // Accept either http://host:8880 or http://host:8880/v1
        if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            root = root.Substring(0, root.Length - 3).TrimEnd('/');
        return root;
    }

    public int GetTimeoutSeconds()
    {
        if (timeoutSeconds <= 0) return DefaultTimeoutSeconds;
        return Mathf.Clamp(timeoutSeconds, 5, 300);
    }

    public static MateSpeechConfig LoadOrCreateTemplate()
    {
        string path = ConfigPath;
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var cfg = JsonUtility.FromJson<MateSpeechConfig>(json);
                if (cfg == null)
                {
                    Debug.LogError("[MateSpeech] Failed to parse config at " + path);
                    return new MateSpeechConfig();
                }
                if (string.IsNullOrWhiteSpace(cfg.providerId))
                    cfg.providerId = DefaultProviderId;
                if (string.IsNullOrWhiteSpace(cfg.kokoroEndpoint))
                    cfg.kokoroEndpoint = DefaultKokoroEndpoint;
                if (string.IsNullOrWhiteSpace(cfg.selectedVoice))
                    cfg.selectedVoice = DefaultVoice;
                return cfg;
            }

            var template = new MateSpeechConfig();
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Application.persistentDataPath);
            File.WriteAllText(path, JsonUtility.ToJson(template, true));
            Debug.Log("[MateSpeech] Wrote speech config template to " + path);
            return template;
        }
        catch (Exception ex)
        {
            Debug.LogError("[MateSpeech] Config load/create failed: " + ex.Message);
            return new MateSpeechConfig();
        }
    }

    public void Save()
    {
        try
        {
            string path = ConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Application.persistentDataPath);
            File.WriteAllText(path, JsonUtility.ToJson(this, true));
        }
        catch (Exception ex)
        {
            Debug.LogError("[MateSpeech] Config save failed: " + ex.Message);
        }
    }
}
