using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Caverarria;

public static class PerformanceProbe
{
    private static object Summary(List<double> values,List<long> allocations)
    {
        var ordered=values.Order().ToArray();
        double Percentile(double q)=>ordered[Math.Clamp((int)Math.Ceiling(q*ordered.Length)-1,0,ordered.Length-1)];
        return new {meanMilliseconds=values.Average(),p50Milliseconds=Percentile(.50),p95Milliseconds=Percentile(.95),
            p99Milliseconds=Percentile(.99),maxMilliseconds=ordered[^1],meanAllocatedBytes=allocations.Average(),
            maxAllocatedBytes=allocations.Max()};
    }
    public static void Run(string[] args)
    {
        string wasmPath=Path.GetFullPath(args[0]),root=Path.GetFullPath(args[1]);
        int sampleFrames=args.Length>2?int.Parse(args[2]):300;
        string dataPath=args.Length>3?Path.GetFullPath(args[3]):Path.Combine(root,"runtime/data");
        byte[] module=File.ReadAllBytes(wasmPath);
        string save=Path.Combine(root,"runtime/managed-wasm-probe/performance-saves",Guid.NewGuid().ToString("N"));
        var initialization=Stopwatch.StartNew();
        using var engine=new WasmEngine(module,dataPath,save,320,240);
        initialization.Stop();
        var results=new List<object>();
        foreach(var scene in new[]{"Start","Shelt","SheltDialogue"})
        foreach(var viewport in new[]{(320,240),(426,240),(640,360)})
        {
            int stage=scene=="Start"?13:18;
            engine.Send(new{op="warp",stage,x=160,y=128});
            engine.Resize(viewport.Item1,viewport.Item2);
            // Explicit performance-only fixture selection. These engine debug
            // commands are never recorded as genuine campaign completion.
            if(scene=="SheltDialogue")
            {
                engine.Send(new{op="event",@event=500});
                engine.Send(new{op="tick",controls=0,external=true});
                engine.Send(new{op="event",@event=502});
            }
            else engine.Send(new{op="event",@event=91});
            object tick=new {op="tick",controls=0,weapon=0,external=true,
                player=new{x=160.0,y=128.0,vx=0.0,vy=0.0,width=20.0/3,height=14.0,direction=1,life=3,max_life=3,grounded=true,jump_started=false,wet=false}};
            byte[][] rgba=Enumerable.Range(0,3).Select(_=>new byte[viewport.Item1*viewport.Item2*4]).ToArray();
            byte[] pcm=new byte[3200]; JsonElement snapshot=default;
            for(int frame=0;frame<100;frame++)
            {
                snapshot=engine.Send(tick);
                for(int layer=0;layer<3;layer++) engine.CopyPixels(layer,rgba[layer]);
                engine.ReadAudio(pcm);
            }
            var scriptBefore=snapshot.Text("script"); var songBefore=snapshot.Integer("song");
            var sends=new List<double>(sampleFrames);var copies=new List<double>(sampleFrames);var audio=new List<double>(sampleFrames);var total=new List<double>(sampleFrames);
            var sendAlloc=new List<long>(sampleFrames);var copyAlloc=new List<long>(sampleFrames);var audioAlloc=new List<long>(sampleFrames);var totalAlloc=new List<long>(sampleFrames);
            var process=Process.GetCurrentProcess();process.Refresh();long initialWorkingSet=process.WorkingSet64;
            var gcBefore=new[]{GC.CollectionCount(0),GC.CollectionCount(1),GC.CollectionCount(2)};
            for(int frame=0;frame<sampleFrames;frame++)
            {
                long fullStart=Stopwatch.GetTimestamp(),fullAllocated=GC.GetAllocatedBytesForCurrentThread();
                long start=fullStart,allocated=fullAllocated;
                snapshot=engine.Send(tick);
                sends.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);sendAlloc.Add(GC.GetAllocatedBytesForCurrentThread()-allocated);
                start=Stopwatch.GetTimestamp();allocated=GC.GetAllocatedBytesForCurrentThread();
                for(int layer=0;layer<3;layer++)engine.CopyPixels(layer,rgba[layer]);
                copies.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);copyAlloc.Add(GC.GetAllocatedBytesForCurrentThread()-allocated);
                start=Stopwatch.GetTimestamp();allocated=GC.GetAllocatedBytesForCurrentThread();
                engine.ReadAudio(pcm);
                audio.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);audioAlloc.Add(GC.GetAllocatedBytesForCurrentThread()-allocated);
                total.Add(Stopwatch.GetElapsedTime(fullStart).TotalMilliseconds);totalAlloc.Add(GC.GetAllocatedBytesForCurrentThread()-fullAllocated);
            }
            process.Refresh();long finalWorkingSet=process.WorkingSet64;
            var gcs=Enumerable.Range(0,3).Select(i=>GC.CollectionCount(i)-gcBefore[i]).ToArray();
            var row=new {scene,stage=snapshot.Field("stage").Integer("id"),width=viewport.Item1,height=viewport.Item2,
                warmupFrames=100,sampleFrames,scriptBefore,scriptAfter=snapshot.Text("script"),songBefore,songAfter=snapshot.Integer("song"),
                npcCount=snapshot.Field("npcs").Elements().Count(),snapshotSerializedBytes=System.Text.Encoding.UTF8.GetByteCount(snapshot.GetRawText()),
                send=Summary(sends,sendAlloc),copyThreeLayers=Summary(copies,copyAlloc),read800StereoFrames=Summary(audio,audioAlloc),total=Summary(total,totalAlloc),
                initialWorkingSet,finalWorkingSet,gcCollections=gcs};
            results.Add(row);
            Console.Error.WriteLine($"{scene} {viewport.Item1}x{viewport.Item2}: Send {sends.Average():F3}ms Copy {copies.Average():F3}ms PCM {audio.Average():F3}ms Total {total.Average():F3}ms");
        }
        Console.WriteLine(JsonSerializer.Serialize(new{scope="Standalone exact-adapter performance partition; diagnostic warp/event fixtures, never campaign completion",
            wasmSha256=Convert.ToHexString(SHA256.HashData(module)).ToLowerInvariant(),runtime=RuntimeInformation.FrameworkDescription,
            initializationMilliseconds=initialization.Elapsed.TotalMilliseconds,processId=Environment.ProcessId,
            notes=new[]{"Actual Terraria player fields matched in external-kinematics tick request","Three full RGBA buffers and800 stereo PCM frames per tick",
                "Managed allocation counters exclude guest linear-memory allocator","Results may include live-client contention; no CPU affinity or process suspension"},results},new JsonSerializerOptions{WriteIndented=true}));
    }
}
