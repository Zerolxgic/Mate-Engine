using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using LLMUnity;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Mate-owned OpenAI-compatible chat backend that ChatBot can use via LLMCharacter polymorphism.
/// Routes assistant text through MateConversationTurn + segment foundation (Slice 4).
/// Does not initialize or register with the bundled llama.cpp LLM path.
/// </summary>
[DefaultExecutionOrder(-1)]
public class MateOpenAICharacter : LLMCharacter
{
    const string LogPrefix = "[MateOpenAI]";

    /// <summary>Set by bootstrap before AddComponent so Awake can read config safely.</summary>
    public static MateOpenAIConfig PendingConfig;
    public static string PendingPrompt;
    public static string PendingPlayerName;
    public static string PendingAIName;

    MateOpenAIConfig config;
    UnityWebRequest activeRequest;
    MateConversationTurn activeTurn;

    public MateConversationTurn ActiveTurn => activeTurn;

    public void Configure(MateOpenAIConfig cfg)
    {
        config = cfg ?? new MateOpenAIConfig { enabled = false };
        temperature = config.temperature;
        if (!string.IsNullOrEmpty(config.apiKey))
            APIKey = config.apiKey;
        remote = true;
        save = "";
        saveCache = false;
        stream = false;
        playerName = string.IsNullOrEmpty(playerName) ? "user" : playerName;
        AIName = string.IsNullOrEmpty(AIName) ? "assistant" : AIName;
    }

    public override void Awake()
    {
        // Do NOT call base.Awake() — that can assign/register the local LLMUnity llama.cpp backend.
        if (PendingConfig != null)
        {
            Configure(PendingConfig);
            PendingConfig = null;
        }
        if (!string.IsNullOrEmpty(PendingPrompt))
        {
            prompt = PendingPrompt;
            PendingPrompt = null;
        }
        if (!string.IsNullOrEmpty(PendingPlayerName))
        {
            playerName = PendingPlayerName;
            PendingPlayerName = null;
        }
        if (!string.IsNullOrEmpty(PendingAIName))
        {
            AIName = PendingAIName;
            PendingAIName = null;
        }

        if (!enabled) return;

        remote = true;
        save = "";
        saveCache = false;
        stream = false;

        requestHeaders = new List<(string, string)> { ("Content-Type", "application/json") };
        if (!string.IsNullOrEmpty(APIKey))
            requestHeaders.Add(("Authorization", "Bearer " + APIKey));
        else if (config != null && !string.IsNullOrEmpty(config.apiKey))
            requestHeaders.Add(("Authorization", "Bearer " + config.apiKey));

        if (string.IsNullOrEmpty(playerName)) playerName = "user";
        if (string.IsNullOrEmpty(AIName)) AIName = "assistant";

        ClearChat();

        Debug.Log($"{LogPrefix} Adapter awake (OpenAI-compatible mode; local LLMUnity server path skipped).");
    }

    void Start()
    {
        try
        {
            string promptPath = System.IO.Path.Combine(Application.persistentDataPath, "ZomeAI_prompt.txt");
            if (System.IO.File.Exists(promptPath))
            {
                string loaded = System.IO.File.ReadAllText(promptPath);
                if (!string.IsNullOrWhiteSpace(loaded))
                    SetPrompt(loaded, true);
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"{LogPrefix} Prompt file load skipped: {ex.Message}");
        }
    }

    public override async Task Warmup(EmptyCallback completionCallback = null)
    {
        await Warmup(null, completionCallback);
    }

    public override async Task Warmup(string query, EmptyCallback completionCallback = null)
    {
        if (config == null)
            config = MateOpenAIConfig.LoadOrCreateTemplate();

        try
        {
            string modelsUrl = config.ModelsUrl;
            using (var req = UnityWebRequest.Get(modelsUrl))
            {
                req.timeout = Mathf.Clamp(config.timeoutSeconds, 5, 300);
                if (!string.IsNullOrEmpty(config.apiKey))
                    req.SetRequestHeader("Authorization", "Bearer " + config.apiKey);

                activeRequest = req;
                var op = req.SendWebRequest();
                while (!op.isDone)
                    await Task.Yield();
                activeRequest = null;

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"{LogPrefix} Warmup failed contacting {modelsUrl}: {req.error}");
                    completionCallback?.Invoke();
                    return;
                }

                string body = req.downloadHandler?.text ?? "";
                Debug.Log($"{LogPrefix} Warmup OK via GET {modelsUrl}");

                if (!string.IsNullOrEmpty(config.model))
                {
                    if (body.IndexOf(config.model, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        string preview = body.Length > 500 ? body.Substring(0, 500) + "..." : body;
                        Debug.LogWarning($"{LogPrefix} Configured model '{config.model}' not found in /models response. Available payload preview: {preview}");
                    }
                    else
                    {
                        Debug.Log($"{LogPrefix} Configured model '{config.model}' present in /models.");
                    }
                }
                else
                {
                    Debug.LogWarning($"{LogPrefix} No model configured in MateOpenAIConfig.json — set \"model\" to a /models id.");
                }
            }
        }
        catch (Exception ex)
        {
            activeRequest = null;
            Debug.LogError($"{LogPrefix} Warmup exception: {ex.Message}");
        }

        completionCallback?.Invoke();
    }

    public override async Task<string> Chat(
        string query,
        Callback<string> callback = null,
        EmptyCallback completionCallback = null,
        bool addToHistory = true)
    {
        if (config == null)
            config = MateOpenAIConfig.LoadOrCreateTemplate();

        if (string.IsNullOrWhiteSpace(query))
        {
            completionCallback?.Invoke();
            return null;
        }

        // Single-active-turn: deterministically cancel any in-flight turn before starting another.
        if (activeTurn != null && activeTurn.State == MateConversationTurn.TurnState.Running)
        {
            Debug.LogWarning($"{LogPrefix} Chat() called while turn {activeTurn.TurnId} is Running — cancelling prior turn.");
            CancelActiveTurnLocal();
        }

        if (chat == null || chat.Count == 0)
            ClearChat();

        var turn = new MateConversationTurn();
        turn.Start();
        activeTurn = turn;

        var messages = new List<ChatMessage>(chat);
        messages.Add(new ChatMessage { role = NormalizeRole(playerName), content = query });

        string reply = null;
        try
        {
            string providerText = await PostChatCompletions(messages, turn.TurnId);

            if (!IsCurrentTurn(turn.TurnId) || turn.State != MateConversationTurn.TurnState.Running)
            {
                // Cancelled or superseded — do not mutate history or UI with late success.
                reply = turn.GetRawResponseText();
                completionCallback?.Invoke();
                return reply;
            }

            if (string.IsNullOrEmpty(providerText))
                providerText = "(local backend returned empty content)";

            // Slice 4: feed complete non-streaming text as one synthetic chunk through the turn segmenter.
            turn.AppendAssistantChunk(providerText);
            turn.Complete();

            reply = turn.GetRawResponseText();

            if (addToHistory && IsCurrentTurn(turn.TurnId) && turn.State == MateConversationTurn.TurnState.Completed)
            {
                await chatLock.WaitAsync();
                try
                {
                    AddPlayerMessage(query);
                    AddAIMessage(reply);
                }
                finally
                {
                    chatLock.Release();
                }
            }

            if (IsCurrentTurn(turn.TurnId) && turn.State == MateConversationTurn.TurnState.Completed)
                callback?.Invoke(reply);
        }
        catch (OperationCanceledException)
        {
            reply = turn.GetRawResponseText();
            // Cancellation is truthful; no assistant history append.
        }
        catch (Exception ex)
        {
            if (IsCurrentTurn(turn.TurnId) && turn.State == MateConversationTurn.TurnState.Running)
            {
                turn.Fail(ex.Message);
                string fail = "Local OpenAI backend failed: " + ex.Message;
                Debug.LogError($"{LogPrefix} {fail}");
                // Failed turns are not appended to conversation history.
                callback?.Invoke(fail);
                reply = fail;
            }
            else
            {
                reply = turn.GetRawResponseText();
                Debug.Log($"{LogPrefix} Ignoring late provider error for terminal turn {turn.TurnId}: {ex.Message}");
            }
        }
        finally
        {
            completionCallback?.Invoke();
        }

        return reply;
    }

    public override void CancelRequests()
    {
        CancelActiveTurnLocal();

        try { base.CancelRequests(); } catch { }
    }

    void CancelActiveTurnLocal()
    {
        if (activeTurn != null && activeTurn.State == MateConversationTurn.TurnState.Running)
            activeTurn.Cancel();

        try
        {
            if (activeRequest != null)
            {
                activeRequest.Abort();
                activeRequest = null;
            }
        }
        catch { }
    }

    bool IsCurrentTurn(Guid turnId)
    {
        return activeTurn != null && activeTurn.TurnId == turnId;
    }

    void OnDisable()
    {
        CancelRequests();
    }

    void OnDestroy()
    {
        CancelRequests();
    }

    async Task<string> PostChatCompletions(List<ChatMessage> messages, Guid turnId)
    {
        if (config == null) throw new InvalidOperationException("MateOpenAI config missing");
        if (string.IsNullOrWhiteSpace(config.model))
            throw new InvalidOperationException("MateOpenAIConfig.model is empty — set it to a local server model id");

        string url = config.ChatCompletionsUrl;
        string json = BuildChatCompletionJson(config.model, messages, config.temperature, config.maxTokens);

        byte[] body = Encoding.UTF8.GetBytes(json);
        using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
        {
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(config.apiKey))
                req.SetRequestHeader("Authorization", "Bearer " + config.apiKey);
            req.timeout = Mathf.Clamp(config.timeoutSeconds, 5, 600);

            if (!IsCurrentTurn(turnId) || activeTurn.State != MateConversationTurn.TurnState.Running)
                throw new OperationCanceledException("Turn is not running");

            activeRequest = req;
            var op = req.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();

            if (activeRequest == req) activeRequest = null;

            // Late / cancelled turn: do not parse into a newer turn's lifecycle.
            if (!IsCurrentTurn(turnId) || activeTurn.State == MateConversationTurn.TurnState.Cancelled)
                throw new OperationCanceledException("Turn cancelled");

            if (req.result != UnityWebRequest.Result.Success)
            {
                if (activeTurn.State == MateConversationTurn.TurnState.Cancelled)
                    throw new OperationCanceledException("Request aborted after cancel");

                string errBody = req.downloadHandler?.text;
                string snippet = string.IsNullOrEmpty(errBody) ? req.error :
                    (errBody.Length > 300 ? errBody.Substring(0, 300) + "..." : errBody);
                throw new InvalidOperationException($"HTTP {(long)req.responseCode} from {url}: {snippet}");
            }

            if (!IsCurrentTurn(turnId) || activeTurn.State != MateConversationTurn.TurnState.Running)
                throw new OperationCanceledException("Turn no longer accepts provider content");

            string responseText = req.downloadHandler?.text ?? "";
            string content = ExtractAssistantContent(responseText);
            if (string.IsNullOrEmpty(content))
                throw new InvalidOperationException("Response missing choices[0].message.content");
            // Preserve Slice 3 extraction behavior (trim) as the accepted assistant text.
            return content.Trim();
        }
    }

    static string NormalizeRole(string role)
    {
        if (string.IsNullOrWhiteSpace(role)) return "user";
        string r = role.Trim().ToLowerInvariant();
        if (r == "system" || r == "user" || r == "assistant") return r;
        if (r == "player" || r == "human") return "user";
        if (r == "ai" || r == "bot") return "assistant";
        return role.Trim();
    }

    static string BuildChatCompletionJson(string model, List<ChatMessage> messages, float temperature, int maxTokens)
    {
        var sb = new StringBuilder(256 + messages.Count * 64);
        sb.Append('{');
        sb.Append("\"model\":\"").Append(EscapeJson(model)).Append("\",");
        sb.Append("\"temperature\":").Append(temperature.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
        sb.Append("\"max_tokens\":").Append(maxTokens).Append(',');
        sb.Append("\"stream\":false,");
        sb.Append("\"messages\":[");
        for (int i = 0; i < messages.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var m = messages[i];
            sb.Append('{');
            sb.Append("\"role\":\"").Append(EscapeJson(NormalizeRole(m.role))).Append("\",");
            sb.Append("\"content\":\"").Append(EscapeJson(m.content ?? "")).Append('"');
            sb.Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("\t", "\\t");
    }

    [Serializable]
    class OpenAIChatResponse
    {
        public OpenAIChoice[] choices;
    }

    [Serializable]
    class OpenAIChoice
    {
        public OpenAIMessage message;
    }

    [Serializable]
    class OpenAIMessage
    {
        public string role;
        public string content;
    }

    static string ExtractAssistantContent(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var parsed = JsonUtility.FromJson<OpenAIChatResponse>(json);
            if (parsed?.choices == null || parsed.choices.Length == 0) return null;
            return parsed.choices[0]?.message?.content;
        }
        catch
        {
            return null;
        }
    }
}
