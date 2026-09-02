using System.Diagnostics;
using System.Net.Http.Headers;
using System.Numerics;

class FlashAirMirror
{
    const int SHORT_OPERATION_TIMEOUT_MS = 5000;
    const int LONG_OPERATION_TIMEOUT_MS = 120000;
    const int OP_DELAY_MS = 50;

    static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    struct DestFileInfo
    {
        public long Size;
        public int FTime;
    }

    static async Task<int> Main(string[] args)
    {
        string ip = args[0];
        string source = Path.GetFullPath(args[1]);
        string destRoot = args[2].Replace('\\', '/').Trim('/');
        int timeoutSec = int.Parse(args[3]);

        Console.WriteLine($"Source: {source}");
        Console.WriteLine($"Dest: /{destRoot}");

        _http.BaseAddress = new Uri($"http://{ip}/");

        if (!await TryConnectAsync(timeoutSec))
            return 1;

        await SetWriteProtectAsync(true);

        Console.WriteLine("Ensuring destination root...");
        await EnsureDirAsync(destRoot);

        Console.WriteLine("Reading destination tree...");
        var destFiles = await ReadDestinationFilesAsync(destRoot);

        Console.WriteLine("Ensuring source directories...");
        await EnsureSourceDirectoriesAsync(source, destRoot);

        Console.WriteLine("Uploading new / changed files...");
        await UploadChangedFilesAsync(source, destRoot, destFiles);

        Console.WriteLine("Computing deletions...");
        var sourceFiles = GetSourceFileSet(source);

        Console.WriteLine("Deleting removed files...");
        await DeleteRemovedFilesAsync(destRoot, destFiles, sourceFiles);

        Console.WriteLine("Cleaning up directories...");
        await DeleteEmptyDirectoriesAsync(destRoot, destFiles, sourceFiles);

        await SetWriteProtectAsync(false);

        Console.WriteLine("Mirror complete.");
        return 0;
    }

    static async Task<bool> TryConnectAsync(int timeoutSeconds)
    {
        Console.Write("Waiting for FlashAir ");
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed.TotalSeconds < timeoutSeconds)
        {
            try
            {
                using var cts = new CancellationTokenSource(SHORT_OPERATION_TIMEOUT_MS);
                await _http.GetAsync(string.Empty, cts.Token);
                Console.WriteLine("\nConnected.");
                return true;
            }
            catch
            {
                Console.Write(".");
                await Task.Delay(1000);
            }
        }

        Console.WriteLine("\nFailed to connect.");
        return false;
    }

    static async Task SetWriteProtectAsync(bool enable)
    {
        string value = enable ? "ON" : "OFF";
        Console.WriteLine($"Setting write protection {value}...");

        using var cts = new CancellationTokenSource(SHORT_OPERATION_TIMEOUT_MS);
        var resp = await _http.GetAsync($"upload.cgi?WRITEPROTECT={value}", cts.Token);
        var body = await resp.Content.ReadAsStringAsync();

        Console.WriteLine(body.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase)
            ? "  OK"
            : $"  FAILED ({body.Trim()})");
    }

    static async Task EnsureDirAsync(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string current = string.Empty;

        foreach (var p in parts)
        {
            current = string.IsNullOrEmpty(current) ? p : $"{current}/{p}";

            using var cts = new CancellationTokenSource(SHORT_OPERATION_TIMEOUT_MS);
            await _http.GetAsync($"upload.cgi?UPDIR=/{current}", cts.Token);

            await Task.Delay(OP_DELAY_MS);
        }
    }

    static async Task EnsureSourceDirectoriesAsync(string source, string destRoot)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, dir).Replace('\\', '/');
            await EnsureDirAsync($"{destRoot}/{rel}");
        }
    }

    static async Task<Dictionary<string, DestFileInfo>> ReadDestinationFilesAsync(string destRoot)
    {
        var map = new Dictionary<string, DestFileInfo>(StringComparer.OrdinalIgnoreCase);
        await ReadDirRecursiveAsync(destRoot, destRoot, map);
        Console.WriteLine($"Found {map.Count} destination files.");
        return map;
    }

    static async Task ReadDirRecursiveAsync(
        string current,
        string destRoot,
        Dictionary<string, DestFileInfo> files)
    {
        using var cts = new CancellationTokenSource(SHORT_OPERATION_TIMEOUT_MS);
        var resp = await _http.GetStringAsync($"command.cgi?op=100&DIR=/{current}", cts.Token);

        foreach (var line in resp.Split('\n').Skip(1))
        {
            var parts = line.Split(',');
            if (parts.Length < 6)
                continue;

            string name = parts[1];
            long size = long.Parse(parts[2]);
            int attr = int.Parse(parts[3]);
            int date = int.Parse(parts[4]);
            int time = int.Parse(parts[5]);
            bool isDir = (attr & 0x10) != 0;

            string full = $"{current}/{name}";
            string rel = full[destRoot.Length..].TrimStart('/');

            if (isDir)
            {
                await ReadDirRecursiveAsync(full, destRoot, files);
            }
            else
            {
                files[rel] = new DestFileInfo
                {
                    Size = size,
                    FTime = (date << 16) | time
                };
            }
        }
    }

    static async Task UploadChangedFilesAsync(
        string source,
        string destRoot,
        Dictionary<string, DestFileInfo> destFiles)
    {
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file).Replace('\\', '/');
            var fi = new FileInfo(file);

            int srcFTime = Date2FTime(fi.LastWriteTime);

            if (destFiles.TryGetValue(rel, out var dest))
            {
                if (dest.Size == fi.Length && 
                    Math.Abs(dest.FTime - srcFTime) <= FTimeToleranceTicks(fi.Length))
                {
                    Console.WriteLine($"Skip (unchanged): {rel}");
                    continue;
                }
            }

            await UploadFileAsync(file, source, destRoot);
        }
    }

    static async Task UploadFileAsync(string localPath, string sourceRoot, string destRoot)
    {
        var relPath = Path.GetRelativePath(sourceRoot, localPath).Replace('\\', '/');
        var fileName = Path.GetFileName(localPath);

        int fTime = Date2FTime(File.GetLastWriteTime(localPath));
        var fTimeHex = $"0x{fTime:X}";

        using (var cts = new CancellationTokenSource(SHORT_OPERATION_TIMEOUT_MS))
            await _http.GetAsync($"upload.cgi?FTIME={fTimeHex}", cts.Token);

        var boundary = "----WebKitFormBoundary7MA4YWxkTrZu0gW";
        var content = new MultipartFormDataContent(boundary);
        byte[] fileBytes = File.ReadAllBytes(localPath);

        // Compensate for FlashAir stripping trailing CRLF
        Array.Resize(ref fileBytes, fileBytes.Length + 2);
        fileBytes[^2] = (byte)'\r';
        fileBytes[^1] = (byte)'\n';

        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
        fileContent.Headers.TryAddWithoutValidation(
            "Content-Disposition",
            $"form-data; name=\"file\"; filename=\"{fileName}\"");

        content.Add(fileContent, "file", fileName);

        var request = new HttpRequestMessage(HttpMethod.Post, "upload.cgi")
        {
            Content = content
        };

        using (var cts = new CancellationTokenSource(LONG_OPERATION_TIMEOUT_MS))
        {
            var resp = await _http.SendAsync(request, cts.Token);
            var body = await resp.Content.ReadAsStringAsync();

            if (!body.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Upload FAILED: {relPath}");
                return;
            }
        }

        Console.WriteLine($"Upload OK: {relPath}");
        await Task.Delay(OP_DELAY_MS);
    }

    static HashSet<string> GetSourceFileSet(string source)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            set.Add(Path.GetRelativePath(source, f).Replace('\\', '/'));
        return set;
    }

    static async Task DeleteRemovedFilesAsync(
        string destRoot,
        Dictionary<string, DestFileInfo> destFiles,
        HashSet<string> sourceFiles)
    {
        foreach (var rel in destFiles.Keys)
        {
            if (sourceFiles.Contains(rel))
                continue;

            Console.WriteLine($"Delete file: {rel}");
            using var cts = new CancellationTokenSource(SHORT_OPERATION_TIMEOUT_MS);
            await _http.GetAsync($"upload.cgi?DEL=/{destRoot}/{rel}", cts.Token);
            await Task.Delay(OP_DELAY_MS);
        }
    }

    static async Task DeleteEmptyDirectoriesAsync(
        string destRoot,
        Dictionary<string, DestFileInfo> destFiles,
        HashSet<string> sourceFiles)
    {
        var liveFiles = destFiles.Keys.Where(sourceFiles.Contains).ToList();

        var dirs = destFiles.Keys
            .Select(p => Path.GetDirectoryName(p)?.Replace('\\', '/'))
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(p => p!.Count(c => c == '/'))
            .ToList();

        foreach (var dir in dirs)
        {
            if (liveFiles.Any(f => f.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase)))
                continue;

            Console.WriteLine($"Delete dir: {dir}");
            using var cts = new CancellationTokenSource(SHORT_OPERATION_TIMEOUT_MS);
            await _http.GetAsync($"upload.cgi?DEL=/{destRoot}/{dir}", cts.Token);
            await Task.Delay(OP_DELAY_MS);
        }
    }

    static int Date2FTime(DateTime dt)
    {
        int date = ((dt.Year - 1980) << 9) | (dt.Month << 5) | dt.Day;
        int time = (dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2);
        return (date << 16) | time;
    }

    static int FTimeToleranceTicks(long sizeBytes)
    {
        int maxTicks = (int)Math.Ceiling(LONG_OPERATION_TIMEOUT_MS / 2000.0);

        int bits = 63 - BitOperations.LeadingZeroCount((ulong)Math.Max(sizeBytes, 1));

        // 256 KB (18 bits) → 0
        // 16 MB  (24 bits) → 48 ticks
        int ticks = Math.Max(0, (bits - 18) * 8);

        return Math.Min(ticks, maxTicks);
    }
}
