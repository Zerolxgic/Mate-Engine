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
    /// <summary>
    /// Default maximum assistant completion/output tokens (not model context length).
    /// Serialized to the provider as <c>max_tokens</c>.
    /// </summary>
    public const int DefaultMaxOutputTokens = 2048;

    public bool enabled = false;
    public string baseUrl = "http://127.0.0.1:1234/v1";
    public string model = "";
    public string apiKey = "";
    public float temperature = 0.7f;

    /// <summary>
    /// Maximum assistant completion/output tokens for one response.
    /// This is not the model context window size. Provider field: max_tokens.
    /// </summary>
    public int maxTokens = DefaultMaxOutputTokens;

    public int timeoutSeconds = 120;

    /// <summary>
    /// Resolve a valid provider <c>max_tokens</c> value.
    /// Zero/negative configs fall back to <see cref="DefaultMaxOutputTokens"/>.
    /// </summary>
    public int GetRequestMaxTokens()
    {
        return ResolveMaxOutputTokens(maxTokens);
    }

    public static int ResolveMaxOutputTokens(int configured)
    {
        if (configured <= 0)
        {
            Debug.LogWarning(
                $"[MateOpenAI] maxTokens={configured} is invalid for provider max_tokens; using default {DefaultMaxOutputTokens}.");
            return DefaultMaxOutputTokens;
        }
        return configured;
    }

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
