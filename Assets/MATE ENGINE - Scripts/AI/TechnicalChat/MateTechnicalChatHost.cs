using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Loopback-only detached technical-chat host.
/// Serves a Mate-owned web UI on 127.0.0.1 and opens it in an OS-level app window
/// (Edge/Chrome --app mode when available). Not an in-Unity Canvas panel.
/// </summary>
public sealed class MateTechnicalChatHost : MonoBehaviour
{
    public const int DefaultPort = 32147;
    const string LogPrefix = "[MateTechnicalChat]";

    static MateTechnicalChatHost s_instance;

    HttpListener listener;
    Thread listenThread;
    volatile bool run;
    Process windowProcess;
    readonly ConcurrentQueue<Action> mainThread = new ConcurrentQueue<Action>();
    readonly ConcurrentDictionary<Guid, TextWriter> sseClients = new ConcurrentDictionary<Guid, TextWriter>();
    int lastPushedRevision = -1;

    public int Port = DefaultPort;
    public bool IsListening => run && listener != null && listener.IsListening;
    public string BaseUrl => "http://127.0.0.1:" + Port + "/";

    public static MateTechnicalChatHost Instance => s_instance;

    public static MateTechnicalChatHost Ensure()
    {
        if (s_instance != null) return s_instance;
        var go = new GameObject("MateTechnicalChatHost");
        DontDestroyOnLoad(go);
        return go.AddComponent<MateTechnicalChatHost>();
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
    }

    void OnEnable()
    {
        MateChatPresentationModel.Session.Changed += OnModelChanged;
        StartHost();
    }

    void OnDisable()
    {
        MateChatPresentationModel.Session.Changed -= OnModelChanged;
        StopHost();
    }

    void OnDestroy()
    {
        if (s_instance == this) s_instance = null;
        MateChatPresentationModel.Session.Changed -= OnModelChanged;
        StopHost();
        CloseWindowProcess();
    }

    void Update()
    {
        DrainMainThreadQueue();

        int rev = MateChatPresentationModel.Session.Revision;
        if (rev != lastPushedRevision)
        {
            lastPushedRevision = rev;
            PushSse();
        }
    }

    void OnModelChanged()
    {
        // Revision poll in Update pushes SSE; nothing else required here.
    }

    public void StartHost()
    {
        if (run) return;
        try
        {
            listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            listener.Start();
            run = true;
            listenThread = new Thread(ListenLoop) { IsBackground = true, Name = "MateTechnicalChatHost" };
            listenThread.Start();
            Debug.Log($"{LogPrefix} Listening on {BaseUrl}");
        }
        catch (Exception ex)
        {
            run = false;
            Debug.LogError($"{LogPrefix} Failed to start host on port {Port}: {ex.Message}");
        }
    }

    public void StopHost()
    {
        run = false;
        try { listener?.Stop(); } catch { }
        try { listener?.Close(); } catch { }
        listener = null;
        foreach (var kv in sseClients)
        {
            try { kv.Value.Dispose(); } catch { }
        }
        sseClients.Clear();
    }

    public void OpenOrFocusWindow()
    {
        if (!IsListening) StartHost();
        if (!IsListening)
        {
            Debug.LogError($"{LogPrefix} Cannot open window — host not listening.");
            return;
        }

        // Raise existing process if still alive.
        if (windowProcess != null && !windowProcess.HasExited)
        {
            try
            {
                // Best-effort focus: re-launching --app with same URL often focuses existing app window.
                TryLaunchAppWindow(BaseUrl);
                return;
            }
            catch { /* fall through */ }
        }

        windowProcess = TryLaunchAppWindow(BaseUrl);
        if (windowProcess == null)
            Debug.LogWarning($"{LogPrefix} Opened via default browser handler; prefer Edge/Chrome --app for a dedicated window.");
    }

    public void CloseWindowProcess()
    {
        try
        {
            if (windowProcess != null && !windowProcess.HasExited)
            {
                windowProcess.CloseMainWindow();
                if (!windowProcess.WaitForExit(1500))
                    windowProcess.Kill();
            }
        }
        catch { }
        finally
        {
            try { windowProcess?.Dispose(); } catch { }
            windowProcess = null;
        }
    }

    static Process TryLaunchAppWindow(string url)
    {
        string[] candidates =
        {
            GetEdgePath(),
            GetChromePath(),
        };

        foreach (var exe in candidates)
        {
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) continue;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--app=\"" + url + "\" --new-window",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                return Process.Start(psi);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} Launch failed for {exe}: {ex.Message}");
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.LogError($"{LogPrefix} Default browser open failed: {ex.Message}");
        }
        return null;
    }

    static string GetEdgePath()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string p1 = Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe");
        if (File.Exists(p1)) return p1;
        string pf64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string p2 = Path.Combine(pf64, "Microsoft", "Edge", "Application", "msedge.exe");
        return File.Exists(p2) ? p2 : null;
    }

    static string GetChromePath()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string p1 = Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe");
        if (File.Exists(p1)) return p1;
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string p2 = Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe");
        return File.Exists(p2) ? p2 : null;
    }

    void ListenLoop()
    {
        while (run)
        {
            HttpListenerContext ctx = null;
            try
            {
                ctx = listener.GetContext();
            }
            catch
            {
                if (!run) break;
                continue;
            }

            // Dispatch each request onto the thread pool so a long-lived SSE
            // connection cannot block POST /api/send (or other routes).
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Handle(ctx); }
                catch (Exception ex)
                {
                    try
                    {
                        WriteText(ctx.Response, 500, "text/plain", "error: " + ex.Message);
                    }
                    catch { }
                }
            });
        }
    }

    void Handle(HttpListenerContext ctx)
    {
        string path = ctx.Request.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant();
        if (string.IsNullOrEmpty(path)) path = "/";

        if (ctx.Request.HttpMethod == "OPTIONS")
        {
            ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
            ctx.Response.AddHeader("Access-Control-Allow-Methods", "GET,POST,OPTIONS");
            ctx.Response.AddHeader("Access-Control-Allow-Headers", "Content-Type");
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            return;
        }

        if (ctx.Request.HttpMethod == "GET" && (path == "/" || path == "/index.html"))
        {
            WriteText(ctx.Response, 200, "text/html; charset=utf-8", MateTechnicalChatPage.Html);
            return;
        }

        if (ctx.Request.HttpMethod == "GET" && path == "/api/state")
        {
            WriteText(ctx.Response, 200, "application/json; charset=utf-8",
                MateChatPresentationModel.Session.ToJsonSnapshot());
            return;
        }

        if (ctx.Request.HttpMethod == "GET" && path == "/api/events")
        {
            HandleSse(ctx);
            return;
        }

        if (ctx.Request.HttpMethod == "POST" && path == "/api/send")
        {
            HandleSend(ctx);
            return;
        }

        if (ctx.Request.HttpMethod == "POST" && path == "/api/cancel")
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            mainThread.Enqueue(() =>
            {
                try
                {
                    MateTechnicalChatController.CancelFromUi();
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            bool done = false;
            try { done = tcs.Task.Wait(5000); } catch { }
            if (!done || tcs.Task.IsFaulted)
                WriteText(ctx.Response, 500, "application/json", "{\"ok\":false,\"error\":\"cancel failed\"}");
            else
                WriteText(ctx.Response, 200, "application/json", "{\"ok\":true}");
            return;
        }

        WriteText(ctx.Response, 404, "text/plain", "not found");
    }

    void HandleSend(HttpListenerContext ctx)
    {
        Debug.Log($"{LogPrefix} browser POST received");
        string body = ReadBody(ctx.Request);
        if (!TryParseSendText(body, out string text, out string parseError))
        {
            WriteText(ctx.Response, 400, "application/json",
                "{\"ok\":false,\"error\":\"" + EscapeJson(parseError) + "\"}");
            return;
        }

        Debug.Log($"{LogPrefix} parsed send text length={text.Length}");

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        mainThread.Enqueue(() =>
        {
            try
            {
                Debug.Log($"{LogPrefix} send dequeued on main thread");
                if (!MateTechnicalChatController.TrySendFromUi(text, out string sendError))
                {
                    tcs.TrySetResult(sendError ?? "rejected");
                    return;
                }
                tcs.TrySetResult(null);
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogPrefix} Send failed: {ex.Message}");
                tcs.TrySetException(ex);
            }
        });
        Debug.Log($"{LogPrefix} send queued");

        bool completed = false;
        try { completed = tcs.Task.Wait(30000); }
        catch (AggregateException ae)
        {
            string msg = ae.InnerException != null ? ae.InnerException.Message : ae.Message;
            WriteText(ctx.Response, 500, "application/json",
                "{\"ok\":false,\"error\":\"" + EscapeJson(msg) + "\"}");
            return;
        }

        if (!completed)
        {
            WriteText(ctx.Response, 504, "application/json", "{\"ok\":false,\"error\":\"timeout\"}");
            return;
        }

        string reject = tcs.Task.Result;
        if (!string.IsNullOrEmpty(reject))
        {
            WriteText(ctx.Response, 409, "application/json",
                "{\"ok\":false,\"error\":\"" + EscapeJson(reject) + "\"}");
            return;
        }

        WriteText(ctx.Response, 200, "application/json", "{\"ok\":true}");
    }

    /// <summary>
    /// Parse POST /api/send JSON body. Rejects missing/blank text.
    /// Exposed for deterministic bridge verification.
    /// </summary>
    public static bool TryParseSendText(string body, out string text, out string error)
    {
        text = ExtractJsonString(body, "text");
        if (string.IsNullOrWhiteSpace(text))
        {
            text = "";
            error = "empty";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>Enqueue a main-thread action (test seam + host internals).</summary>
    public void EnqueueMainThread(Action action)
    {
        if (action == null) return;
        mainThread.Enqueue(action);
    }

    /// <summary>Drain queued main-thread actions once (mirrors Update drain; test seam).</summary>
    public int DrainMainThreadQueue()
    {
        int n = 0;
        while (mainThread.TryDequeue(out var action))
        {
            n++;
            try { action?.Invoke(); }
            catch (Exception ex) { Debug.LogError($"{LogPrefix} Main-thread action failed: {ex.Message}"); }
        }
        return n;
    }

    static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "")
            .Replace("\n", " ");
    }

    void HandleSse(HttpListenerContext ctx)
    {
        var resp = ctx.Response;
        resp.StatusCode = 200;
        resp.ContentType = "text/event-stream";
        resp.AddHeader("Cache-Control", "no-cache");
        resp.AddHeader("Access-Control-Allow-Origin", "*");
        resp.SendChunked = true;

        var id = Guid.NewGuid();
        var writer = new StreamWriter(resp.OutputStream, new UTF8Encoding(false)) { AutoFlush = true };
        sseClients[id] = writer;

        try
        {
            writer.Write("event: state\ndata: ");
            writer.Write(MateChatPresentationModel.Session.ToJsonSnapshot());
            writer.Write("\n\n");

            // Keep connection until client disconnects or host stops.
            while (run && sseClients.ContainsKey(id))
            {
                Thread.Sleep(1000);
                try
                {
                    writer.Write(": ping\n\n");
                }
                catch
                {
                    break;
                }
            }
        }
        catch { }
        finally
        {
            TextWriter removed;
            sseClients.TryRemove(id, out removed);
            try { writer.Dispose(); } catch { }
            try { resp.Close(); } catch { }
        }
    }

    void PushSse()
    {
        string payload = MateChatPresentationModel.Session.ToJsonSnapshot();
        foreach (var kv in sseClients)
        {
            try
            {
                kv.Value.Write("event: state\ndata: ");
                kv.Value.Write(payload);
                kv.Value.Write("\n\n");
            }
            catch
            {
                TextWriter removed;
                sseClients.TryRemove(kv.Key, out removed);
            }
        }
    }

    static string ReadBody(HttpListenerRequest req)
    {
        using (var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
            return reader.ReadToEnd();
    }

    static string ExtractJsonString(string json, string key)
    {
        if (string.IsNullOrEmpty(json)) return "";
        // Minimal extractor for {"text":"..."}; avoids JsonUtility needing a wrapper type for arbitrary keys.
        string needle = "\"" + key + "\"";
        int idx = json.IndexOf(needle, StringComparison.Ordinal);
        if (idx < 0) return "";
        int colon = json.IndexOf(':', idx + needle.Length);
        if (colon < 0) return "";
        int startQuote = json.IndexOf('"', colon + 1);
        if (startQuote < 0) return "";
        var sb = new StringBuilder();
        for (int i = startQuote + 1; i < json.Length; i++)
        {
            char c = json[i];
            if (c == '\\' && i + 1 < json.Length)
            {
                char n = json[++i];
                if (n == 'n') sb.Append('\n');
                else if (n == 'r') sb.Append('\r');
                else if (n == 't') sb.Append('\t');
                else sb.Append(n);
                continue;
            }
            if (c == '"') break;
            sb.Append(c);
        }
        return sb.ToString();
    }

    static void WriteText(HttpListenerResponse resp, int code, string contentType, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body ?? "");
        resp.StatusCode = code;
        resp.ContentType = contentType;
        resp.ContentEncoding = Encoding.UTF8;
        resp.AddHeader("Access-Control-Allow-Origin", "*");
        resp.ContentLength64 = bytes.Length;
        resp.OutputStream.Write(bytes, 0, bytes.Length);
        resp.Close();
    }
}
