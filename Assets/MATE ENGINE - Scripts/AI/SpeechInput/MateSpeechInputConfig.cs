using System;
using System.IO;
using UnityEngine;

/// <summary>Persisted Mate-owned configuration for the bounded Slice 9 speech-input path.</summary>
[Serializable]
public sealed class MateSpeechInputConfig
{
    public const string DefaultProviderId = "speaches";
    public const string DefaultBaseUrl = "http://127.0.0.1:8000";
    public const string DefaultModelId = "Systran/faster-distil-whisper-large-v3";
    public const string DefaultLanguage = "en";
    public const int DefaultMaximumCaptureSeconds = 90;
    public const int DefaultMinimumSamples = 1600; // 100 ms at the target 16 kHz.

    public bool speechInputEnabled = true;
    public string providerId = DefaultProviderId;
    public string baseUrl = DefaultBaseUrl;
    public string modelId = DefaultModelId;
    public string language = DefaultLanguage;
    public int maximumCaptureSeconds = DefaultMaximumCaptureSeconds;
    public int minimumSamples = DefaultMinimumSamples;

    public static string ConfigPath => Path.Combine(Application.persistentDataPath, "MateSpeechInputConfig.json");

    public string TranscriptionsUrl => NormalizeBaseUrl(baseUrl) + "/v1/audio/transcriptions";

    public int GetMaximumCaptureSeconds() => Mathf.Clamp(maximumCaptureSeconds <= 0 ? DefaultMaximumCaptureSeconds : maximumCaptureSeconds, 1, 90);
    public int GetMinimumSamples() => Mathf.Max(1, minimumSamples <= 0 ? DefaultMinimumSamples : minimumSamples);

    public static string NormalizeBaseUrl(string value)
    {
        string root = (value ?? "").Trim().TrimEnd('/');
        return string.IsNullOrEmpty(root) ? DefaultBaseUrl : root;
    }

    public static MateSpeechInputConfig LoadOrCreateTemplate()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var loaded = JsonUtility.FromJson<MateSpeechInputConfig>(File.ReadAllText(ConfigPath)) ?? new MateSpeechInputConfig();
                loaded.Normalize();
                return loaded;
            }
            var created = new MateSpeechInputConfig();
            created.Save();
            return created;
        }
        catch (Exception ex)
        {
            Debug.LogError("[MateSpeechInput] Config load/create failed: " + ex.Message);
            return new MateSpeechInputConfig();
        }
    }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(providerId)) providerId = DefaultProviderId;
        if (string.IsNullOrWhiteSpace(baseUrl)) baseUrl = DefaultBaseUrl;
        if (string.IsNullOrWhiteSpace(modelId)) modelId = DefaultModelId;
        if (string.IsNullOrWhiteSpace(language)) language = DefaultLanguage;
        maximumCaptureSeconds = GetMaximumCaptureSeconds();
        minimumSamples = GetMinimumSamples();
    }

    public void Save()
    {
        try
        {
            Normalize();
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath) ?? Application.persistentDataPath);
            File.WriteAllText(ConfigPath, JsonUtility.ToJson(this, true));
        }
        catch (Exception ex) { Debug.LogError("[MateSpeechInput] Config save failed: " + ex.Message); }
    }
}
