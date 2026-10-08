using System.Text;

namespace Mpgsql.Internal;

// PostgreSQL uses the original UTF8 password when SASLprep rejects it. The Unicode 3.2
// RFC 3454 tables are generated into SaslPrepTables; normalization is only on startup.
internal static class SaslPrep
{
    internal static string Normalize(string password)
    {
        var mapped = new StringBuilder(password.Length);
        foreach (var rune in password.EnumerateRunes())
        {
            var value = rune.Value;
            // PostgreSQL gives space mapping priority for U+200B, present in both tables.
            if (SaslPrepTables.In(SaslPrepTables.NonAsciiSpace, value))
            {
                mapped.Append(' ');
            }
            else if (!SaslPrepTables.In(SaslPrepTables.MappedToNothing, value))
            {
                mapped.Append(rune.ToString());
            }
        }
        var text = mapped.ToString();
        if (text.Length == 0)
        {
            return password;
        }
        bool randal = false, lcat = false;
        int first = -1, last = -1;
        // Match PostgreSQL's pg_saslprep: prohibited/unassigned and bidi checks inspect
        // the mapped input, while the returned successful value is NFKC-normalized.
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (first < 0)
            {
                first = value;
            }
            last = value;
            if (SaslPrepTables.In(SaslPrepTables.Prohibited, value) || SaslPrepTables.In(SaslPrepTables.Unassigned, value))
            {
                return password;
            }
            randal |= SaslPrepTables.In(SaslPrepTables.RandAL, value);
            lcat |= SaslPrepTables.In(SaslPrepTables.LCat, value);
        }
        if (randal && (lcat || !SaslPrepTables.In(SaslPrepTables.RandAL, first) || !SaslPrepTables.In(SaslPrepTables.RandAL, last)))
        {
            return password;
        }
        try { return text.Normalize(NormalizationForm.FormKC); }
        catch (ArgumentException) { return password; }
    }
}