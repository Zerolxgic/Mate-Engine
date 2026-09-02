using System.Collections.Concurrent;
using UnityEngine.Networking;

/// <summary>
/// UnityWebRequest download handler that feeds OpenAI SSE bytes into <see cref="MateOpenAISseParser"/>.
/// ReceiveData may run off the main thread; events are queued for main-thread consumption.
/// </summary>
public sealed class MateOpenAIStreamDownloadHandler : DownloadHandlerScript
{
    readonly MateOpenAISseParser parser = new MateOpenAISseParser();
    readonly ConcurrentQueue<MateOpenAISseEvent> events = new ConcurrentQueue<MateOpenAISseEvent>();

    public ConcurrentQueue<MateOpenAISseEvent> Events => events;
    public MateOpenAISseParser Parser => parser;

    public MateOpenAIStreamDownloadHandler() : base(new byte[64 * 1024]) { }

    protected override bool ReceiveData(byte[] data, int dataLength)
    {
        if (data == null || dataLength <= 0) return true;
        try
        {
            parser.AppendBytes(data, dataLength, events);
        }
        catch
        {
            events.Enqueue(MateOpenAISseEvent.ParseError("streaming SSE parse failed"));
        }
        return true;
    }

    protected override void CompleteContent()
    {
        // Flush any trailing incomplete UTF-8 is intentionally not forced —
        // premature EOF is diagnosed by the request loop when no terminal event arrived.
    }
}
