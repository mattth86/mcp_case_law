namespace EpoCaseLaw.Indexing;

public static class Chunker
{
    // multilingual-e5-small has a 512-token window; ~1600 chars ≈ 400 tokens
    // leaves headroom for the "passage: " prefix and tokenization variance.
    private const int TargetChars = 1600;
    private const int OverlapChars = 200;

    public static IReadOnlyList<string> SplitText(string text, int targetChars = TargetChars)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text))
            return [];

        if (text.Length <= targetChars)
            return [text];

        // Decision texts use \n\n between paragraphs; PDF-extracted text has single \n lines.
        var separator = text.Contains("\n\n") ? "\n\n" : "\n";
        var paragraphs = text.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var chunks = new List<string>();
        var current = new List<string>();
        var currentLen = 0;

        foreach (var para in paragraphs)
        {
            if (para.Length > targetChars)
            {
                Flush(withOverlap: false);
                foreach (var piece in SplitLongParagraph(para, targetChars))
                    chunks.Add(piece);
                continue;
            }

            if (currentLen + para.Length + 2 > targetChars && current.Count > 0)
                Flush(withOverlap: true);

            current.Add(para);
            currentLen += para.Length + 2;
        }

        Flush(withOverlap: false);
        return chunks;

        void Flush(bool withOverlap)
        {
            if (current.Count == 0)
                return;

            chunks.Add(string.Join(separator, current));
            if (withOverlap && chunks[^1].Length > OverlapChars)
            {
                var tail = chunks[^1][^OverlapChars..];
                var wordStart = tail.IndexOf(' ');
                if (wordStart > 0)
                    tail = tail[(wordStart + 1)..];
                current = [tail];
                currentLen = tail.Length;
            }
            else
            {
                current.Clear();
                currentLen = 0;
            }
        }
    }

    private static IEnumerable<string> SplitLongParagraph(string para, int targetChars)
    {
        var pos = 0;
        while (pos < para.Length)
        {
            var len = Math.Min(targetChars, para.Length - pos);
            if (pos + len < para.Length)
            {
                // Break at the last space so we don't cut words in half.
                var lastSpace = para.LastIndexOf(' ', pos + len - 1, len);
                if (lastSpace > pos + targetChars / 2)
                    len = lastSpace - pos;
            }

            yield return para.Substring(pos, len).Trim();
            pos += len;
        }
    }

    public static string BuildEssence(string? headword, string? keywords, string? catchwords, string? headnote)
    {
        var parts = new[] { headword, keywords, catchwords, headnote }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        return string.Join("\n\n", parts);
    }
}
