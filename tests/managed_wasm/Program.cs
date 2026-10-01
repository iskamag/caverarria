using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using WebAssembly;
using WebAssembly.Runtime;

public abstract class ProbeExports
{
    public abstract int call_host(int value);
    public abstract float f32_mix(float a, float b);
    public abstract double f64_mix(double a, double b);
    public abstract int indirect(int which, int value);
    public abstract int alloc(int size);
    public abstract void free(int pointer, int size);
    public abstract void copy(int destination, int source, int size);
    public abstract void fill(int destination, int value, int size);
    public abstract int grow(int pages);
    public abstract int pages();
    public abstract void raster(int pointer, int width, int height, int frame);
    public abstract UnmanagedMemory memory { get; }
}

public abstract class GlobalsExports
{
    public abstract int Counter { get; set; }
}

public abstract class MultiExports
{
    public abstract (int, long) Pair();
}

public static class Program
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }

    public static unsafe void Main(string[] args)
    {
        if(args[0]=="--core") { CoreProbe.Run(args.Skip(1).ToArray()); return; }
        if(args[0]=="--adapter") { AdapterProbe.Run(args.Skip(1).ToArray()); return; }
        if(args[0]=="--performance") { PerformanceProbe.Run(args.Skip(1).ToArray()); return; }
        var mode = args.Length > 1 ? args[1] : "cil";
        var startup = Stopwatch.StartNew();
        var checks = new List<string>();
        var hostCalls = 0;
        Func<int, int> host = value => { hostCalls++; return value + 7; };
        Action<int,int,int,int> raster;
        Func<int,int> alloc;
        Action<int,int> free;
        Action<int,int,int> copy, fill;
        Func<int,int> grow;
        Func<int> pages;
        Func<int,int> callHost;
        Func<float,float,float> f32;
        Func<double,double,double> f64;
        Func<int,int,int> indirect;
        Func<int,int,byte[]> read;
        Action<int,byte[]> write;
        object memory;
        Instance<ProbeExports> probeInstance;
        using var binary = File.OpenRead(args[0]);
        if (mode == "cil")
        {
            var imports = new ImportDictionary { { "env", "host_delta", new FunctionImport(host) } };
            var instance = Compile.FromBinary<ProbeExports>(binary)(imports);
            probeInstance = instance;
            var e = instance.Exports;
            memory = e.memory;
            raster=e.raster; alloc=e.alloc; free=e.free; copy=e.copy; fill=e.fill;
            grow=e.grow; pages=e.pages; callHost=e.call_host; f32=e.f32_mix; f64=e.f64_mix; indirect=e.indirect;
            read = (pointer, length) => { var bytes = new byte[length]; Marshal.Copy(e.memory.Start + pointer, bytes, 0, length); return bytes; };
            write = (pointer, bytes) => Marshal.Copy(bytes, 0, e.memory.Start + pointer, bytes.Length);
        }
        else throw new ArgumentException("Only the packaged CIL runtime is supported");
        using var lifetime = probeInstance;
        startup.Stop();
        Check(callHost(35)==42 && hostCalls==1, "host import"); checks.Add("host import");
        Check(Math.Abs(f32(4, 2)-7)<1e-6, "f32"); checks.Add("f32");
        Check(Math.Abs(f64(9, 8)-7)<1e-12, "f64 sqrt"); checks.Add("f64 sqrt");
        Check(indirect(0,7)==14 && indirect(1,7)==21, "call_indirect"); checks.Add("call_indirect");
        var pointer=alloc(64);
        write(pointer, Enumerable.Range(0,16).Select(x=>(byte)x).ToArray());
        copy(pointer+4,pointer,12);
        Check(read(pointer+4,12).SequenceEqual(Enumerable.Range(0,12).Select(x=>(byte)x)), "overlapping memory.copy");
        checks.Add("overlapping memory.copy");
        fill(pointer+20,0xab,10); Check(read(pointer+20,10).All(x=>x==0xab), "memory.fill"); checks.Add("memory.fill");
        var oldPages=pages(); Check(grow(1)==oldPages && pages()==oldPages+1, "memory.grow");
        Check(read(pointer+4,12).SequenceEqual(Enumerable.Range(0,12).Select(x=>(byte)x)), "memory preserved after growth");
        checks.Add("memory.grow / memory reacquisition"); free(pointer,64);
        // Independent module: exact mutable-global get/set API.
        var globals = new WebAssembly.Module();
        globals.Globals.Add(new Global { ContentType=WebAssemblyValueType.Int32, IsMutable=true,
            InitializerExpression=[new WebAssembly.Instructions.Int32Constant(3),new WebAssembly.Instructions.End()] });
        globals.Exports.Add(new Export { Name="Counter",Kind=ExternalKind.Global });
        using var globalBinary = new MemoryStream(); globals.WriteToBinary(globalBinary); globalBinary.Position=0;
        if (mode=="cil")
        {
            using var instance=Compile.FromBinary<GlobalsExports>(globalBinary)(new ImportDictionary());
            Check(instance.Exports.Counter==3,"global get"); instance.Exports.Counter=17;
            Check(instance.Exports.Counter==17,"global set");
        }
        else throw new ArgumentException("Only the packaged CIL runtime is supported");
        checks.Add("mutable global get / set");
        var multi = new WebAssembly.Module();
        multi.Types.Add(new WebAssemblyType { Returns=[WebAssemblyValueType.Int32,WebAssemblyValueType.Int64] });
        multi.Functions.Add(new Function()); multi.Exports.Add(new Export { Name="Pair" });
        multi.Codes.Add(new FunctionBody { Code=[new WebAssembly.Instructions.Int32Constant(19),
            new WebAssembly.Instructions.Int64Constant(23),new WebAssembly.Instructions.End()] });
        using var multiBinary=new MemoryStream(); multi.WriteToBinary(multiBinary); multiBinary.Position=0;
        if(mode=="cil")
        {
            using var instance=Compile.FromBinary<MultiExports>(multiBinary)(new ImportDictionary());
            Check(instance.Exports.Pair()==(19,23L),"multivalue");
        }
        else throw new ArgumentException("Only the packaged CIL runtime is supported");
        checks.Add("multivalue function result");
        var timings = new List<object>();
        foreach(var width in new[]{320,426})
        {
            const int height=240,frames=20;
            var bytes=width*height*4; var framebuffer=alloc(bytes); var output=new byte[bytes];
            raster(framebuffer,width,height,0);
            var timer=Stopwatch.StartNew();
            for(var frame=0;frame<frames;frame++) { raster(framebuffer,width,height,frame); output=read(framebuffer,bytes); }
            timer.Stop();
            Check(output[0]==frames-1 && output[3]==255,"RGBA verification");
            timings.Add(new{width,height,frames,millisecondsPerFrame=timer.Elapsed.TotalMilliseconds/frames,
                generatedAndCopiedFramesPerSecond=frames/timer.Elapsed.TotalSeconds});
            free(framebuffer,bytes);
        }
        var pinvoke = AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name == "WebAssembly")
            .SelectMany(a=>a.GetTypes()).SelectMany(t=>t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance))
            .Where(m=>(m.Attributes&MethodAttributes.PinvokeImpl)!=0).Select(m=>$"{m.DeclaringType}.{m.Name}").ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new{mode,runtime=RuntimeInformation.FrameworkDescription,
            startupMilliseconds=startup.Elapsed.TotalMilliseconds,checks,memoryType=memory.GetType().FullName,pinvoke,
            loadedProbeAssemblies=AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name == "WebAssembly")
                .Select(a=>new{name=a.GetName().Name,version=a.GetName().Version?.ToString(),path=a.Location}),timings},new JsonSerializerOptions{WriteIndented=true}));
    }
}
