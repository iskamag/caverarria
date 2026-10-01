using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WebAssembly;
using WebAssembly.Runtime;

namespace Caverarria;

// Public so the managed WASM compiler can derive its generated export bindings.
public abstract class CaveWasmExports
{
    public abstract UnmanagedMemory memory { get; }
    public abstract int cave_alloc(int length);
    public abstract void cave_free(int pointer, int length);
    public abstract void cave_set_time(long seconds);
    public abstract int cave_fs_put(int path, int bytes, int length, int save);
    public abstract int cave_fs_get(int path, int save);
    public abstract int cave_fs_len(int path, int save);
    public abstract int cave_fs_list(int save);
    public abstract long cave_fs_revision(int save);
    public abstract int cave_extract_original(int bytes, int length);
    public abstract int cave_create(int data, int save, int width, int height);
    public abstract int cave_last_error();
    public abstract int cave_command(int handle, int request);
    public abstract int cave_pixels(int handle, int layer);
    public abstract int cave_audio_rate();
    public abstract int cave_audio(int handle, int frames);
    public abstract int cave_audio_length(int handle);
    public abstract void cave_destroy(int handle);
}

/// <summary>The original Rust engine compiled to .NET code with no host imports.</summary>
internal sealed class WasmEngine : ICampaignEngine
{
    private readonly Instance<CaveWasmExports> instance;
    private CaveWasmExports Exports => instance.Exports;
    private readonly string saveDirectory;
    private readonly Dictionary<string, string> saveNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]> persisted = new(StringComparer.OrdinalIgnoreCase);
    private int handle;
    private long saveRevision;
    private readonly object gate = new();
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool HasPcmAudio => true;
    public int AudioSampleRate => Exports.cave_audio_rate();

    public WasmEngine(byte[] module, string dataPath, string savePath, int width, int height)
    {
        using var binary = new MemoryStream(module, false);
        // An empty import table grants the guest no filesystem, network, device,
        // process, or CLR-reflection functions. Assets and saves are copied below.
        instance = Compile.FromBinary<CaveWasmExports>(binary)(new ImportDictionary());
        saveDirectory = Path.GetFullPath(savePath);
        try
        {
            Exports.cave_set_time(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            Preload(dataPath, false);
            string executable = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataPath))!, "Doukutsu.exe");
            if (!File.Exists(Path.Combine(dataPath, "stage.sect")))
            {
                using var original = Allocate(File.ReadAllBytes(executable));
                if (Exports.cave_extract_original(original.Pointer, original.Length) != 0)
                    throw new InvalidOperationException(ReadString(Exports.cave_last_error()));
            }
            Directory.CreateDirectory(saveDirectory);
            Preload(saveDirectory, true);
            saveRevision = Exports.cave_fs_revision(1);
            using var data = AllocateString("/data");
            using var save = AllocateString("/save");
            handle = Exports.cave_create(data.Pointer, save.Pointer, width, height);
            if (handle == 0) throw new InvalidOperationException(ReadString(Exports.cave_last_error()));
            Width = width; Height = height;
            FlushSaves();
        }
        catch
        {
            if (handle != 0) Exports.cave_destroy(handle);
            instance.Dispose();
            throw;
        }
    }

    private void Preload(string directory, bool save)
    {
        long total = 0;
        int count = 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order())
        {
            if (++count > 2048) throw new InvalidDataException("Too many campaign files.");
            var information = new FileInfo(file);
            if (information.Length > 16 * 1024 * 1024 || (total += information.Length) > 64 * 1024 * 1024)
                throw new InvalidDataException("Campaign files exceed the expected data size.");
            string relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            byte[] bytes = File.ReadAllBytes(file);
            using var path = AllocateString("/" + relative);
            using var payload = Allocate(bytes);
            if (Exports.cave_fs_put(path.Pointer, payload.Pointer, bytes.Length, save ? 1 : 0) != 0)
                throw new InvalidOperationException(ReadString(Exports.cave_last_error()));
            if (save) { saveNames[relative] = relative; persisted[relative] = bytes; }
        }
    }

    public JsonElement Send(object request)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(handle == 0, this);
            Exports.cave_set_time(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            using var input = AllocateString(request is string raw ? raw : JsonSerializer.Serialize(request));
            using var document = JsonDocument.Parse(ReadString(Exports.cave_command(handle, input.Pointer)));
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                throw new InvalidOperationException(error.GetString());
            FlushSaves();
            return document.RootElement.Clone();
        }
    }

    public JsonElement Resize(int width, int height)
    {
        lock (gate)
        {
            var snapshot = Send(new { op = "resize", width, height });
            Width = snapshot.Field("viewport").Integer("width", width);
            Height = snapshot.Field("viewport").Integer("height", height);
            return snapshot;
        }
    }

    public void CopyPixels(int layer, byte[] destination)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(handle == 0, this);
            int pointer = Exports.cave_pixels(handle, layer);
            if (pointer == 0) Array.Clear(destination);
            else ReadBytes(pointer, destination);
        }
    }

    public void ReadAudio(byte[] destination)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(handle == 0, this);
            if (destination.Length == 0 || destination.Length % 4 != 0)
                throw new ArgumentException("Audio buffer must contain stereo 16-bit frames.", nameof(destination));
            int pointer = Exports.cave_audio(handle, destination.Length / 4);
            if (pointer == 0 || Exports.cave_audio_length(handle) != destination.Length)
                throw new InvalidOperationException("Engine could not render PCM audio.");
            ReadBytes(pointer, destination);
        }
    }

    private void FlushSaves()
    {
        long revision = Exports.cave_fs_revision(1);
        if (revision == saveRevision) return;
        string[] files = JsonSerializer.Deserialize<string[]>(ReadString(Exports.cave_fs_list(1)))!;
        foreach (string file in files)
        {
            string relative = file.TrimStart('/');
            if (relative.Split('/').Any(part => part is "" or "." or "..") || relative.Contains('\\') || relative.Contains(':'))
                throw new InvalidDataException("Engine returned an invalid save path.");
            using var path = AllocateString(file);
            int length = Exports.cave_fs_len(path.Pointer, 1);
            if (length < 0 || length > 16 * 1024 * 1024) throw new InvalidDataException("Engine save exceeds the expected size.");
            var bytes = new byte[length];
            if (length != 0) ReadBytes(Exports.cave_fs_get(path.Pointer, 1), bytes);
            if (persisted.TryGetValue(relative, out var previous) && bytes.AsSpan().SequenceEqual(previous)) continue;
            string name = saveNames.GetValueOrDefault(relative) ?? (relative.Equals("profile.dat", StringComparison.OrdinalIgnoreCase) ? "Profile.dat" : relative);
            string target = Path.GetFullPath(Path.Combine(saveDirectory, name));
            if (!target.StartsWith(saveDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("Engine save escapes the campaign directory.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temporary = target + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, target, true);
            saveNames[relative] = name; persisted[relative] = bytes;
        }
        saveRevision = revision;
    }

    private void CheckRange(int pointer, int length)
    {
        if (length < 0 || (ulong)(uint)pointer + (uint)length > Exports.memory.Size)
            throw new InvalidDataException("Engine returned an invalid memory range.");
    }

    private void ReadBytes(int pointer, byte[] destination)
    {
        CheckRange(pointer, destination.Length);
        Marshal.Copy(Exports.memory.Start + pointer, destination, 0, destination.Length);
    }

    private unsafe string ReadString(int pointer)
    {
        CheckRange(pointer, 1);
        int limit = (int)Math.Min(Exports.memory.Size - (uint)pointer, 8 * 1024 * 1024);
        var bytes = new ReadOnlySpan<byte>((void*)(Exports.memory.Start + pointer), limit);
        int end = bytes.IndexOf((byte)0);
        if (end < 0) throw new InvalidDataException("Engine returned an unterminated string.");
        return Encoding.UTF8.GetString(bytes[..end]);
    }

    private Buffer AllocateString(string value) => Allocate(Encoding.UTF8.GetBytes(value + "\0"));
    private Buffer Allocate(byte[] bytes)
    {
        int size = Math.Max(1, bytes.Length);
        int pointer = Exports.cave_alloc(size);
        if (pointer == 0) throw new OutOfMemoryException("Could not allocate engine input.");
        try { CheckRange(pointer, bytes.Length); Marshal.Copy(bytes, 0, Exports.memory.Start + pointer, bytes.Length); }
        catch { Exports.cave_free(pointer, size); throw; }
        return new Buffer(Exports, pointer, size);
    }

    private sealed class Buffer(CaveWasmExports exports, int pointer, int length) : IDisposable
    {
        public int Pointer => pointer;
        public int Length => length;
        public void Dispose() => exports.cave_free(pointer, length);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (handle == 0) return;
            try { FlushSaves(); }
            finally { Exports.cave_destroy(handle); handle = 0; instance.Dispose(); }
        }
    }
}
