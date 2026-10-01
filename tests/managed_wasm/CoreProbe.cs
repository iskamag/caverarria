using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WebAssembly.Runtime;

public abstract class CoreExports
{
    public abstract UnmanagedMemory memory { get; }
    public abstract int cave_alloc(int bytes);
    public abstract void cave_free(int pointer,int bytes);
    public abstract int cave_create(int data,int save,int width,int height);
    public abstract int cave_command(int handle,int request);
    public abstract int cave_pixels(int handle,int layer);
    public abstract void cave_destroy(int handle);
    public abstract int cave_last_error();
    public abstract int cave_fs_put(int path,int bytes,int length,int save);
    public abstract int cave_fs_get(int path,int save);
    public abstract int cave_fs_len(int path,int save);
    public abstract int cave_fs_list(int save);
    public abstract int cave_extract_original(int bytes,int length);
    public abstract int cave_audio(int handle,int frames);
    public abstract int cave_audio_length(int handle);
    public abstract int cave_audio_rate();
    public abstract void cave_set_time(long seconds);
}

public static class CoreProbe
{
    public static unsafe void Run(string[] args)
    {
        var wasmPath=Path.GetFullPath(args[0]);
        var root=args.Length>1 ? Path.GetFullPath(args[1]) : Directory.GetCurrentDirectory();
        var dataPath=args.Length>2 && !args[2].StartsWith("--") ? Path.GetFullPath(args[2]) : Path.Combine(root,"runtime/data");
        var compileOnly=args.Contains("--compile-only");
        var timer=Stopwatch.StartNew();
        using var instance=Compile.FromBinary<CoreExports>(wasmPath)(new ImportDictionary());
        timer.Stop(); var compileMilliseconds=timer.Elapsed.TotalMilliseconds;
        var engine=instance.Exports;
        byte[] Read(int pointer,int length)
        {
            if(pointer<0||length<0||(ulong)(uint)pointer+(uint)length>engine.memory.Size) throw new Exception("Host memory range");
            var bytes=new byte[length]; Marshal.Copy(engine.memory.Start+pointer,bytes,0,length); return bytes;
        }
        string CString(int pointer)
        {
            if(pointer<=0||(uint)pointer>=engine.memory.Size) throw new Exception("Invalid string pointer");
            var limit=(int)Math.Min(16*1024*1024,engine.memory.Size-(uint)pointer);
            var span=new ReadOnlySpan<byte>((void*)(engine.memory.Start+pointer),limit);
            var end=span.IndexOf((byte)0); if(end<0) throw new Exception("Unterminated string");
            return Encoding.UTF8.GetString(span[..end]);
        }
        int PutBytes(byte[] bytes)
        {
            if(bytes.Length==0) return 0;
            int pointer=engine.cave_alloc(bytes.Length); if(pointer<=0) throw new Exception("Allocation failed");
            if((ulong)(uint)pointer+(uint)bytes.Length>engine.memory.Size) throw new Exception("Allocation range");
            Marshal.Copy(bytes,0,engine.memory.Start+pointer,bytes.Length); return pointer;
        }
        int PutString(string value)=>PutBytes(Encoding.UTF8.GetBytes(value+"\0"));
        void VfsPut(string path,byte[] bytes,int save)
        {
            int pathPtr=PutString(path),dataPtr=PutBytes(bytes);
            try { if(engine.cave_fs_put(pathPtr,dataPtr,bytes.Length,save)!=0) throw new Exception(CString(engine.cave_last_error())); }
            finally { engine.cave_free(pathPtr,Encoding.UTF8.GetByteCount(path)+1); if(bytes.Length>0) engine.cave_free(dataPtr,bytes.Length); }
        }
        if(compileOnly)
        {
            Console.WriteLine(JsonSerializer.Serialize(new{scope="Real core CIL compilation and zero-import instantiation only",wasmPath,
                wasmSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(wasmPath))).ToLowerInvariant(),
                compileMilliseconds,memoryBytes=engine.memory.Size,runtime=RuntimeInformation.FrameworkDescription},new JsonSerializerOptions{WriteIndented=true}));
            return;
        }
        var load=Stopwatch.StartNew();
        var files=Directory.EnumerateFiles(dataPath,"*",SearchOption.AllDirectories).ToArray();
        foreach(var file in files) VfsPut("/"+Path.GetRelativePath(dataPath,file).Replace('\\','/'),File.ReadAllBytes(file),0);
        var original=File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(dataPath)!,"Doukutsu.exe")); var executable=PutBytes(original);
        try { if(engine.cave_extract_original(executable,original.Length)!=0) throw new Exception(CString(engine.cave_last_error())); }
        finally {engine.cave_free(executable,original.Length);}
        engine.cave_set_time(1);
        int dataString=PutString("/data"),saveString=PutString("/save");
        var handle=engine.cave_create(dataString,saveString,320,240);
        engine.cave_free(dataString,6);engine.cave_free(saveString,6);
        if(handle==0) throw new Exception(CString(engine.cave_last_error()));
        load.Stop();
        JsonDocument Command(string json)
        {
            var request=PutString(json);
            try
            {
                var response=JsonDocument.Parse(CString(engine.cave_command(handle,request)));
                if(response.RootElement.TryGetProperty("ok",out var ok)&&!ok.GetBoolean()) throw new Exception(response.RootElement.ToString());
                return response;
            }
            finally {engine.cave_free(request,Encoding.UTF8.GetByteCount(json)+1);}
        }
        try
        {
            using var initial=Command("{\"op\":\"snapshot\"}");
            if(initial.RootElement.GetProperty("stage").GetProperty("id").GetInt32()!=13) throw new Exception("Expected original fresh Start");
            var initialState=new {stage=initial.RootElement.GetProperty("stage").GetProperty("id").GetInt32(),
                stages=initial.RootElement.GetProperty("stages").GetArrayLength(),
                timingHz=initial.RootElement.GetProperty("timing_hz").GetInt32(),
                player=initial.RootElement.GetProperty("player").Clone()};
            // Native-only ordinary confirm controls. This standalone engine
            // diagnostic is separate from the hosted Terraria campaign trace.
            for(var i=0;i<50;i++) { using var response=Command($"{{\"op\":\"tick\",\"external\":false,\"controls\":{(i%5==0?64:0)}}}"); }
            var timings=new List<object>();
            foreach(var width in new[]{320,426})
            {
                using(var resize=Command($"{{\"op\":\"resize\",\"width\":{width},\"height\":240}}")){}
                for(var warm=0;warm<10;warm++) {using var response=Command("{\"op\":\"tick\",\"external\":false,\"controls\":0}");}
                const int frames=60; var render=Stopwatch.StartNew();long pcmEnergy=0;string frameHash="";var last=default(JsonElement);
                for(var frame=0;frame<frames;frame++)
                {
                    using var response=Command("{\"op\":\"tick\",\"external\":false,\"controls\":0}");
                    last=response.RootElement.Clone();
                    for(var layer=0;layer<3;layer++)
                    {
                        var rgba=Read(engine.cave_pixels(handle,layer),width*240*4);
                        if(frame==frames-1&&layer==0) frameHash=Convert.ToHexString(SHA256.HashData(rgba)).ToLowerInvariant();
                    }
                    var audio=engine.cave_audio(handle,800); var pcm=Read(audio,engine.cave_audio_length(handle));
                    foreach(var sample in MemoryMarshal.Cast<byte,short>(pcm)) pcmEnergy+=Math.Abs((int)sample);
                }
                render.Stop();
                if(pcmEnergy==0) throw new Exception("Original PCM must be nonzero");
                timings.Add(new{width,height=240,frames,millisecondsPerFrame=render.Elapsed.TotalMilliseconds/frames,
                    includes="tick, original software raster, JSON snapshot parse, 3 RGBA copies, 800 stereo PCM frames",pcmEnergy,frameHash,
                    finalStage=last.GetProperty("stage").GetProperty("id").GetInt32()});
            }
            var savePayload=Encoding.UTF8.GetBytes("managed save roundtrip");
            VfsPut("/managed-probe.bin",savePayload,1);var savePath=PutString("/managed-probe.bin");
            try {if(!Read(engine.cave_fs_get(savePath,1),engine.cave_fs_len(savePath,1)).SequenceEqual(savePayload)) throw new Exception("Save memory roundtrip");}
            finally {engine.cave_free(savePath,19);}
            Console.WriteLine(JsonSerializer.Serialize(new{scope="Real engine standalone managed ABI and Start render/audio diagnostic; not Terraria/campaign completion",
                wasmPath,wasmSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(wasmPath))).ToLowerInvariant(),
                runtime=RuntimeInformation.FrameworkDescription,compileMilliseconds,dataLoadAndInitializationMilliseconds=load.Elapsed.TotalMilliseconds,
                loadedOriginalFileCount=files.Length,originalMemoryExtraction=true,saveVfsRoundtrip=true,audioRate=engine.cave_audio_rate(),
                memoryBytes=engine.memory.Size,initialState,timings},new JsonSerializerOptions{WriteIndented=true}));
        }
        finally{engine.cave_destroy(handle);}
    }
}
