using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Caverarria;

internal sealed record CaveDataProgress(string Phase, string Message, long Completed = 0, long Total = 0);

/// <summary>Installs only original freeware assets. The Windows executable is inert resource data.</summary>
internal static class CaveStoryDataArchive
{
    internal const string DownloadUrl = "https://www.cavestory.org/downloads/cavestoryen.zip";
    internal const string ArchiveSha256 = "aa87fa30bee9b4980640c7e104791354e0f1f6411ee0d45a70af70046aa0685f";
    internal const long MaximumArchiveBytes = 16 * 1024 * 1024;
    internal const long MaximumExpandedBytes = 64 * 1024 * 1024;
    internal const long MaximumEntryBytes = 16 * 1024 * 1024;
    internal const int MaximumEntries = 4096;
    private static readonly string[] RequiredData =
    [
        "Head.tsc", "ArmsItem.tsc", "npc.tbl", "MyChar.pbm", "Caret.pbm", "TextBox.pbm", "Loading.pbm",
        "Stage/Start.pxm", "Stage/Start.tsc", "Stage/MazeB.pxm", "Stage/MazeB.tsc",
        "Stage/Plant.pxm", "Stage/Plant.tsc", "Stage/PrtCave.pbm", "Stage/Cave.pxa"
    ];

    internal static bool IsInstalled(string dataPath)
    {
        try
        {
            return RequiredData.All(relative => new FileInfo(Path.Combine(dataPath, relative)) is { Exists: true, Length: > 0 })
                && new FileInfo(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataPath))!, "Doukutsu.exe")) is { Exists: true, Length: > 0 }
                && Directory.EnumerateFiles(Path.Combine(dataPath, "Stage"), "*.pxm").Take(90).Count() == 90
                && Directory.EnumerateFiles(Path.Combine(dataPath, "Stage"), "*.tsc").Take(90).Count() == 90;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
    }

    internal static async Task DownloadAndInstallAsync(string assetsPath, Action<CaveDataProgress> report, CancellationToken cancellation)
    {
        Directory.CreateDirectory(assetsPath);
        string temporary = Path.Combine(assetsPath, ".data-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string archivePath = Path.Combine(temporary, "cavestoryen.zip");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Caverarria/0.1 freeware-data-installer");
            report(new("download", "Downloading the original Cave Story freeware data…"));
            using var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0;
            if (total > MaximumArchiveBytes) throw new InvalidDataException("The download exceeds the expected archive size.");
            await using (var incoming = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false))
            await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await CopyBoundedAsync(incoming, output, MaximumArchiveBytes, cancellation,
                    completed => report(new("download", "Downloading the original Cave Story freeware data…", completed, total))).ConfigureAwait(false);
            await InstallArchiveAsync(archivePath, assetsPath, temporary, report, cancellation).ConfigureAwait(false);
        }
        finally { TryDeleteDirectory(temporary); }
    }

    internal static async Task InstallArchiveAsync(string archivePath, string assetsPath, string temporary, Action<CaveDataProgress> report, CancellationToken cancellation)
    {
        report(new("verify", "Checking the freeware download…"));
        await using (var input = File.OpenRead(archivePath))
        {
            if (input.Length > MaximumArchiveBytes) throw new InvalidDataException("The archive exceeds the expected size.");
            string digest = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellation).ConfigureAwait(false)).ToLowerInvariant();
            if (digest != ArchiveSha256) throw new InvalidDataException("The freeware download has changed or is incomplete. Its checksum does not match.");
        }
        string payload = Path.Combine(temporary, "payload");
        Directory.CreateDirectory(payload);
        using (var archive = ZipFile.OpenRead(archivePath))
            await ExtractAssetsAsync(archive, payload, report, cancellation).ConfigureAwait(false);
        if (!IsInstalled(Path.Combine(payload, "data"))) throw new InvalidDataException("The download does not contain the complete Cave Story campaign data.");
        cancellation.ThrowIfCancellationRequested();
        report(new("install", "Installing Cave Story data…"));
        Publish(payload, assetsPath);
        File.WriteAllText(Path.Combine(assetsPath, "data-source.json"), JsonSerializer.Serialize(new
        {
            url = DownloadUrl, sha256 = ArchiveSha256,
            edition = "Cave Story original freeware, Aeon Genesis English translation"
        }, new JsonSerializerOptions { WriteIndented = true }));
        report(new("ready", "Cave Story is ready."));
    }

    internal static async Task ExtractAssetsAsync(ZipArchive archive, string destination, Action<CaveDataProgress> report, CancellationToken cancellation)
    {
        if (archive.Entries.Count > MaximumEntries) throw new InvalidDataException("The archive contains too many files.");
        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        long expanded = 0, copied = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(ZipArchiveEntry Entry, string Target)>();
        foreach (var entry in archive.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            string name = entry.FullName.Replace('\\', '/');
            string[] parts = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (name.StartsWith('/') || name.Contains(':') || name.Contains('\0') || parts.Any(part => part is "." or ".."))
                throw new InvalidDataException("The archive contains an unsafe file path.");
            int kind = (entry.ExternalAttributes >> 16) & 0xf000;
            if (kind == 0xa000 || (entry.ExternalAttributes & 0x400) != 0)
                throw new InvalidDataException("The archive contains a file link.");
            if (parts.Length < 2 || parts[0] != "CaveStory" || name.EndsWith('/')) continue;
            if (!(parts.Length == 2 && parts[1] == "Doukutsu.exe") && !(parts.Length >= 3 && parts[1] == "data")) continue;
            if (entry.Length < 0 || entry.Length > MaximumEntryBytes || entry.Length > MaximumExpandedBytes - expanded)
                throw new InvalidDataException("The archive expands beyond the expected data size.");
            expanded += entry.Length;
            string relative = Path.Combine(parts.Skip(1).ToArray());
            if (!names.Add(relative)) throw new InvalidDataException("The archive contains duplicate file paths.");
            string target = Path.GetFullPath(Path.Combine(destination, relative));
            if (!target.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("The archive path escapes the data folder.");
            entries.Add((entry, target));
        }
        foreach (var (entry, target) in entries)
        {
            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var incoming = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            long actual = await CopyBoundedAsync(incoming, output, Math.Min(entry.Length, MaximumExpandedBytes - copied), cancellation).ConfigureAwait(false);
            if (actual != entry.Length) throw new InvalidDataException("An archive file is incomplete.");
            copied += actual;
            report(new("extract", "Unpacking the original campaign, art, and music…", copied, expanded));
        }
    }

    private static async Task<long> CopyBoundedAsync(Stream input, Stream output, long maximum, CancellationToken cancellation, Action<long>? report = null)
    {
        byte[] buffer = new byte[81920];
        long copied = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
        {
            if (count > maximum - copied) throw new InvalidDataException("A downloaded file exceeds the expected size.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
            copied += count; report?.Invoke(copied);
        }
        return copied;
    }

    private static void Publish(string payload, string assetsPath)
    {
        string data = Path.Combine(assetsPath, "data"), exe = Path.Combine(assetsPath, "Doukutsu.exe");
        string backup = Path.Combine(assetsPath, ".data-backup-" + Guid.NewGuid().ToString("N"));
        bool oldData = Directory.Exists(data), oldExe = File.Exists(exe), newData = false, newExe = false;
        if (oldData || oldExe) Directory.CreateDirectory(backup);
        try
        {
            if (oldData) Directory.Move(data, Path.Combine(backup, "data"));
            if (oldExe) File.Move(exe, Path.Combine(backup, "Doukutsu.exe"));
            Directory.Move(Path.Combine(payload, "data"), data); newData = true;
            File.Move(Path.Combine(payload, "Doukutsu.exe"), exe); newExe = true;
        }
        catch
        {
            if (newExe) File.Delete(exe);
            if (newData) TryDeleteDirectory(data);
            if (Directory.Exists(Path.Combine(backup, "data")) && !Directory.Exists(data)) Directory.Move(Path.Combine(backup, "data"), data);
            if (File.Exists(Path.Combine(backup, "Doukutsu.exe")) && !File.Exists(exe)) File.Move(Path.Combine(backup, "Doukutsu.exe"), exe);
            throw;
        }
        // Existing incomplete assets remain in the backup; campaign saves are never moved.
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
