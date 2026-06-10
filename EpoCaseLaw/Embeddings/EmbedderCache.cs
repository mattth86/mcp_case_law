namespace EpoCaseLaw.Embeddings;

/// <summary>
/// Process-wide embedder instance. Loading the ONNX model takes seconds, so the
/// MCP server must not construct an embedder per query.
/// </summary>
public static class EmbedderCache
{
    private static readonly Lock Gate = new();
    private static E5Embedder? _instance;
    private static bool _loadFailed;

    public static E5Embedder? TryGet()
    {
        if (_instance is not null)
            return _instance;
        if (_loadFailed)
            return null;

        lock (Gate)
        {
            if (_instance is not null || _loadFailed || !ModelDownloader.IsModelPresent())
                return _instance;

            try
            {
                _instance = new E5Embedder(Paths.ModelsDir);
            }
            catch (Exception ex)
            {
                _loadFailed = true;
                Console.Error.WriteLine(
                    $"Embedding model load failed; hybrid search disabled for this process: {ex.GetType().Name}: {ex.Message}");
            }

            return _instance;
        }
    }
}
