using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Owns speech lifecycle: projection deltas → sentence chunks → ordered one-chunk prefetch.
/// TTS failures never fail conversation turns.
/// </summary>
public sealed class MateSpeechOrchestrator
{
    const string LogPrefix = "[MateSpeech]";

    public static MateSpeechOrchestrator ForTests(
        IMateTtsProvider provider,
        IMateSpeechPlayer player,
        MateSpeechConfig config = null)
    {
        return new MateSpeechOrchestrator(provider, player, config ?? new MateSpeechConfig(), autoPump: false);
    }

    readonly Queue<SpeechJob> queue = new Queue<SpeechJob>();
    readonly object gate = new object();

    IMateTtsProvider provider;
    IMateSpeechPlayer player;
    MateSpeechConfig config;

    MateSentenceChunker chunker = new MateSentenceChunker();
    Guid activeTurnId = Guid.Empty;
    int consumedSpeakableLength;
    // Prefix already released from the chunker into speech jobs. This is the
    // per-turn ownership boundary: projection rewrites must never replay it.
    int committedSpeakableLength;
    string lastSpeakable = "";
    int nextChunkIndex;
    long generation;
    CancellationTokenSource workCts = new CancellationTokenSource();
    bool workerRunning;
    bool autoPump;
    ReadySpeech readyPrefetch;
    string lastError;
    MateTtsConnectionStatus status = MateTtsConnectionStatus.Unknown;
    MateSpeechLifecycleState state = MateSpeechLifecycleState.Idle;

    // Test observation
    public List<SpeechJob> EnqueuedJobsForTests { get; } = new List<SpeechJob>();
    public List<SpeechJob> CompletedJobsForTests { get; } = new List<SpeechJob>();
    public List<SpeechJob> RejectedLateJobsForTests { get; } = new List<SpeechJob>();
    public List<SpeechTiming> TimingsForTests { get; } = new List<SpeechTiming>();

    public MateSpeechLifecycleState State => state;
    public MateTtsConnectionStatus ConnectionStatus => status;
    public string LastError => lastError;
    public string ProviderId => provider != null ? provider.ProviderId : (config?.providerId ?? MateSpeechConfig.DefaultProviderId);
    public MateSpeechConfig Config => config;
    public bool SpeechOutputEnabled => config != null && config.speechOutputEnabled;
    public int QueuedCount
    {
        get { lock (gate) return queue.Count; }
    }

    public event Action Changed;

    public MateSpeechOrchestrator(
        IMateTtsProvider provider = null,
        IMateSpeechPlayer player = null,
        MateSpeechConfig config = null,
        bool autoPump = true)
    {
        this.config = config ?? MateSpeechConfig.LoadOrCreateTemplate();
        this.provider = provider ?? new MateKokoroTtsProvider(() => this.config);
        this.player = player;
        this.autoPump = autoPump;
    }

    public void SetProvider(IMateTtsProvider next)
    {
        provider = next ?? provider;
        RaiseChanged();
    }

    public void SetPlayer(IMateSpeechPlayer next)
    {
        player = next;
    }

    public void ApplyConfig(MateSpeechConfig next, bool save = true)
    {
        if (next == null) return;
        bool wasEnabled = config.speechOutputEnabled;
        config = next;
        if (save) config.Save();
        if (wasEnabled && !config.speechOutputEnabled)
            DisableSpeechOutput();
        RaiseChanged();
    }

    public void SetSpeechOutputEnabled(bool enabled, bool save = true)
    {
        if (config.speechOutputEnabled == enabled) return;
        config.speechOutputEnabled = enabled;
        if (save) config.Save();
        if (!enabled) DisableSpeechOutput();
        RaiseChanged();
    }

    public void SetSelectedVoice(string voiceId, bool save = true)
    {
        if (string.IsNullOrWhiteSpace(voiceId)) return;
        config.selectedVoice = voiceId.Trim();
        if (save) config.Save();
        RaiseChanged();
    }

    public void SetKokoroEndpoint(string endpoint, bool save = true)
    {
        config.kokoroEndpoint = string.IsNullOrWhiteSpace(endpoint)
            ? MateSpeechConfig.DefaultKokoroEndpoint
            : endpoint.Trim();
        if (save) config.Save();
        RaiseChanged();
    }

    /// <summary>Begin tracking a new assistant turn for speech.</summary>
    public void OnTurnStarted(Guid turnId)
    {
        CancelActiveSpeech(clearError: true);
        lock (gate)
        {
            activeTurnId = turnId;
            consumedSpeakableLength = 0;
            committedSpeakableLength = 0;
            lastSpeakable = "";
            nextChunkIndex = 0;
            chunker.Reset();
        }
    }

    /// <summary>Project current segments and enqueue newly completed speakable sentences.</summary>
    public void OnSegmentsUpdated(Guid turnId, IReadOnlyList<MateResponseSegment> segments)
    {
        if (!SpeechOutputEnabled) return;
        if (turnId == Guid.Empty) return;

        lock (gate)
        {
            if (activeTurnId != turnId)
            {
                // First update for this turn without explicit start.
                activeTurnId = turnId;
                consumedSpeakableLength = 0;
                committedSpeakableLength = 0;
                lastSpeakable = "";
                nextChunkIndex = 0;
                chunker.Reset();
            }
        }

        string speakable = MateSpeechProjector.Project(segments);
        FeedSpeakable(turnId, speakable, flush: false, source: "stream");
    }

    public void OnTurnCompleted(Guid turnId, IReadOnlyList<MateResponseSegment> segments)
    {
        if (!SpeechOutputEnabled)
        {
            // Still project nothing; leave chat alone.
            return;
        }

        lock (gate)
        {
            if (activeTurnId != turnId && turnId != Guid.Empty)
            {
                activeTurnId = turnId;
            }
        }

        string speakable = MateSpeechProjector.Project(segments);
        FeedSpeakable(turnId, speakable, flush: true, source: "complete");
    }

    public void OnTurnCancelled(Guid turnId)
    {
        CancelSpeechForTurn(turnId);
    }

    public void CancelActiveSpeech(bool clearError = false)
    {
        InvalidateWork(clearError: clearError, clearActiveTurn: true);
    }

    public void CancelSpeechForTurn(Guid turnId)
    {
        lock (gate)
        {
            if (activeTurnId != Guid.Empty && activeTurnId != turnId)
                return;
        }
        InvalidateWork(clearError: false, clearActiveTurn: true);
    }

    void InvalidateWork(bool clearError, bool clearActiveTurn)
    {
        CancellationTokenSource oldCts = null;
        lock (gate)
        {
            generation++;
            queue.Clear();
            readyPrefetch = null;
            chunker.Reset();
            consumedSpeakableLength = 0;
            committedSpeakableLength = 0;
            lastSpeakable = "";
            nextChunkIndex = 0;
            if (clearActiveTurn) activeTurnId = Guid.Empty;
            if (clearError) lastError = null;
            oldCts = workCts;
            workCts = new CancellationTokenSource();
        }

        try { oldCts?.Cancel(); } catch { }
        try { oldCts?.Dispose(); } catch { }
        try { player?.Stop(); } catch { }
        state = MateSpeechLifecycleState.Idle;
        RaiseChanged();
    }

    void DisableSpeechOutput()
    {
        CancelActiveSpeech(clearError: false);
    }

    void FeedSpeakable(Guid turnId, string speakable, bool flush, string source)
    {
        List<string> chunks;
        long gen;
        lock (gate)
        {
            if (activeTurnId != turnId) return;
            gen = generation;

            speakable = speakable ?? "";
            bool isStablePrefix = speakable.Length >= consumedSpeakableLength &&
                                  speakable.StartsWith(lastSpeakable, StringComparison.Ordinal);
            if (!isStablePrefix)
            {
                // Projection normalization can rewrite unclosed Markdown when a later stream
                // delta completes it. Preserve the prefix already released to speech jobs;
                // resetting to zero here previously replayed the opening chunk at completion.
                int commonPrefix = CommonPrefixLength(lastSpeakable, speakable);
                int preservedBoundary = Math.Min(committedSpeakableLength, speakable.Length);
                Debug.Log($"[MateSpeechTrace] turn={turnId:N} gen={gen} source={source} " +
                          $"rewrite common=0..{commonPrefix} committed=0..{committedSpeakableLength} " +
                          $"resume={preservedBoundary} preview=\"{BoundedPreview(speakable)}\"");
                chunker.Reset();
                consumedSpeakableLength = preservedBoundary;
            }

            string delta = speakable.Length > consumedSpeakableLength
                ? speakable.Substring(consumedSpeakableLength)
                : "";
            consumedSpeakableLength = speakable.Length;
            lastSpeakable = speakable;

            chunks = new List<string>();
            if (!string.IsNullOrEmpty(delta))
                chunks.AddRange(chunker.Append(delta));
            if (flush)
                chunks.AddRange(chunker.Flush());
            committedSpeakableLength = Math.Max(0, consumedSpeakableLength - chunker.BufferedLength);
        }

        foreach (var c in chunks)
            EnqueueChunk(turnId, c, gen);

        if (autoPump)
            EnsureWorker();
    }
    public bool HasReadyPrefetch { get { lock (gate) return readyPrefetch != null; } }

    static int CommonPrefixLength(string left, string right)
    {
        int count = Math.Min(left?.Length ?? 0, right?.Length ?? 0);
        int i = 0;
        while (i < count && left[i] == right[i]) i++;
        return i;
    }

    static string BoundedPreview(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        const int max = 80;
        string preview = text.Length <= max ? text : text.Substring(0, max) + "...";
        return preview.Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");
    }

    void EnqueueChunk(Guid turnId, string text, long gen)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        SpeechJob job;
        lock (gate)
        {
            if (gen != generation) return;
            if (activeTurnId != turnId) return;
            job = new SpeechJob(turnId, nextChunkIndex++, text, gen);
            job.Timing.QueuedUtc = DateTime.UtcNow;
            queue.Enqueue(job);
            EnqueuedJobsForTests.Add(job);
            TimingsForTests.Add(job.Timing);
        }
        RaiseChanged();
    }

    /// <summary>Test seam: pump the sequential worker until idle (or maxSteps).</summary>
    public async Task PumpAsync(int maxSteps = 64)
    {
        for (int i = 0; i < maxSteps; i++)
        {
            bool hasWork;
            lock (gate) hasWork = queue.Count > 0 || readyPrefetch != null || workerRunning;
            if (!hasWork && state == MateSpeechLifecycleState.Idle)
                break;
            if (!workerRunning)
                await RunWorkerOnce();
            else
                await Task.Yield();
        }
        // Drain until empty.
        while (true)
        {
            bool has;
            lock (gate) has = queue.Count > 0 || readyPrefetch != null;
            if (!has) break;
            await RunWorkerOnce();
        }
    }

    void EnsureWorker()
    {
        lock (gate)
        {
            if (workerRunning) return;
            workerRunning = true;
        }
        _ = RunWorkerLoop();
    }

    async Task RunWorkerLoop()
    {
        try
        {
            while (true)
            {
                bool more = await RunWorkerOnce();
                if (!more) break;
            }
        }
        finally
        {
            lock (gate) workerRunning = false;
            if (state != MateSpeechLifecycleState.Idle)
            {
                state = MateSpeechLifecycleState.Idle;
                RaiseChanged();
            }
        }
    }

    async Task<bool> RunWorkerOnce()
    {
        ReadySpeech current = await TakeReadyOrNextAsync();
        if (current == null) return false;
        if (!IsReadyValid(current))
        {
            RejectedLateJobsForTests.Add(current.Job);
            return QueuedRemaining();
        }

        if (player == null)
        {
            CompletedJobsForTests.Add(current.Job);
            return QueuedRemaining();
        }

        CancellationToken token;
        lock (gate) token = workCts.Token;
        try
        {
            state = MateSpeechLifecycleState.Speaking;
            current.Timing.PlaybackStartedUtc = DateTime.UtcNow;
            if (player is MateFakeSpeechPlayer fake)
                fake.RememberJob(current.Job.TurnId, current.Job.ChunkIndex);
            var playback = player.PlayAsync(current.Result.AudioBytes, current.Result.Format, token);

            // Depth one: remove and synthesize at most the immediately next job.
            SpeechJob next = DequeueNext();
            Task<ReadySpeech> prefetch = next == null ? null : SynthesizeAsync(next, token);
            await playback;
            current.Timing.PlaybackCompletedUtc = DateTime.UtcNow;
            LogTiming(current);
            if (!IsReadyValid(current) || token.IsCancellationRequested)
                RejectedLateJobsForTests.Add(current.Job);
            else
                CompletedJobsForTests.Add(current.Job);

            if (prefetch != null)
            {
                ReadySpeech ready = await prefetch;
                if (ready != null && IsReadyValid(ready))
                {
                    ready.Timing.PrefetchHit = ready.Timing.SynthesisCompletedUtc <= current.Timing.PlaybackCompletedUtc;
                    ready.Timing.PreviousPlaybackCompletedUtc = current.Timing.PlaybackCompletedUtc;
                    lock (gate) readyPrefetch = ready;
                }
            }
        }
        catch (OperationCanceledException)
        {
            RejectedLateJobsForTests.Add(current.Job);
        }
        catch (Exception ex)
        {
            lastError = "playback: " + ex.Message;
            Debug.LogWarning($"{LogPrefix} playback failed (chat unaffected): {ex.Message}");
        }
        finally
        {
            state = MateSpeechLifecycleState.Idle;
            RaiseChanged();
        }
        return QueuedRemaining();
    }

    async Task<ReadySpeech> TakeReadyOrNextAsync()
    {
        ReadySpeech ready;
        lock (gate)
        {
            ready = readyPrefetch;
            readyPrefetch = null;
        }
        if (ready != null) return ready;
        SpeechJob job = DequeueNext();
        if (job == null) return null;
        CancellationToken token;
        lock (gate) token = workCts.Token;
        return await SynthesizeAsync(job, token);
    }

    SpeechJob DequeueNext()
    {
        lock (gate)
            return queue.Count > 0 ? queue.Dequeue() : null;
    }

    async Task<ReadySpeech> SynthesizeAsync(SpeechJob job, CancellationToken token)
    {
        if (!IsJobValid(job))
        {
            RejectedLateJobsForTests.Add(job);
            return null;
        }
        try
        {
            state = MateSpeechLifecycleState.Synthesizing;
            job.Timing.SynthesisStartedUtc = DateTime.UtcNow;
            string voice = config.selectedVoice;
            var request = new MateTtsRequest(job.TurnId, job.ChunkIndex, job.Text, voice);
            MateTtsSynthesisResult result = await provider.SynthesizeAsync(request, token);
            job.Timing.SynthesisCompletedUtc = DateTime.UtcNow;
            if (!IsJobValid(job) || token.IsCancellationRequested)
            {
                RejectedLateJobsForTests.Add(job);
                return null;
            }
            if (result == null || !result.Success)
            {
                lastError = result?.Error ?? "synthesis failed";
                status = MateTtsConnectionStatus.Unavailable;
                Debug.LogWarning($"{LogPrefix} synthesis failed (chat unaffected): {lastError}");
                return null;
            }
            status = MateTtsConnectionStatus.Connected;
            lastError = null;
            return new ReadySpeech(job, result, voice);
        }
        catch (OperationCanceledException)
        {
            RejectedLateJobsForTests.Add(job);
            return null;
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            status = MateTtsConnectionStatus.Unavailable;
            Debug.LogWarning($"{LogPrefix} synthesis exception (chat unaffected): {ex.Message}");
            return null;
        }
    }

    bool IsReadyValid(ReadySpeech ready)
    {
        return ready != null && IsJobValid(ready.Job);
    }

    static void LogTiming(ReadySpeech ready)
    {
        var t = ready.Timing;
        if (t.QueuedUtc == default || t.SynthesisStartedUtc == default ||
            t.SynthesisCompletedUtc == default || t.PlaybackStartedUtc == default ||
            t.PlaybackCompletedUtc == default)
            return;
        Debug.Log($"[MateSpeechTiming] turn={ready.Job.TurnId:N} gen={ready.Job.Generation} seq={ready.Job.ChunkIndex} " +
                  $"queueMs={(t.SynthesisStartedUtc - t.QueuedUtc).TotalMilliseconds:F0} " +
                  $"synthMs={(t.SynthesisCompletedUtc - t.SynthesisStartedUtc).TotalMilliseconds:F0} " +
                  $"readyMs={(t.PlaybackStartedUtc - t.SynthesisCompletedUtc).TotalMilliseconds:F0} " +
                  $"transitionMs={(t.PreviousPlaybackCompletedUtc == default ? 0 : (t.PlaybackStartedUtc - t.PreviousPlaybackCompletedUtc).TotalMilliseconds):F0} " +
                  $"playMs={(t.PlaybackCompletedUtc - t.PlaybackStartedUtc).TotalMilliseconds:F0} " +
                  $"prefetchHit={t.PrefetchHit} preview=\"{BoundedPreview(ready.Job.Text)}\"");
    }

    bool QueuedRemaining()
    {
        lock (gate) return queue.Count > 0 || readyPrefetch != null;
    }

    bool IsJobValid(SpeechJob job)
    {
        lock (gate)
        {
            if (job == null) return false;
            if (job.Generation != generation) return false;
            if (config == null || !config.speechOutputEnabled) return false;
            return true;
        }
    }

    public async Task RefreshStatusAsync()
    {
        try
        {
            status = await provider.ProbeAsync(CancellationToken.None);
            if (status == MateTtsConnectionStatus.Connected)
                lastError = null;
        }
        catch (Exception ex)
        {
            status = MateTtsConnectionStatus.Unavailable;
            lastError = ex.Message;
        }
        RaiseChanged();
    }

    public async Task<IReadOnlyList<MateTtsVoice>> ListVoicesAsync()
    {
        try
        {
            var voices = await provider.ListVoicesAsync(CancellationToken.None);
            status = voices != null && voices.Count > 0
                ? MateTtsConnectionStatus.Connected
                : MateTtsConnectionStatus.Unavailable;
            return voices ?? Array.Empty<MateTtsVoice>();
        }
        catch (Exception ex)
        {
            status = MateTtsConnectionStatus.Unavailable;
            lastError = ex.Message;
            RaiseChanged();
            return Array.Empty<MateTtsVoice>();
        }
    }

    public async Task TestVoiceAsync(string text = null)
    {
        if (!SpeechOutputEnabled)
        {
            lastError = "speech output off";
            RaiseChanged();
            return;
        }

        string sample = string.IsNullOrWhiteSpace(text)
            ? "Mate speech output is ready."
            : text.Trim();
        var turnId = Guid.NewGuid();
        OnTurnStarted(turnId);
        EnqueueChunk(turnId, sample, generation);
        if (autoPump) EnsureWorker();
        else await PumpAsync();
    }

    public string ToJsonState(IReadOnlyList<MateTtsVoice> voices = null)
    {
        var sb = new System.Text.StringBuilder(256);
        sb.Append('{');
        sb.Append("\"speechOutputEnabled\":").Append(config.speechOutputEnabled ? "true" : "false").Append(',');
        sb.Append("\"providerId\":\"").Append(Escape(ProviderId)).Append("\",");
        sb.Append("\"kokoroEndpoint\":\"").Append(Escape(config.kokoroEndpoint)).Append("\",");
        sb.Append("\"selectedVoice\":\"").Append(Escape(config.selectedVoice)).Append("\",");
        sb.Append("\"status\":\"").Append(StatusLabel()).Append("\",");
        sb.Append("\"lifecycle\":\"").Append(state.ToString()).Append("\",");
        sb.Append("\"lastError\":\"").Append(Escape(lastError ?? "")).Append("\",");
        sb.Append("\"queued\":").Append(QueuedCount).Append(',');
        sb.Append("\"voices\":[");
        if (voices != null)
        {
            for (int i = 0; i < voices.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":\"").Append(Escape(voices[i].Id)).Append("\",");
                sb.Append("\"name\":\"").Append(Escape(voices[i].Name)).Append("\"}");
            }
        }
        sb.Append("]}");
        return sb.ToString();
    }

    string StatusLabel()
    {
        if (!SpeechOutputEnabled) return "Off";
        switch (status)
        {
            case MateTtsConnectionStatus.Connected: return "Connected";
            case MateTtsConnectionStatus.Unavailable: return "Unavailable";
            default: return "Unknown";
        }
    }

    static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ");
    }

    void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch { }
    }

    public sealed class SpeechJob
    {
        public Guid TurnId { get; }
        public int ChunkIndex { get; }
        public string Text { get; }
        public long Generation { get; }
        public SpeechTiming Timing { get; } = new SpeechTiming();

        public SpeechJob(Guid turnId, int chunkIndex, string text, long generation)
        {
            TurnId = turnId;
            ChunkIndex = chunkIndex;
            Text = text;
            Generation = generation;
        }
    }

    sealed class ReadySpeech
    {
        public SpeechJob Job { get; }
        public MateTtsSynthesisResult Result { get; }
        public string VoiceId { get; }
        public SpeechTiming Timing => Job.Timing;

        public ReadySpeech(SpeechJob job, MateTtsSynthesisResult result, string voiceId)
        {
            Job = job;
            Result = result;
            VoiceId = voiceId ?? "";
        }
    }

    /// <summary>Bounded per-chunk timing record; previews deliberately stay out of this data.</summary>
    public sealed class SpeechTiming
    {
        public DateTime QueuedUtc { get; internal set; }
        public DateTime SynthesisStartedUtc { get; internal set; }
        public DateTime SynthesisCompletedUtc { get; internal set; }
        public DateTime PlaybackStartedUtc { get; internal set; }
        public DateTime PlaybackCompletedUtc { get; internal set; }
        public DateTime PreviousPlaybackCompletedUtc { get; internal set; }
        public bool PrefetchHit { get; internal set; }
    }
}
