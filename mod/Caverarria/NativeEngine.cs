using System.Runtime.InteropServices;
using System.Text.Json;

namespace Caverarria;

/// <summary>The original engine owns campaign scripts, enemies, bullets and saves.</summary>
#if CAVERARRIA_NATIVE_DIAGNOSTICS
internal sealed class NativeEngine : ICampaignEngine
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Create(IntPtr data, IntPtr save, int width, int height);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Command(IntPtr handle, IntPtr request);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Pixels(IntPtr handle, int layer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Destroy(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Audio(IntPtr handle, int frames);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AudioRate();
    private readonly IntPtr library;
    private readonly IntPtr handle;
    private readonly Command command;
    private readonly Pixels pixels;
    private readonly Destroy destroy;
    private readonly Audio? audio;
    public bool HasPcmAudio => audio != null;
    public int AudioSampleRate { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    public NativeEngine(string libraryPath, string dataPath, string savePath, int width, int height)
    {
        library = NativeLibrary.Load(libraryPath);
        var create = Marshal.GetDelegateForFunctionPointer<Create>(NativeLibrary.GetExport(library, "cave_create"));
        command = Marshal.GetDelegateForFunctionPointer<Command>(NativeLibrary.GetExport(library, "cave_command"));
        pixels = Marshal.GetDelegateForFunctionPointer<Pixels>(NativeLibrary.GetExport(library, "cave_pixels"));
        destroy = Marshal.GetDelegateForFunctionPointer<Destroy>(NativeLibrary.GetExport(library, "cave_destroy"));
        if (NativeLibrary.TryGetExport(library, "cave_audio", out var audioExport))
        {
            audio = Marshal.GetDelegateForFunctionPointer<Audio>(audioExport);
            AudioSampleRate = Marshal.GetDelegateForFunctionPointer<AudioRate>(NativeLibrary.GetExport(library, "cave_audio_rate"))();
        }
        IntPtr data = Marshal.StringToCoTaskMemUTF8(dataPath), save = Marshal.StringToCoTaskMemUTF8(savePath);
        try { handle = create(data, save, width, height); }
        finally { Marshal.FreeCoTaskMem(data); Marshal.FreeCoTaskMem(save); }
        if (handle == IntPtr.Zero) throw new InvalidOperationException("Cave Story engine could not initialize. Check the freeware data and native engine log.");
        Width = width;
        Height = height;
    }

    public JsonElement Send(object request)
    {
        string json = request is string raw ? raw : JsonSerializer.Serialize(request);
        IntPtr input = Marshal.StringToCoTaskMemUTF8(json);
        try
        {
            var response = command(handle, input);
            string output = Marshal.PtrToStringUTF8(response) ?? throw new InvalidOperationException("Engine returned an empty response.");
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                throw new InvalidOperationException(error.GetString());
            return document.RootElement.Clone();
        }
        finally { Marshal.FreeCoTaskMem(input); }
    }

    public JsonElement Resize(int width, int height)
    {
        var snapshot = Send(new { op = "resize", width, height });
        Width = snapshot.Field("viewport").Integer("width", width);
        Height = snapshot.Field("viewport").Integer("height", height);
        return snapshot;
    }

    public void CopyPixels(int layer, byte[] destination)
    {
        IntPtr source = pixels(handle, layer);
        if (source != IntPtr.Zero) Marshal.Copy(source, destination, 0, destination.Length);
        else Array.Clear(destination);
    }

    public void ReadAudio(byte[] destination)
    {
        if (audio == null) throw new InvalidOperationException("Engine has no PCM output.");
        if (destination.Length == 0 || destination.Length % 4 != 0) throw new ArgumentException("Audio buffer must contain stereo 16-bit frames.", nameof(destination));
        IntPtr source = audio(handle, destination.Length / 4);
        if (source == IntPtr.Zero) throw new InvalidOperationException("Engine could not render PCM audio.");
        Marshal.Copy(source, destination, 0, destination.Length);
    }

    public void Dispose()
    {
        destroy(handle);
        NativeLibrary.Free(library);
    }
}
#endif

internal static class JsonFields
{
    public static JsonElement Field(this JsonElement node, string key) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var result) ? result : default;
    public static float Number(this JsonElement node, string key, float fallback = 0) => node.Field(key).ValueKind == JsonValueKind.Number ? node.Field(key).GetSingle() : fallback;
    public static int Integer(this JsonElement node, string key, int fallback = 0) => (int)node.Number(key, fallback);
    public static string Text(this JsonElement node, string key, string fallback = "") => node.Field(key).ValueKind == JsonValueKind.String ? node.Field(key).GetString()! : fallback;
    public static bool Boolean(this JsonElement node, string key, bool fallback = false) => node.Field(key).ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => fallback };
    public static IEnumerable<JsonElement> Elements(this JsonElement node) => node.ValueKind == JsonValueKind.Array ? node.EnumerateArray() : Enumerable.Empty<JsonElement>();
}
