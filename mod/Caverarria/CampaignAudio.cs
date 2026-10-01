using Microsoft.Xna.Framework.Audio;

namespace Caverarria;

/// <summary>Plays the original engine's PCM through Terraria's existing FNA device.</summary>
internal sealed class CampaignAudio : IDisposable
{
    private readonly DynamicSoundEffectInstance stream;
    private readonly Action<byte[]> render;
    private readonly byte[] buffer;
    private readonly Action<Action> scheduleDisposal;
    private readonly object gate = new();
    private bool disposed;
    private const int QueuedChunks = 6;
    public long SubmittedFrames { get; private set; }
    public int PendingBuffers { get { lock (gate) return disposed ? 0 : stream.PendingBufferCount; } }
    public bool Playing { get { lock (gate) return !disposed && stream.State == SoundState.Playing; } }

    public CampaignAudio(int sampleRate, Action<byte[]> render, Action<Action>? scheduleDisposal = null)
    {
        this.render = render;
        this.scheduleDisposal = scheduleDisposal ?? (action => action());
        // 100 ms tolerates occasional slow frames. Keep each chunk short so
        // the device can request a refill before the whole queue runs dry.
        buffer = new byte[(sampleRate / 60) * 2 * sizeof(short)];
        stream = new DynamicSoundEffectInstance(sampleRate, AudioChannels.Stereo);
        stream.BufferNeeded += BufferNeeded;
        try { Update(); }
        catch { stream.Dispose(); throw; }
    }

    public void Update()
    {
        lock (gate)
        {
            if (disposed) return;
            // Called on the game's main thread. The mixer shares the game engine's
            // state, so it must never be invoked from an audio callback/thread.
            while (stream.PendingBufferCount < QueuedChunks)
            {
                render(buffer);
                stream.SubmitBuffer(buffer);
                SubmittedFrames += buffer.Length / 4;
            }
            // FNA can suspend a stream while the window is inactive. Once the
            // host resumes its audio updates, resume this stream as well.
            if (stream.State == SoundState.Paused) stream.Resume();
            else if (stream.State == SoundState.Stopped) stream.Play();
        }
    }

    // FNA dispatches this event during FrameworkDispatcher.Update on the game
    // thread, immediately after updating PendingBufferCount. The ordinary audio
    // hook may run earlier in a frame and still see the previous queue count.
    private void BufferNeeded(object? sender, EventArgs args) => Update();

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            stream.BufferNeeded -= BufferNeeded;
        }
        // Terraria unloads worlds on a worker. Suppress refills immediately,
        // then release this captured FNA resource on the game thread. Never
        // resolve a later campaign's stream or engine inside the queued action.
        DynamicSoundEffectInstance capturedStream = stream;
        scheduleDisposal(() =>
        {
            try { capturedStream.Stop(); }
            finally { capturedStream.Dispose(); }
        });
    }
}
