using System.Reflection;
using Caverarria;
using Microsoft.Xna.Framework.Audio;

using var game = new Microsoft.Xna.Framework.Game(true);

// Exact host adapter and real FNA streams; this checks lifecycle, not speakers.
for (int session = 0; session < 2; session++)
{
    using var audio = new CampaignAudio(48000, bytes => Array.Clear(bytes));
    if (!audio.Playing || audio.PendingBuffers != 6 || audio.SubmittedFrames != 4800)
        throw new Exception("New session did not prime and start its FNA stream.");
    var stream = (DynamicSoundEffectInstance)typeof(CampaignAudio)
        .GetField("stream", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(audio)!;
    stream.Pause();
    if (audio.Playing) throw new Exception("Pause fixture did not suspend the FNA stream.");
    audio.Update();
    if (!audio.Playing) throw new Exception("Host audio update did not resume a paused stream.");
}
var pending = new Queue<Action>();
int renders = 0;
var unloading = new CampaignAudio(48000, bytes => { renders++; Array.Clear(bytes); }, pending.Enqueue);
Task.Run(unloading.Dispose).GetAwaiter().GetResult();
if (pending.Count != 1 || unloading.Playing || unloading.PendingBuffers != 0)
    throw new Exception("Worker unload did not immediately detach its audio session.");
int before = renders;
unloading.Update();
typeof(CampaignAudio).GetMethod("BufferNeeded", BindingFlags.NonPublic | BindingFlags.Instance)!
    .Invoke(unloading, new object?[] { null, EventArgs.Empty });
if (renders != before) throw new Exception("Disposed audio still accessed its guest mixer.");
// A later session can exist before the old stream's queued release runs.
using var next = new CampaignAudio(48000, bytes => Array.Clear(bytes));
pending.Dequeue()();
if (!next.Playing) throw new Exception("Old stream disposal stopped the next session.");
unloading.Dispose();
if (pending.Count != 0) throw new Exception("Repeated disposal scheduled duplicate teardown.");
Console.WriteLine("PASS: real FNA pause/resume and queued worker-unload teardown preserve the next session.");
