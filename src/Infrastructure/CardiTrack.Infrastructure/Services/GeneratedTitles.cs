namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// The one word budget every generated title is held to — the status line's headline, the
/// digest and journal headlines, the chat theme label, and any title a later rewrite adds.
/// </summary>
/// <remarks>
/// <para>
/// Each of those briefs already asks for a handful of words, and each stores behind a character
/// cap that only catches a runaway answer. Neither holds a title to the width it renders in: a
/// seven- or eight-word headline still clears 40 characters and still truncates in the hero
/// card's single line, which tail-clips it mid-thought. Words are what overflow, so words are
/// what is capped, and in one place so a brief that drifts cannot take its guard with it.
/// </para>
/// <para>
/// Counted the way a reader would: whitespace-separated, and a token has to carry a letter or a
/// digit to count, so a stray dash between two halves of a label is punctuation rather than a
/// word. A hyphenated compound is one word.
/// </para>
/// </remarks>
internal static class GeneratedTitles
{
    /// <summary>The most words a generated title may carry. Above this it is not a title.</summary>
    internal const int MaxWords = 6;

    /// <summary>How many words <paramref name="title"/> carries, on the counting rule above.</summary>
    internal static int WordCount(string? title) => Words(title).Length;

    /// <summary>True when <paramref name="title"/> runs past <see cref="MaxWords"/>.</summary>
    internal static bool ExceedsWordCap(string? title) => WordCount(title) > MaxWords;

    /// <summary>
    /// The first <see cref="MaxWords"/> words of <paramref name="title"/>, for the surfaces whose
    /// convention is to cut a runaway label to title length rather than drop it. A title already
    /// within the cap comes back untouched, spacing included.
    /// </summary>
    internal static string TruncateToWordCap(string title)
    {
        if (!ExceedsWordCap(title))
            return title;

        var kept = 0;
        var tokens = title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var end = 0;
        foreach (var token in tokens)
        {
            if (Counts(token))
                kept++;
            end++;
            if (kept == MaxWords)
                break;
        }

        return string.Join(' ', tokens[..end]).TrimEnd('—', '-', ',', ':', ';', ' ');
    }

    private static string[] Words(string? title) =>
        (title ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(Counts)
            .ToArray();

    private static bool Counts(string token) => token.Any(char.IsLetterOrDigit);
}
