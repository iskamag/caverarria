using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Caverarria;

public static class AdapterProbe
{
    private static void Check(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); }
    public static void Run(string[] args)
    {
        string wasmPath=Path.GetFullPath(args[0]),root=Path.GetFullPath(args[1]);
        string dataPath=args.Length>2 ? Path.GetFullPath(args[2]) : Path.Combine(root,"runtime/data");
        bool extractsOriginal=!File.Exists(Path.Combine(dataPath,"stage.sect"));
        byte[] module=File.ReadAllBytes(wasmPath);
        string saveDirectory=Path.Combine(root,"runtime/managed-wasm-probe/saves",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(saveDirectory);
        var startup=Stopwatch.StartNew();
        JsonElement saved;
        var timings=new List<object>();
        using(var engine=new WasmEngine(module,dataPath,saveDirectory,320,240))
        {
            startup.Stop();
            var initial=engine.Send(new{op="snapshot"});
            Check(initial.Field("stage").Integer("id")==13,"Fresh adapter must start in original Start");
            Check(initial.Field("stages").GetArrayLength()==95,"Original 95 stages");
            Check(engine.HasPcmAudio&&engine.AudioSampleRate==48000,"Original 48 kHz audio");
            // This standalone diagnostic uses ordinary native controls, not the
            // hosted Terraria trace, and makes no campaign-completion claim.
            for(int i=0;i<180;i++) engine.Send(new{op="tick",external=false,controls=i%5==0?64:0});
            foreach(int width in new[]{320,426})
            {
                engine.Resize(width,240);
                byte[][] layers=Enumerable.Range(0,3).Select(_=>new byte[width*240*4]).ToArray();
                byte[] pcm=new byte[3200];
                for(int i=0;i<10;i++) engine.Send(new{op="tick",external=false,controls=0});
                const int frames=60; long pcmEnergy=0,alpha=0;
                var timer=Stopwatch.StartNew();
                for(int i=0;i<frames;i++)
                {
                    engine.Send(new{op="tick",external=false,controls=0});
                    for(int layer=0;layer<3;layer++) engine.CopyPixels(layer,layers[layer]);
                    engine.ReadAudio(pcm);
                    foreach(short sample in MemoryMarshal.Cast<byte,short>(pcm)) pcmEnergy+=Math.Abs((int)sample);
                }
                timer.Stop();
                Check(pcmEnergy>0,"Original music must produce nonzero stereo PCM");
                for(int layer=1;layer<3;layer++) for(int i=3;i<layers[layer].Length;i+=4)alpha+=layers[layer][i];
                Check(alpha>0,"Original software layers must contain visible pixels");
                timings.Add(new{width,height=240,frames,millisecondsPerFrame=timer.Elapsed.TotalMilliseconds/frames,pcmEnergy,alpha,
                    includes="exact mod adapter tick, software raster, JSON parse, three RGBA copies, original stereo PCM, save-revision polling",
                    frameSha256=Convert.ToHexString(SHA256.HashData(layers[0])).ToLowerInvariant()});
            }
            saved=engine.Send(new{op="save"});
        }
        string profile=Path.Combine(saveDirectory,"Profile.dat");
        Check(File.Exists(profile),"Adapter must flush canonical Profile.dat to actual disk");
        byte[] profileBytes=File.ReadAllBytes(profile);
        Check(profileBytes.Length>0,"Native original save must be nonempty");
        var reopen=Stopwatch.StartNew(); JsonElement resumed;
        using(var engine=new WasmEngine(module,dataPath,saveDirectory,320,240))
        {
            // Reopening must load the profile before an explicit retry request.
            resumed=engine.Send(new{op="snapshot"}); reopen.Stop();
            Check(saved.Field("stage").Integer("id")==resumed.Field("stage").Integer("id"),"Original saved stage restored");
            foreach(string field in new[]{"life","max_life","equipment"})
                Check(saved.Field("player").Field(field).ToString()==resumed.Field("player").Field(field).ToString(),"Original saved player "+field+" restored");
            Check(saved.Field("flags").ToString()==resumed.Field("flags").ToString(),"Original campaign flags restored");
            foreach(string field in new[]{"weapons","items"})
                Check(saved.Field(field).ToString()==resumed.Field(field).ToString(),"Original saved "+field+" restored");
            var reloaded=engine.Send(new{op="load"});
            Check(reloaded.Field("stage").Integer("id")==resumed.Field("stage").Integer("id"),"Ordinary retry reloads the same saved stage");
        }
        Console.WriteLine(JsonSerializer.Serialize(new{scope="Exact mod managed adapter standalone .NET 8 render/audio/disk-save reopen diagnostic; not Terraria/campaign completion",
            wasmPath,wasmSha256=Convert.ToHexString(SHA256.HashData(module)).ToLowerInvariant(),runtime=RuntimeInformation.FrameworkDescription,
            dataPath,loadedFileCount=Directory.EnumerateFiles(dataPath,"*",SearchOption.AllDirectories).Count(),originalExecutableMemoryExtraction=extractsOriginal,
            adapterStartupMilliseconds=startup.Elapsed.TotalMilliseconds,reopenMilliseconds=reopen.Elapsed.TotalMilliseconds,
            saveDirectory,profileBytes=profileBytes.Length,profileSha256=Convert.ToHexString(SHA256.HashData(profileBytes)).ToLowerInvariant(),
            stage=saved.Field("stage").Integer("id"),playerLife=saved.Field("player").Integer("life"),commandsUsed=new[]{"snapshot","tick (ordinary controls)","resize","save","load"},
            diskSaveReopen=true,timings},new JsonSerializerOptions{WriteIndented=true}));
    }
}
