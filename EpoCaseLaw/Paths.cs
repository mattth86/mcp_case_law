namespace EpoCaseLaw;

public static class Paths
{
    public const string DefaultDecisionsXml = "EPDecisions_March2026.xml";
    public const string DecisionsXmlPattern = "EPDecisions_*.xml";
    public const string CaseLawBookPdf = "en-case-law-of-the-boards-of-appeal-2025.pdf";
    public const string GuidelinesPdf = "en-epc-guidelines-2026-hyperlinked.pdf";
    public const string DefaultDbFile = "epo.db";

    public static string DataRoot
    {
        get
        {
            // An explicit EPO_DATA_ROOT wins even without source XML present
            // (e.g. a container shipping only epo.db + models/ + native/).
            var explicitRoot = Environment.GetEnvironmentVariable("EPO_DATA_ROOT");
            if (!string.IsNullOrWhiteSpace(explicitRoot))
                return explicitRoot;

            var candidates = new[]
            {
                FindDataRoot(AppContext.BaseDirectory),
                FindDataRoot(Directory.GetCurrentDirectory()),
            };

            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && FindDecisionXml(candidate) is not null)
                    return candidate;
            }

            return Directory.GetCurrentDirectory();
        }
    }

    public static string DecisionsXml => Path.GetFileName(DecisionsXmlPath);

    public static string DecisionsXmlPath =>
        FindDecisionXml(DataRoot) ?? Path.Combine(DataRoot, DefaultDecisionsXml);

    public static string DbPath =>
        Environment.GetEnvironmentVariable("EPO_DB_PATH")
        ?? Path.Combine(DataRoot, DefaultDbFile);

    public static string ModelsDir => Path.Combine(DataRoot, "models");

    public static string SqliteVecLib
    {
        get
        {
            var explicitPath = Environment.GetEnvironmentVariable("EPO_SQLITE_VEC_PATH");
            if (!string.IsNullOrWhiteSpace(explicitPath))
                return explicitPath;

            var ext = OperatingSystem.IsWindows() ? "dll" : OperatingSystem.IsMacOS() ? "dylib" : "so";
            return Path.Combine(DataRoot, "native", $"vec0.{ext}");
        }
    }

    private static string? FindDataRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (FindDecisionXml(dir.FullName) is not null)
                return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }

    private static string? FindDecisionXml(string dir)
    {
        if (!Directory.Exists(dir))
            return null;

        return Directory.EnumerateFiles(dir, DecisionsXmlPattern, SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?.FullName;
    }
}
