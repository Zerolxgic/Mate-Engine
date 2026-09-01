using System;
using System.IO;
using UnityEngine;

/// <summary>
/// External OpenAI-compatible backend config for Mate Engine.
/// Stored outside the repo at Application.persistentDataPath.
/// </summary>
[Serializable]
public class MateOpenAIConfig
{
    public bool enabled = false;
    public string baseUrl = "http://127.0.0.1:1234/v1";
    public string model = "";
    public string apiKey = "";
    public float temperature = 0.7f;
    public int maxTokens = 512;
    public int timeoutSeconds = 120;

    public static string ConfigPath =>
        Path.Combine(Application.persistentDataPath, "MateOpenAIConfig.json");

    public string ChatCompletionsUrl
    {
        get
        {
            string root = (baseUrl ?? "").Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(root)) root = "http://127.0.0.1:1234/v1";
            return root + "/chat/completions";
        }
    }

    public string ModelsUrl
    {
        get
        {
            string root = (baseUrl ?? "").Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(root)) root = "http://127.0.0.1:1234/v1";
            return root + "/models";
        }
    }

    public static MateOpenAIConfig LoadOrCreateTemplate()
    {
        string path = ConfigPath;
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var cfg = JsonUtility.FromJson<MateOpenAIConfig>(json);
                if (cfg == null)
                {
                    Debug.LogError("[MateOpenAI] Failed to parse config at " + path);
                    return new MateOpenAIConfig { enabled = false };
                }
                return cfg;
            }

            var template = new MateOpenAIConfig { enabled = false };
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Application.persistentDataPath);
            File.WriteAllText(path, JsonUtility.ToJson(template, true));
            Debug.Log("[MateOpenAI] Wrote disabled template config to " + path);
            return template;
        }
        catch (Exception ex)
        {
            Debug.LogError("[MateOpenAI] Config load/create failed: " + ex.Message);
            return new MateOpenAIConfig { enabled = false };
        }
    }

    public void LogSafeSummary()
    {
        Debug.Log($"[MateOpenAI] Config path: {ConfigPath}");
        Debug.Log($"[MateOpenAI] enabled={enabled} baseUrl={baseUrl} model={model} temperature={temperature} maxTokens={maxTokens} apiKey={(string.IsNullOrEmpty(apiKey) ? "(none)" : "(set)")}");
    }
}
