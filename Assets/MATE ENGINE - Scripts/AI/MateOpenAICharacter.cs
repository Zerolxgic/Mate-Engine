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

        MateChatPresentationModel.Session.BeginUserTurn(turn.TurnId, query);

        var messages = new List<ChatMessage>(chat);
        messages.Add(new ChatMessage { role = NormalizeRole(playerName), content = query });

        string reply = null;
        try
        {
            bool useStream = config.streamResponses;
            string finishReason = null;

            if (useStream)
            {
                finishReason = await StreamChatCompletions(messages, turn.TurnId);
            }
            else
            {
                var buffered = await PostChatCompletions(messages, turn.TurnId);
                string providerText = buffered.content;
                finishReason = buffered.finishReason ?? "stop";

                if (!IsCurrentTurn(turn.TurnId) || turn.State != MateConversationTurn.TurnState.Running)
                {
                    MateChatPresentationModel.Session.CancelTurn(turn.TurnId, turn);
                    reply = turn.GetRawResponseText();
                    completionCallback?.Invoke();
                    return reply;
                }

                if (string.IsNullOrEmpty(providerText))
                    providerText = "(local backend returned empty content)";

                turn.AppendAssistantChunk(providerText);
            }

            if (!IsCurrentTurn(turn.TurnId) || turn.State != MateConversationTurn.TurnState.Running)
            {
                // Cancelled or superseded — do not mutate history or UI with late success.
                MateChatPresentationModel.Session.CancelTurn(turn.TurnId, turn);
                reply = turn.GetRawResponseText();
                completionCallback?.Invoke();
                return reply;
            }

            if (string.IsNullOrEmpty(turn.GetRawResponseText()))
                turn.AppendAssistantChunk("(local backend returned empty content)");

            WarnIfOutputLengthLimited(finishReason, config.GetRequestMaxTokens());

            turn.Complete();
            reply = turn.GetRawResponseText();
            MateChatPresentationModel.Session.CompleteAssistant(turn);

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
            MateChatPresentationModel.Session.CancelTurn(turn.TurnId, turn);
            reply = turn.GetRawResponseText();
            // Cancellation is truthful; no assistant history append.
        }
        catch (Exception ex)
        {
            if (IsCurrentTurn(turn.TurnId) && turn.State == MateConversationTurn.TurnState.Running)
            {
                turn.Fail(ex.Message);
                MateChatPresentationModel.Session.FailTurn(turn.TurnId, ex.Message, turn);
                string fail = "Local OpenAI backend failed: " + ex.Message;
                Debug.LogError($"{LogPrefix} {fail}");
                // Failed turns are not appended to conversation history.
                callback?.Invoke(fail);
                reply = fail;
            }
            else
            {
                MateChatPresentationModel.Session.CancelTurn(turn.TurnId, turn);
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
        {
            var id = activeTurn.TurnId;
            activeTurn.Cancel();
            MateChatPresentationModel.Session.CancelTurn(id, activeTurn);
        }

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

    async Task<(string content, string finishReason)> PostChatCompletions(List<ChatMessage> messages, Guid turnId)
    {
        if (config == null) throw new InvalidOperationException("MateOpenAI config missing");
        if (string.IsNullOrWhiteSpace(config.model))
            throw new InvalidOperationException("MateOpenAIConfig.model is empty — set it to a local server model id");

        string url = config.ChatCompletionsUrl;
        int maxOutputTokens = config.GetRequestMaxTokens();
        string json = BuildChatCompletionJson(config.model, messages, config.temperature, maxOutputTokens, stream: false);

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
            if (!TryParseChatCompletion(responseText, out string content, out string finishReason))
                throw new InvalidOperationException("Response missing choices[0].message.content");

            // Preserve Slice 3 extraction behavior (trim) as the accepted assistant text.
            // finish_reason=length still yields a successful Completed turn with preserved partial text.
            return (content.Trim(), finishReason);
        }
    }

    /// <summary>
    /// Real provider SSE streaming path. Appends deltas into the active turn and
    /// publishes incremental presentation updates. Returns provider finish_reason.
    /// </summary>
    async Task<string> StreamChatCompletions(List<ChatMessage> messages, Guid turnId)
    {
        if (config == null) throw new InvalidOperationException("MateOpenAI config missing");
        if (string.IsNullOrWhiteSpace(config.model))
            throw new InvalidOperationException("MateOpenAIConfig.model is empty — set it to a local server model id");

        string url = config.ChatCompletionsUrl;
        int maxOutputTokens = config.GetRequestMaxTokens();
        string json = BuildChatCompletionJson(config.model, messages, config.temperature, maxOutputTokens, stream: true);

        byte[] body = Encoding.UTF8.GetBytes(json);
        var handler = new MateOpenAIStreamDownloadHandler();
        using (var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
        {
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = handler;
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Accept", "text/event-stream");
            if (!string.IsNullOrEmpty(config.apiKey))
                req.SetRequestHeader("Authorization", "Bearer " + config.apiKey);
            req.timeout = Mathf.Clamp(config.timeoutSeconds, 5, 600);

            if (!IsCurrentTurn(turnId) || activeTurn.State != MateConversationTurn.TurnState.Running)
                throw new OperationCanceledException("Turn is not running");

            activeRequest = req;
            Debug.Log($"{LogPrefix} streaming response started");
            try
            {
                var op = req.SendWebRequest();

                string finishReason = null;
                bool sawDone = false;
                bool sawContent = false;
                int presentRevisionBudget = 0;

                while (!op.isDone)
                {
                    if (!IsCurrentTurn(turnId) || activeTurn.State != MateConversationTurn.TurnState.Running)
                    {
                        try { req.Abort(); } catch { }
                        throw new OperationCanceledException("Turn cancelled");
                    }

                    while (handler.Events.TryDequeue(out var ev))
                    {
                        ApplyStreamEvent(ev, turnId, ref finishReason, ref sawDone, ref sawContent, ref presentRevisionBudget);
                    }

                    await Task.Yield();
                }

                // Drain trailing events after download completes.
                while (handler.Events.TryDequeue(out var ev))
                    ApplyStreamEvent(ev, turnId, ref finishReason, ref sawDone, ref sawContent, ref presentRevisionBudget);

                if (!IsCurrentTurn(turnId) || activeTurn.State == MateConversationTurn.TurnState.Cancelled)
                    throw new OperationCanceledException("Turn cancelled");

                if (req.result != UnityWebRequest.Result.Success)
                {
                    if (activeTurn != null && activeTurn.State == MateConversationTurn.TurnState.Cancelled)
                        throw new OperationCanceledException("Request aborted after cancel");

                    string errBody = req.downloadHandler != null ? req.downloadHandler.text : null;
                    string snippet = string.IsNullOrEmpty(errBody) ? req.error :
                        (errBody.Length > 300 ? errBody.Substring(0, 300) + "..." : errBody);
                    throw new InvalidOperationException($"HTTP {(long)req.responseCode} from {url}: {snippet}");
                }

                if (!IsCurrentTurn(turnId) || activeTurn.State != MateConversationTurn.TurnState.Running)
                    throw new OperationCanceledException("Turn no longer accepts provider content");

                if (string.IsNullOrEmpty(finishReason))
                {
                    if (sawDone)
                        finishReason = "stop";
                    else
                    {
                        Debug.LogError($"{LogPrefix} streaming transport ended before terminal event");
                        throw new InvalidOperationException("streaming transport ended before terminal event");
                    }
                }

                Debug.Log($"{LogPrefix} streaming response completed: {finishReason}");
                if (IsCurrentTurn(turnId) && activeTurn.State == MateConversationTurn.TurnState.Running)
                    MateChatPresentationModel.Session.UpdateRunningAssistant(activeTurn);

                return finishReason;
            }
            finally
            {
                if (activeRequest == req) activeRequest = null;
            }
        }
    }

    void ApplyStreamEvent(
        MateOpenAISseEvent ev,
        Guid turnId,
        ref string finishReason,
        ref bool sawDone,
        ref bool sawContent,
        ref int presentRevisionBudget)
    {
        if (!IsCurrentTurn(turnId) || activeTurn == null) return;
        if (activeTurn.State != MateConversationTurn.TurnState.Running) return;

        switch (ev.Kind)
        {
            case MateOpenAISseEventKind.ContentDelta:
                if (!string.IsNullOrEmpty(ev.Text))
                {
                    activeTurn.AppendAssistantChunk(ev.Text);
                    sawContent = true;
                    presentRevisionBudget++;
                    // Publish every delta for true streaming visibility (no artificial delay).
                    MateChatPresentationModel.Session.UpdateRunningAssistant(activeTurn);
                }
                if (!string.IsNullOrEmpty(ev.FinishReason) && string.IsNullOrEmpty(finishReason))
                    finishReason = ev.FinishReason;
                break;

            case MateOpenAISseEventKind.ReasoningDelta:
                if (!string.IsNullOrEmpty(ev.Text))
                {
                    activeTurn.AppendReasoningChunk(ev.Text);
                    // Reasoning stays hidden; still bump so state stays coherent without prose merge.
                    MateChatPresentationModel.Session.UpdateRunningAssistant(activeTurn);
                }
                if (!string.IsNullOrEmpty(ev.FinishReason) && string.IsNullOrEmpty(finishReason))
                    finishReason = ev.FinishReason;
                break;

            case MateOpenAISseEventKind.FinishReason:
                if (!string.IsNullOrEmpty(ev.FinishReason) && string.IsNullOrEmpty(finishReason))
                    finishReason = ev.FinishReason;
                break;

            case MateOpenAISseEventKind.Done:
                sawDone = true;
                break;

            case MateOpenAISseEventKind.ParseError:
                Debug.LogError($"{LogPrefix} streaming SSE parse failed");
                throw new InvalidOperationException(ev.ErrorMessage ?? "streaming SSE parse failed");
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

    /// <summary>
    /// Build chat.completions request body.
    /// <paramref name="maxOutputTokens"/> is max assistant completion size (provider max_tokens).
    /// <paramref name="stream"/> selects provider SSE streaming vs buffered completion.
    /// </summary>
    public static string BuildChatCompletionJson(
        string model,
        List<ChatMessage> messages,
        float temperature,
        int maxOutputTokens,
        bool stream = false)
    {
        int tokens = MateOpenAIConfig.ResolveMaxOutputTokens(maxOutputTokens);
        var sb = new StringBuilder(256 + (messages?.Count ?? 0) * 64);
        sb.Append('{');
        sb.Append("\"model\":\"").Append(EscapeJson(model ?? "")).Append("\",");
        sb.Append("\"temperature\":").Append(temperature.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(',');
        sb.Append("\"max_tokens\":").Append(tokens).Append(',');
        sb.Append("\"stream\":").Append(stream ? "true" : "false").Append(',');
        sb.Append("\"messages\":[");
        if (messages != null)
        {
            for (int i = 0; i < messages.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var m = messages[i];
                sb.Append('{');
                sb.Append("\"role\":\"").Append(EscapeJson(NormalizeRole(m.role))).Append("\",");
                sb.Append("\"content\":\"").Append(EscapeJson(m.content ?? "")).Append('"');
                sb.Append('}');
            }
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
        public string finish_reason;
    }

    [Serializable]
    class OpenAIMessage
    {
        public string role;
        public string content;
    }

    /// <summary>
    /// Parse OpenAI-compatible chat.completions JSON. Preserves partial content when
    /// finish_reason is length (provider output-token cap). Test seam for O5.
    /// </summary>
    public static bool TryParseChatCompletion(string json, out string content, out string finishReason)
    {
        content = null;
        finishReason = null;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var parsed = JsonUtility.FromJson<OpenAIChatResponse>(json);
            if (parsed?.choices == null || parsed.choices.Length == 0) return false;
            var choice = parsed.choices[0];
            content = choice?.message?.content;
            finishReason = choice?.finish_reason;
            return !string.IsNullOrEmpty(content);
        }
        catch
        {
            return false;
        }
    }

    static string ExtractAssistantContent(string json)
    {
        return TryParseChatCompletion(json, out string content, out _) ? content : null;
    }

    /// <summary>Bounded diagnostic for finish_reason=length without failing the turn (O5 seam).</summary>
    public static bool WarnIfOutputLengthLimited(string finishReason, int maxOutputTokens)
    {
        if (!string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
            return false;
        Debug.LogWarning(
            $"{LogPrefix} response reached provider output-token limit (max_tokens={maxOutputTokens})");
        return true;
    }
}
