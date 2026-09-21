using System.Security.Cryptography;
using System.Text;
using EpoCaseLaw.Db;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;

namespace EpoCaseLaw;

/// <summary>
/// Streamable HTTP transport for remote clients (Copilot Studio / M365 Copilot).
/// Stateless: every tool is a pure request/response SQLite read, and Copilot Studio's
/// infrastructure does not guarantee session affinity.
/// </summary>
public static class HttpServerMode
{
    public static async Task RunAsync(string[] args)
    {
        var port = 5234;
        var noAuth = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var p))
                port = p;
            else if (args[i] == "--no-auth")
                noAuth = true;
        }

        var apiKey = Environment.GetEnvironmentVariable("EPO_MCP_API_KEY");
        if (string.IsNullOrEmpty(apiKey) && !noAuth)
        {
            Console.Error.WriteLine(
                "serve-http refuses to start without authentication: set EPO_MCP_API_KEY " +
                "(clients send it in the x-api-key header), or pass --no-auth for local-only testing.");
            Environment.ExitCode = 1;
            return;
        }

        if (noAuth)
            Console.Error.WriteLine("WARNING: --no-auth — /mcp is unauthenticated. Never expose this through a tunnel.");

        var builder = WebApplication.CreateBuilder();

        builder.Services.AddEpoMcpServer(readOnly: true).WithHttpTransport(o => o.Stateless = true);

        // Warm the ONNX model off the startup path so the first hybrid query doesn't pay the load time.
        _ = Task.Run(() => EpoCaseLaw.Embeddings.EmbedderCache.TryGet());

        var app = builder.Build();

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
        {
            app.Urls.Clear();
            app.Urls.Add($"http://localhost:{port}");
        }

        app.MapGet("/health", () =>
        {
            var dbExists = File.Exists(Paths.DbPath);
            var indexed = dbExists && DbBootstrap.IsIndexed(Paths.DbPath);
            var modelAvailable = File.Exists(Path.Combine(Paths.ModelsDir, "model.onnx"))
                && File.Exists(Path.Combine(Paths.ModelsDir, "tokenizer.json"))
                && File.Exists(Path.Combine(Paths.ModelsDir, "sentencepiece.bpe.model"));
            var vectorExtensionLoaded = false;
            if (indexed)
            {
                try
                {
                    using var connection = DbBootstrap.Open(Paths.DbPath, readOnly: true);
                    vectorExtensionLoaded = DbBootstrap.VecExtensionLoaded(connection);
                }
                catch
                {
                    vectorExtensionLoaded = false;
                }
            }

            var ready = indexed && modelAvailable && vectorExtensionLoaded;
            return Results.Json(new
            {
                status = ready ? "ok" : "degraded",
                ready,
                database = new { exists = dbExists, indexed },
                embeddings = new
                {
                    modelAvailable,
                    vectorExtensionLoaded,
                    searchMode = vectorExtensionLoaded && modelAvailable ? "hybrid" : "lexical",
                },
            }, statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        });

        if (!noAuth)
        {
            var keyBytes = Encoding.UTF8.GetBytes(apiKey!);
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/mcp"))
                {
                    var supplied = context.Request.Headers["x-api-key"].ToString();
                    var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
                    if (!CryptographicOperations.FixedTimeEquals(suppliedBytes, keyBytes))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
                        return;
                    }
                }

                await next(context);
            });
        }

        app.MapMcp("/mcp");

        await app.RunAsync();
    }
}
