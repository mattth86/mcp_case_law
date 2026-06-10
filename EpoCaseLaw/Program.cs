using EpoCaseLaw;
using EpoCaseLaw.Db;
using EpoCaseLaw.Embeddings;
using EpoCaseLaw.Indexing;
using EpoCaseLaw.Search;
using EpoCaseLaw.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

if (args.Length > 0 && args[0].Equals("index", StringComparison.OrdinalIgnoreCase))
{
    var incremental = args.Contains("--incremental", StringComparer.OrdinalIgnoreCase);
    var booksOnly = args.Contains("--books", StringComparer.OrdinalIgnoreCase);
    using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
    var logger = loggerFactory.CreateLogger<IndexRunner>();
    await new IndexRunner(logger).RunAsync(incremental, booksOnly);
    return;
}

if (args.Length > 0 && args[0].Equals("fix-provisions", StringComparison.OrdinalIgnoreCase))
{
    using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
    var logger = loggerFactory.CreateLogger<IndexRunner>();
    new IndexRunner(logger).FixProvisions(Paths.DbPath);
    return;
}

if (args.Length > 0 && args[0].Equals("smoke", StringComparison.OrdinalIgnoreCase))
{
    await SmokeTest.RunAsync();
    return;
}

if (args.Length > 0 && args[0].Equals("embed", StringComparison.OrdinalIgnoreCase))
{
    string? stage = null;
    for (var i = 1; i < args.Length; i++)
    {
        if (args[i] == "--stage" && i + 1 < args.Length)
            stage = args[++i];
    }

    using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
    var logger = loggerFactory.CreateLogger<EmbedJob>();
    await new EmbedJob(logger).RunAsync(Paths.DbPath, stage);
    return;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(consoleLogOptions =>
{
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton(new SearchService(Paths.DbPath));

// Warm the ONNX model off the startup path so the first hybrid query doesn't pay the load time.
_ = Task.Run(() => EpoCaseLaw.Embeddings.EmbedderCache.TryGet());
builder.Services.AddMcpServer(options =>
{
    options.ServerInstructions = """
        EPO case law MCP server. Corpus: Boards of Appeal decisions (March 2026 XML dump),
        Case Law of the Boards of Appeal 11th ed. 2025, and EPC Guidelines for Examination 2026.
        Cite decisions by case number and ECLI; cite book sections as CLB <section> or GL <section>.
        Iterate: search with filters, fetch decision parts, follow citations, read Guidelines/CLB sections.
        For long decision reasons/full text, use get_decision_passages for pagination or
        search_decision_passages for targeted passages rather than fetching the whole text.
        Exact-term queries (article numbers, case numbers) and conceptual queries both work; hybrid search
        activates automatically once embeddings are built (dotnet run --project EpoCaseLaw -- embed).
        """;
})
.WithStdioServerTransport()
.WithToolsFromAssembly();

await builder.Build().RunAsync();
