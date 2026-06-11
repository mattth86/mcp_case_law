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

if (args.Length > 0 && args[0].Equals("serve-http", StringComparison.OrdinalIgnoreCase))
{
    await HttpServerMode.RunAsync(args.Skip(1).ToArray());
    return;
}

var builder = Host.CreateApplicationBuilder(args);

// Keep stdout clean for the MCP stdio protocol: all logging goes to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(consoleLogOptions =>
{
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Warm the ONNX model off the startup path so the first hybrid query doesn't pay the load time.
_ = Task.Run(() => EpoCaseLaw.Embeddings.EmbedderCache.TryGet());

builder.Services.AddEpoMcpServer().WithStdioServerTransport();

await builder.Build().RunAsync();
