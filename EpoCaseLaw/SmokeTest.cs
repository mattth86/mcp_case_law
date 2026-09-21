using System.Diagnostics;
using System.Text.Json;
using EpoCaseLaw.Embeddings;
using EpoCaseLaw.Search;

namespace EpoCaseLaw;

public static class SmokeTest
{
    public static async Task RunAsync()
    {
        var search = new SearchService(Paths.DbPath);
        if (!search.IsReady)
        {
            Console.Error.WriteLine("FAIL: index not built");
            Environment.Exit(1);
        }

        var sw = Stopwatch.StartNew();
        var results = search.SearchDecisions("inventive step", typeCode: "T", limit: 5);
        sw.Stop();
        Console.Error.WriteLine($"search_decisions: {results.RootElement.GetProperty("total").GetInt64()} hits in {sw.ElapsedMilliseconds}ms");
        var emptySnippets = results.RootElement.GetProperty("results").EnumerateArray()
            .Count(r => string.IsNullOrEmpty(r.GetProperty("snippet").GetString()));
        if (emptySnippets > 0)
        {
            Console.Error.WriteLine($"FAIL: {emptySnippets} lexical results have empty snippets (contentless FTS?)");
            Environment.Exit(1);
        }

        var inlineCase = search.SearchDecisions("admissibility of appeal T 664/16", mode: "lexical", limit: 3);
        var topHit = inlineCase.RootElement.GetProperty("results").EnumerateArray().FirstOrDefault();
        var inlineOk = topHit.ValueKind == JsonValueKind.Object && topHit.GetProperty("case_number").GetString() == "T 0664/16";
        Console.Error.WriteLine($"inline case number: top hit {(inlineOk ? "T 0664/16 OK" : "WRONG")}");
        if (!inlineOk)
            Environment.Exit(1);

        var page = search.SearchDecisions("inventive step", typeCode: "T", mode: "lexical", limit: 5, offset: 150);
        var pageHits = page.RootElement.GetProperty("results").GetArrayLength();
        Console.Error.WriteLine($"pagination offset=150: {pageHits} results");
        if (pageHits == 0)
        {
            Console.Error.WriteLine("FAIL: pagination returns nothing beyond the first candidate page");
            Environment.Exit(1);
        }

        var blank = search.SearchDecisions(" ", mode: "hybrid", limit: 3);
        if (!blank.RootElement.TryGetProperty("error", out _))
        {
            Console.Error.WriteLine("FAIL: blank hybrid query returned results");
            Environment.Exit(1);
        }

        var cappedOffset = search.SearchDecisions("inventive step", offset: HybridSearch.MaxCandidateWindow);
        if (!cappedOffset.RootElement.TryGetProperty("error", out _))
        {
            Console.Error.WriteLine("FAIL: offset at candidate cap did not return an error");
            Environment.Exit(1);
        }

        var langFiltered = search.SearchDecisions("erfinderische Tätigkeit", language: "en", mode: "lexical", limit: 10);
        var wrongLang = langFiltered.RootElement.GetProperty("results").EnumerateArray()
            .Count(r => r.GetProperty("language").GetString() != "en");
        Console.Error.WriteLine($"language filter: {wrongLang} wrong-language rows");
        if (wrongLang > 0)
        {
            Console.Error.WriteLine("FAIL: language=en returned non-English text rows");
            Environment.Exit(1);
        }

        var g119 = search.GetDecision("G 1/19", "summary");
        Console.Error.WriteLine($"get_decision G 1/19: {(g119.RootElement.TryGetProperty("error", out _) ? "FAIL" : "OK")}");

        var g119ReasonsPreview = search.GetDecision("G 1/19", "reasons");
        var previewOk = g119ReasonsPreview.RootElement.TryGetProperty("chunks", out var previewChunks)
            && previewChunks.GetArrayLength() > 0
            && g119ReasonsPreview.RootElement.TryGetProperty("next_offset", out _)
            && !g119ReasonsPreview.RootElement.TryGetProperty("Reasons", out _);
        Console.Error.WriteLine($"get_decision reasons preview: {(previewOk ? "OK" : "FAIL")}");
        if (!previewOk)
            Environment.Exit(1);

        var g119Passages = search.GetDecisionPassages("G 1/19", "reasons", offset: 3, limit: 2);
        var passageCount = g119Passages.RootElement.GetProperty("chunks").GetArrayLength();
        Console.Error.WriteLine($"get_decision_passages G 1/19: {passageCount} chunks");
        if (passageCount != 2)
            Environment.Exit(1);

        var g119PassageSearch = search.SearchDecisionPassages("G 1/19", "COMVIK", mode: "lexical", limit: 3);
        var passageSearchCount = g119PassageSearch.RootElement.GetProperty("results").GetArrayLength();
        Console.Error.WriteLine($"search_decision_passages G 1/19 COMVIK: {passageSearchCount} results");
        if (passageSearchCount == 0)
            Environment.Exit(1);

        var prov = search.LookupProvision("Art. 123(2) EPC");
        Console.Error.WriteLine($"lookup_provision: {prov.RootElement.GetProperty("decision_count").GetInt64()} decisions");

        var gl = search.SearchLegalTexts("clarity of claims", source: "guidelines", limit: 3);
        Console.Error.WriteLine($"search_legal_texts: {gl.RootElement.GetProperty("results").GetArrayLength()} results");

        var glCase = search.SearchLegalTexts("G 1/19 computer simulation", source: "both", limit: 3);
        if (glCase.RootElement.TryGetProperty("error", out _))
        {
            Console.Error.WriteLine("FAIL: search_legal_texts with case number in query");
            Environment.Exit(1);
        }
        Console.Error.WriteLine($"search_legal_texts G 1/19: {glCase.RootElement.GetProperty("results").GetArrayLength()} results");

        var blankLegalText = search.SearchLegalTexts(" ", source: "guidelines", mode: "hybrid");
        if (!blankLegalText.RootElement.TryGetProperty("error", out _))
        {
            Console.Error.WriteLine("FAIL: blank legal-text query returned results");
            Environment.Exit(1);
        }

        var glSection = search.GetLegalTextSection("guidelines", "A-II, 1.1.1");
        Console.Error.WriteLine($"get_legal_text_section A-II, 1.1.1: {(glSection.RootElement.TryGetProperty("error", out _) ? "FAIL" : "OK")}");
        if (glSection.RootElement.TryGetProperty("error", out _))
            Environment.Exit(1);

        var wildcardSection = search.GetLegalTextSection("guidelines", "A_II%");
        if (!wildcardSection.RootElement.TryGetProperty("error", out _))
        {
            Console.Error.WriteLine("FAIL: wildcard-looking section id matched unexpectedly");
            Environment.Exit(1);
        }

        var langFallback = search.GetDecision("G 1/19", "summary", lang: "fr");
        Console.Error.WriteLine($"get_decision lang fallback: {(langFallback.RootElement.TryGetProperty("error", out _) ? "FAIL" : "OK")}");

        if (!ModelDownloader.IsModelPresent())
        {
            var downloader = new ModelDownloader(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ModelDownloader>.Instance);
            await downloader.EnsureModelAsync();
        }

        var embedder = EmbedderCache.TryGet()!;
        var q = embedder.EmbedQuery("inventive step");
        var related = Cosine(q, embedder.EmbedPassage("erfinderische Tätigkeit"));
        var unrelated = Cosine(q, embedder.EmbedPassage("the restaurant serves excellent pasta on Tuesdays"));
        Console.Error.WriteLine($"embedding cosines: related {related:F3} vs unrelated {unrelated:F3} (related must win clearly)");
        if (related - unrelated < 0.03f)
        {
            Console.Error.WriteLine("FAIL: embeddings do not discriminate");
            Environment.Exit(1);
        }

        sw.Restart();
        var hybrid = search.SearchDecisions("can a computer simulation be patented?", mode: "hybrid", limit: 5);
        sw.Stop();
        Console.Error.WriteLine($"hybrid search: mode={hybrid.RootElement.GetProperty("mode").GetString()} in {sw.ElapsedMilliseconds}ms");

        Console.Error.WriteLine("smoke tests passed");
    }

    private static float Cosine(float[] a, float[] b)
    {
        float dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return dot / (MathF.Sqrt(na) * MathF.Sqrt(nb));
    }
}
