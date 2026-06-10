using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace EpoCaseLaw.Embeddings;

public sealed class ModelDownloader(ILogger logger)
{
    private static readonly (string File, string Url)[] Files =
    [
        ("model.onnx", "https://huggingface.co/Xenova/multilingual-e5-small/resolve/main/onnx/model.onnx"),
        ("sentencepiece.bpe.model", "https://huggingface.co/intfloat/multilingual-e5-small/resolve/main/sentencepiece.bpe.model"),
    ];

    public async Task<string> EnsureModelAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Paths.ModelsDir);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EpoCaseLaw", "1.0"));

        foreach (var (file, url) in Files)
        {
            var dest = Path.Combine(Paths.ModelsDir, file);
            if (File.Exists(dest) && new FileInfo(dest).Length > 1024)
                continue;

            logger.LogInformation("Downloading {File}...", file);
            await DownloadAtomicallyAsync(http, url, dest, ct).ConfigureAwait(false);
            logger.LogInformation("Downloaded {File} ({Size} bytes)", file, new FileInfo(dest).Length);
        }

        return Paths.ModelsDir;
    }

    private static async Task DownloadAtomicallyAsync(HttpClient http, string url, string dest, CancellationToken ct)
    {
        var temp = Path.Combine(
            Path.GetDirectoryName(dest)!,
            $".{Path.GetFileName(dest)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using var stream = await http.GetStreamAsync(url, ct).ConfigureAwait(false);
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await stream.CopyToAsync(fs, ct).ConfigureAwait(false);
            File.Move(temp, dest, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    public static bool IsModelPresent() =>
        HasUsableFile("model.onnx")
        && HasUsableFile("sentencepiece.bpe.model");

    private static bool HasUsableFile(string file)
    {
        var path = Path.Combine(Paths.ModelsDir, file);
        return File.Exists(path) && new FileInfo(path).Length > 1024;
    }
}
