using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Loopback-only agent event receiver for Mate Engine.
/// POST http://127.0.0.1:32146/event  → speech bubble + isTalking (no LLM / TTS).
/// </summary>
public class MateAgentEventReceiver : MonoBehaviour
{
    const string LogPrefix = "[MateAgentEventReceiver]";
    const int DefaultPort = 32146;

    [Serializable]
    public class AgentEvent
    {
        public string type;
        public string source;
        public string message;
        public string severity;
    }

    sealed class QueuedEvent
    {
        public AgentEvent Payload;
    }

    [Header("Networking")]
    public int port = DefaultPort;
    public bool debugLog = true;

    [Header("Bubble")]
    public Material bubbleMaterial;
    public Transform chatContainer;
    public Sprite bubbleSprite;
    public Color bubbleColor = new Color32(120, 120, 255, 255);
    public Color attentionBubbleColor = new Color32(220, 170, 60, 255);
    public Color errorBubbleColor = new Color32(200, 70, 70, 255);
    public Color fontColor = Color.white;
    public Font font;
    public int fontSize = 16;
    public int bubbleWidth = 600;
    public float textPadding = 10f;
    public float bubbleSpacing = 10f;
    [Range(5, 100)] public int streamSpeed = 35;
    [Range(1, 60)] public int despawnTime = 10;

    public AudioSource streamAudioSource;

    static MateAgentEventReceiver s_instance;

    LLMUnitySamples.Bubble activeBubble;
    Coroutine streamCoroutine;
    Coroutine despawnCoroutine;
    Animator avatarAnimator;

    TcpListener listener;
    Thread listenThread;
    volatile bool run;
    readonly ConcurrentQueue<QueuedEvent> queue = new ConcurrentQueue<QueuedEvent>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (!Application.isPlaying) return;
        if (FindExistingReceiver() != null) return;

        var go = new GameObject("MateAgentEventReceiver");
        DontDestroyOnLoad(go);
        go.AddComponent<MateAgentEventReceiver>();
        Debug.Log($"{LogPrefix} Runtime bootstrap created receiver GameObject.");
    }

    static MateAgentEventReceiver FindExistingReceiver()
    {
#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
        return FindFirstObjectByType<MateAgentEventReceiver>();
#else
        return FindObjectOfType<MateAgentEventReceiver>();
#endif
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
        ResolvePresentationRefs();
        FindAvatarSmart();
    }

    void OnEnable()
    {
        StartListener();
    }

    void OnDisable()
    {
        StopListener();
        RemoveBubble();
    }

    void OnDestroy()
    {
        if (s_instance == this) s_instance = null;
        StopListener();
        RemoveBubble();
    }

    void ResolvePresentationRefs()
    {
        AvatarMinecraftMessages mc = null;
#if UNITY_2023_1_OR_NEWER || UNITY_6000_0_OR_NEWER
        mc = FindFirstObjectByType<AvatarMinecraftMessages>();
#else
        mc = FindObjectOfType<AvatarMinecraftMessages>();
#endif
        if (mc != null)
        {
            if (chatContainer == null) chatContainer = mc.chatContainer;
            if (bubbleSprite == null) bubbleSprite = mc.bubbleSprite;
            if (bubbleMaterial == null) bubbleMaterial = mc.bubbleMaterial;
            if (font == null) font = mc.font;
            if (fontSize <= 0) fontSize = mc.fontSize;
            if (bubbleWidth <= 0) bubbleWidth = mc.bubbleWidth;
            if (streamAudioSource == null) streamAudioSource = mc.streamAudioSource;
            bubbleColor = mc.bubbleColor;
            fontColor = mc.fontColor;
            textPadding = mc.textPadding;
            bubbleSpacing = mc.bubbleSpacing;
            streamSpeed = mc.streamSpeed;
            despawnTime = mc.despawnTime;
            if (debugLog) Debug.Log($"{LogPrefix} Copied presentation refs from AvatarMinecraftMessages on '{mc.gameObject.name}'.");
        }

        if (font == null)
            font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        if (chatContainer == null)
            Debug.LogError($"{LogPrefix} No chatContainer resolved. Events will be accepted over HTTP but cannot display bubbles until a Mate chat container is available.");
    }

    void FindAvatarSmart()
    {
        Animator found = null;
        var loader = FindFirstObjectByType<VRMLoader>();
        if (loader != null)
        {
            var current = loader.GetCurrentModel();
            if (current != null)
                found = current.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a && a.gameObject.activeInHierarchy);
        }
        if (found == null)
        {
            var modelParent = GameObject.Find("Model");
            if (modelParent != null)
                found = modelParent.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a && a.gameObject.activeInHierarchy);
        }
        if (found == null)
        {
            var all = FindObjectsByType<Animator>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            found = all.FirstOrDefault(a => a && a.isActiveAndEnabled);
        }
        avatarAnimator = found;
        if (avatarAnimator == null && debugLog)
            Debug.LogWarning($"{LogPrefix} Avatar Animator not found; isTalking will be skipped until an avatar is available.");
    }

    void StartListener()
    {
        if (run) return;
        try
        {
            // TcpListener on loopback only — no URLACL / admin rights required.
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            run = true;
            listenThread = new Thread(ListenLoop) { IsBackground = true, Name = "MateAgentEventReceiver" };
            listenThread.Start();
            Debug.Log($"{LogPrefix} Listening on 127.0.0.1:{port} (POST /event)");
        }
        catch (Exception ex)
        {
            run = false;
            Debug.LogError($"{LogPrefix} Failed to bind 127.0.0.1:{port}: {ex.Message}");
            StopListener();
        }
    }

    void StopListener()
    {
        run = false;
        try { listener?.Stop(); } catch { }
        listener = null;
        try
        {
            if (listenThread != null && listenThread.IsAlive)
                listenThread.Join(500);
        }
        catch { }
        listenThread = null;
        if (debugLog) Debug.Log($"{LogPrefix} Listener stopped.");
    }

    void ListenLoop()
    {
        while (run)
        {
            TcpClient client = null;
            try
            {
                if (listener == null) break;
                if (!listener.Pending())
                {
                    Thread.Sleep(20);
                    continue;
                }
                client = listener.AcceptTcpClient();
                client.NoDelay = true;
                HandleClient(client);
            }
            catch (SocketException)
            {
                if (!run) break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (debugLog) Debug.LogWarning($"{LogPrefix} Accept/handle error: {ex.Message}");
            }
            finally
            {
                try { client?.Close(); } catch { }
            }
        }
    }

    void HandleClient(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            stream.ReadTimeout = 5000;
            stream.WriteTimeout = 5000;

            string requestText = ReadHttpRequest(stream);
            if (string.IsNullOrEmpty(requestText))
            {
                WriteHttpResponse(stream, 400, "application/json", "{\"ok\":false,\"error\":\"empty request\"}");
                return;
            }

            if (!TryParseHttpRequest(requestText, out string method, out string path, out string body))
            {
                WriteHttpResponse(stream, 400, "application/json", "{\"ok\":false,\"error\":\"malformed HTTP request\"}");
                return;
            }

            if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                WriteHttpResponse(stream, 405, "application/json", "{\"ok\":false,\"error\":\"method not allowed\"}");
                return;
            }

            string normalized = path.TrimEnd('/');
            if (!string.Equals(normalized, "/event", StringComparison.OrdinalIgnoreCase))
            {
                WriteHttpResponse(stream, 404, "application/json", "{\"ok\":false,\"error\":\"not found\"}");
                return;
            }

            if (!TryValidateEventJson(body, out AgentEvent evt, out string error))
            {
                WriteHttpResponse(stream, 400, "application/json",
                    "{\"ok\":false,\"error\":\"" + EscapeJson(error) + "\"}");
                return;
            }

            queue.Enqueue(new QueuedEvent { Payload = evt });
            WriteHttpResponse(stream, 200, "application/json",
                "{\"ok\":true,\"accepted\":true,\"type\":\"" + EscapeJson(evt.type) + "\"}");
        }
    }

    static string ReadHttpRequest(NetworkStream stream)
    {
        var buffer = new byte[8192];
        var ms = new MemoryStream();
        int contentLength = -1;
        int headerBytes = -1;

        while (true)
        {
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            ms.Write(buffer, 0, read);

            if (headerBytes < 0)
            {
                headerBytes = IndexOfHeaderEnd(ms.ToArray());
                if (headerBytes < 0)
                {
                    if (ms.Length > 64 * 1024) break;
                    continue;
                }

                string headers = Encoding.UTF8.GetString(ms.ToArray(), 0, headerBytes);
                contentLength = ParseContentLength(headers);
            }

            int haveBody = (int)ms.Length - (headerBytes + 4);
            if (contentLength <= 0 || haveBody >= contentLength)
                return Encoding.UTF8.GetString(ms.ToArray());

            if (ms.Length > 1024 * 1024) break;
        }

        return ms.Length > 0 ? Encoding.UTF8.GetString(ms.ToArray()) : "";
    }

    static int IndexOfHeaderEnd(byte[] data)
    {
        for (int i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == (byte)'\r' && data[i + 1] == (byte)'\n' && data[i + 2] == (byte)'\r' && data[i + 3] == (byte)'\n')
                return i;
        }
        return -1;
    }

    static int ParseContentLength(string headers)
    {
        using (var reader = new StringReader(headers))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    string v = line.Substring("Content-Length:".Length).Trim();
                    if (int.TryParse(v, out int n) && n >= 0) return n;
                }
            }
        }
        return 0;
    }

    static bool TryParseHttpRequest(string raw, out string method, out string path, out string body)
    {
        method = "";
        path = "";
        body = "";
        int headerEnd = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0) return false;

        string headerBlock = raw.Substring(0, headerEnd);
        body = raw.Substring(headerEnd + 4);

        using (var reader = new StringReader(headerBlock))
        {
            string requestLine = reader.ReadLine();
            if (string.IsNullOrEmpty(requestLine)) return false;
            string[] parts = requestLine.Split(' ');
            if (parts.Length < 2) return false;
            method = parts[0];
            path = parts[1];
            int q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);
        }
        return true;
    }

    static bool TryValidateEventJson(string json, out AgentEvent evt, out string error)
    {
        evt = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "empty body";
            return false;
        }

        try
        {
            evt = JsonUtility.FromJson<AgentEvent>(json);
        }
        catch (Exception ex)
        {
            error = "invalid JSON: " + ex.Message;
            return false;
        }

        if (evt == null)
        {
            error = "invalid JSON";
            return false;
        }
        if (string.IsNullOrWhiteSpace(evt.type))
        {
            error = "missing required field: type";
            return false;
        }
        if (string.IsNullOrWhiteSpace(evt.source))
        {
            error = "missing required field: source";
            return false;
        }
        if (string.IsNullOrWhiteSpace(evt.message))
        {
            error = "missing required field: message";
            return false;
        }

        if (string.IsNullOrWhiteSpace(evt.severity))
            evt.severity = "normal";
        else
        {
            string s = evt.severity.Trim().ToLowerInvariant();
            if (s != "normal" && s != "attention" && s != "error")
                s = "normal";
            evt.severity = s;
        }

        evt.type = evt.type.Trim();
        evt.source = evt.source.Trim();
        evt.message = evt.message.Trim();
        return true;
    }

    static void WriteHttpResponse(NetworkStream stream, int status, string contentType, string body)
    {
        string reason =
            status == 200 ? "OK" :
            status == 400 ? "Bad Request" :
            status == 404 ? "Not Found" :
            status == 405 ? "Method Not Allowed" : "Error";

        byte[] bodyBytes = Encoding.UTF8.GetBytes(body ?? "");
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
        sb.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
        sb.Append("Connection: close\r\n");
        sb.Append("\r\n");
        byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        stream.Write(headerBytes, 0, headerBytes.Length);
        if (bodyBytes.Length > 0) stream.Write(bodyBytes, 0, bodyBytes.Length);
        stream.Flush();
    }

    static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
    }

    void Update()
    {
        if (avatarAnimator == null) FindAvatarSmart();

        while (queue.TryDequeue(out var item))
        {
            if (item?.Payload == null) continue;
            PresentEvent(item.Payload);
        }
    }

    void PresentEvent(AgentEvent evt)
    {
        if (debugLog)
            Debug.Log($"{LogPrefix} Event type={evt.type} source={evt.source} severity={evt.severity} message={evt.message}");

        if (chatContainer == null)
        {
            Debug.LogWarning($"{LogPrefix} Accepted event but chatContainer is missing; cannot show bubble.");
            return;
        }

        RemoveBubble();

        Color color = bubbleColor;
        if (evt.severity == "attention") color = attentionBubbleColor;
        else if (evt.severity == "error") color = errorBubbleColor;

        var ui = new LLMUnitySamples.BubbleUI
        {
            sprite = bubbleSprite,
            font = font,
            fontSize = fontSize,
            fontColor = fontColor,
            bubbleColor = color,
            bottomPosition = 0,
            leftPosition = 1,
            textPadding = textPadding,
            bubbleOffset = bubbleSpacing,
            bubbleWidth = bubbleWidth,
            bubbleHeight = -1
        };

        activeBubble = new LLMUnitySamples.Bubble(chatContainer, ui, "AgentEventBubble", "");
        var rt = activeBubble.GetRectTransform();
        var imgs = rt.GetComponentsInChildren<Image>(true);
        for (int i = 0; i < imgs.Length; i++)
        {
            if (bubbleMaterial != null) imgs[i].material = bubbleMaterial;
            imgs[i].pixelsPerUnitMultiplier = 0.25f;
        }

        if (streamAudioSource != null)
        {
            streamAudioSource.Stop();
            streamAudioSource.Play();
        }
        if (streamCoroutine != null) StopCoroutine(streamCoroutine);
        if (avatarAnimator != null) avatarAnimator.SetBool("isTalking", true);

        streamCoroutine = StartCoroutine(FakeStreamText(evt.message));
        if (despawnCoroutine != null) StopCoroutine(despawnCoroutine);
        despawnCoroutine = StartCoroutine(DespawnAfterDelay());
    }

    IEnumerator FakeStreamText(string fullText)
    {
        if (activeBubble == null) yield break;
        activeBubble.SetText("");
        int length = 0;
        float delay = 1f / Mathf.Max(streamSpeed, 1);
        while (length < fullText.Length)
        {
            length++;
            if (activeBubble == null) yield break;
            activeBubble.SetText(fullText.Substring(0, length));
            yield return new WaitForSeconds(delay);
        }
        if (activeBubble != null) activeBubble.SetText(fullText);
        if (streamAudioSource != null && streamAudioSource.isPlaying) streamAudioSource.Stop();
        if (avatarAnimator != null) avatarAnimator.SetBool("isTalking", false);
        streamCoroutine = null;
    }

    IEnumerator DespawnAfterDelay()
    {
        yield return new WaitForSeconds(Mathf.Max(1, despawnTime));
        RemoveBubble();
    }

    void RemoveBubble()
    {
        if (streamCoroutine != null) { StopCoroutine(streamCoroutine); streamCoroutine = null; }
        if (despawnCoroutine != null) { StopCoroutine(despawnCoroutine); despawnCoroutine = null; }
        if (activeBubble != null) { activeBubble.Destroy(); activeBubble = null; }
        if (streamAudioSource != null && streamAudioSource.isPlaying) streamAudioSource.Stop();
        if (avatarAnimator != null) avatarAnimator.SetBool("isTalking", false);
    }
}
