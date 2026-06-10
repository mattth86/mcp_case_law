using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace EpoCaseLaw.Db;

public static class SqliteVecFetcher
{
    private const string Version = "0.1.6";

    public static async Task EnsureAsync(ILogger logger, CancellationToken ct = default)
    {
        var dest = Paths.SqliteVecLib;
        if (File.Exists(dest))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var asset = ResolveAssetName();
        var url = $"https://github.com/asg017/sqlite-vec/releases/download/v{Version}/{asset}";
        var tempTar = Path.Combine(Path.GetTempPath(), $"vec0-{Version}.tar.gz");

        logger.LogInformation("Downloading sqlite-vec {Version}...", Version);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EpoCaseLaw", "1.0"));
        await using (var stream = await http.GetStreamAsync(url, ct).ConfigureAwait(false))
        await using (var fs = File.Create(tempTar))
            await stream.CopyToAsync(fs, ct).ConfigureAwait(false);

        ExtractVecLibrary(tempTar, dest);
        File.Delete(tempTar);
        logger.LogInformation("Installed sqlite-vec extension at {Path}", dest);
    }

    private static void ExtractVecLibrary(string tarGzPath, string destPath)
    {
        var libName = OperatingSystem.IsWindows() ? "vec0.dll" : OperatingSystem.IsMacOS() ? "vec0.dylib" : "vec0.so";
        using var fileStream = File.OpenRead(tarGzPath);
        using var gzip = new GZipStream(fileStream, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        TarEntry? entry;
        while ((entry = reader.GetNextEntry(copyData: true)) is not null)
        {
            if (entry.EntryType != TarEntryType.RegularFile)
                continue;
            if (!entry.Name.EndsWith(libName, StringComparison.OrdinalIgnoreCase))
                continue;

            using var outStream = File.Create(destPath);
            entry.DataStream!.CopyTo(outStream);
            return;
        }

        throw new InvalidOperationException($"Could not find {libName} in sqlite-vec archive.");
    }

    private static string ResolveAssetName()
    {
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return $"sqlite-vec-{Version}-loadable-macos-aarch64.tar.gz";
        if (OperatingSystem.IsMacOS())
            return $"sqlite-vec-{Version}-loadable-macos-x86_64.tar.gz";
        if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return $"sqlite-vec-{Version}-loadable-linux-aarch64.tar.gz";
        if (OperatingSystem.IsWindows())
            return $"sqlite-vec-{Version}-loadable-windows-x86_64.tar.gz";
        return $"sqlite-vec-{Version}-loadable-linux-x86_64.tar.gz";
    }
}
