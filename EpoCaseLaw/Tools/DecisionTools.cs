using System.ComponentModel;
using EpoCaseLaw.Indexing;
using EpoCaseLaw.Search;
using ModelContextProtocol.Server;

namespace EpoCaseLaw.Tools;

[McpServerToolType]
public sealed class DecisionTools(SearchService search)
{
    [McpServerTool, Description("Search EPO Boards of Appeal decisions by keyword, case number, or concept. Supports filters on board, decision type, date range, language, IPC prefix, legal provision, and headnote presence. Returns ranked hits with snippets for citation.")]
    public string SearchDecisions(
        [Description("Search query: keywords, phrases in quotes, or a case number like T 0664/16")] string query,
        [Description("Board code filter, e.g. 3.3.01 or EBA")] string? board = null,
        [Description("Appeal type code: G, T, J, D, W, or R")] string? type_code = null,
        [Description("Earliest decision date (yyyy-MM-dd)")] string? date_from = null,
        [Description("Latest decision date (yyyy-MM-dd)")] string? date_to = null,
        [Description("Language of the matched text/snippet: en, de, or fr")] string? language = null,
        [Description("Normalized legal provision, e.g. Art. 56 EPC or Art. 13(2) RPBA 2020")] string? provision = null,
        [Description("IPC classification prefix, e.g. A61K")] string? ipc_prefix = null,
        [Description("Only return decisions with a published headnote")] bool only_with_headnote = false,
        [Description("Search mode: auto (default), hybrid, or lexical")] string mode = "auto",
        [Description("Maximum results to return (default 10, max 25)")] int limit = 10,
        [Description("Result offset for pagination")] int offset = 0) =>
        search.SearchDecisions(
            query, board, type_code, date_from, date_to, language,
            provision is null ? null : LegalRefNormalizer.NormalizeQueryProvision(provision),
            ipc_prefix, only_with_headnote, mode, Math.Clamp(limit, 1, 25), offset).RootElement.GetRawText();

    [McpServerTool, Description("Retrieve a decision by case number (e.g. G 1/19, T 0664/16) or ECLI. Use summary first; for long reasons/full text prefer get_decision_passages or search_decision_passages.")]
    public string GetDecision(
        [Description("Case number or ECLI")] string case_number,
        [Description("Part to return: summary (default), facts, reasons, order, or full")] string part = "summary",
        [Description("Language code for translated text: en, de, or fr")] string? lang = null) =>
        search.GetDecision(case_number, part, lang).RootElement.GetRawText();

    [McpServerTool, Description("Retrieve a long decision part in bounded chunks. Use this for reasons/full text to avoid MCP response truncation.")]
    public string GetDecisionPassages(
        [Description("Case number or ECLI")] string case_number,
        [Description("Part to return: facts, reasons, order, headnote, catchwords, or full")] string part = "reasons",
        [Description("Language code for translated text: en, de, or fr")] string? lang = null,
        [Description("Chunk offset for pagination")] int offset = 0,
        [Description("Maximum chunks to return (default 3, max 10)")] int limit = 3,
        [Description("Approximate maximum text characters to return across all chunks (default 6000, max 20000)")] int max_chars = 6000) =>
        search.GetDecisionPassages(case_number, part, lang, offset, Math.Clamp(limit, 1, 10), Math.Clamp(max_chars, 1000, 20000))
            .RootElement.GetRawText();

    [McpServerTool, Description("Search within one decision's passages, e.g. find where G 1/19 discusses COMVIK or T 1227/05.")]
    public string SearchDecisionPassages(
        [Description("Case number or ECLI")] string case_number,
        [Description("Search query within the decision")] string query,
        [Description("Part to search: facts, reasons, order, headnote, catchwords, or full")] string part = "reasons",
        [Description("Language code for translated text: en, de, or fr")] string? lang = null,
        [Description("Search mode: auto (default), hybrid, vector, or lexical")] string mode = "auto",
        [Description("Maximum passages to return (default 5, max 10)")] int limit = 5) =>
        search.SearchDecisionPassages(case_number, query, part, lang, mode, Math.Clamp(limit, 1, 10))
            .RootElement.GetRawText();

    [McpServerTool, Description("Explore the decision citation graph: what a decision cites, or which later decisions cite it.")]
    public string GetDecisionCitations(
        [Description("Case number or ECLI")] string case_number,
        [Description("Direction: cites (outgoing) or cited_by (incoming)")] string direction = "cites") =>
        search.GetDecisionCitations(case_number, direction).RootElement.GetRawText();
}
