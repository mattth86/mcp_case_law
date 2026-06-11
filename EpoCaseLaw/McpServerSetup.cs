using EpoCaseLaw.Search;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace EpoCaseLaw;

public static class McpServerSetup
{
    public const string ServerInstructions = """
        EPO case law MCP server. Corpus: Boards of Appeal decisions (March 2026 XML dump),
        Case Law of the Boards of Appeal 11th ed. 2025, and EPC Guidelines for Examination 2026.
        Cite decisions by case number and ECLI; cite book sections as CLB <section> or GL <section>.
        Iterate: search with filters, fetch decision parts, follow citations, read Guidelines/CLB sections.
        For long decision reasons/full text, use get_decision_passages for pagination or
        search_decision_passages for targeted passages rather than fetching the whole text.
        Exact-term queries (article numbers, case numbers) and conceptual queries both work; hybrid search
        activates automatically once embeddings are built (dotnet run --project EpoCaseLaw -- embed).
        """;

    public static IMcpServerBuilder AddEpoMcpServer(this IServiceCollection services)
    {
        services.AddSingleton(new SearchService(Paths.DbPath));
        return services
            .AddMcpServer(o => o.ServerInstructions = ServerInstructions)
            .WithToolsFromAssembly();
    }
}
