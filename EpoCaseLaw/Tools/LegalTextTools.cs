using System.ComponentModel;
using EpoCaseLaw.Search;
using ModelContextProtocol.Server;

namespace EpoCaseLaw.Tools;

[McpServerToolType]
public sealed class LegalTextTools(SearchService search)
{
    [McpServerTool, Description("Search the Case Law of the Boards of Appeal (11th ed. 2025) and/or EPC Guidelines for Examination 2026.")]
    public string SearchLegalTexts(
        [Description("Search query")] string query,
        [Description("Source: case_law_book, guidelines, or both (default)")] string source = "both",
        [Description("Search mode: auto (default), hybrid, or lexical")] string mode = "auto",
        [Description("Maximum results (default 10, max 25)")] int limit = 10) =>
        search.SearchLegalTexts(query, source, mode, Math.Clamp(limit, 1, 25)).RootElement.GetRawText();

    [McpServerTool, Description("Retrieve the full text of a case-law-book or Guidelines section by section ID.")]
    public string GetLegalTextSection(
        [Description("Source: case_law_book or guidelines")] string source,
        [Description("Section identifier, e.g. II.A.3.1 or A-II, 1.1")] string section_id) =>
        search.GetLegalTextSection(source, section_id).RootElement.GetRawText();

    [McpServerTool, Description("Look up how a legal provision is applied across decisions and reference texts.")]
    public string LookupProvision(
        [Description("Provision reference, e.g. Art. 123(2) EPC or Art. 13(2) RPBA")] string provision) =>
        search.LookupProvision(provision).RootElement.GetRawText();
}
