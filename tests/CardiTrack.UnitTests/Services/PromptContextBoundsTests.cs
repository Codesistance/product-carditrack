using CardiTrack.Infrastructure.Services;
using Xunit;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The caps and cuts that decide what survives into a member-context section.
/// </summary>
public class PromptContextBoundsTests
{
    /// <summary>An emoji, which is a surrogate pair — two chars, one character.</summary>
    private const string Emoji = "\U0001F642";

    [Fact]
    public void CutTo_returns_the_text_unchanged_when_it_already_fits()
    {
        Assert.Equal("short", MedicalPromptBlocks.CutTo("short", 10));
        Assert.Equal("exact", MedicalPromptBlocks.CutTo("exact", 5));
    }

    [Fact]
    public void CutTo_cuts_plain_text_at_the_limit()
    {
        Assert.Equal("abcde", MedicalPromptBlocks.CutTo("abcdefgh", 5));
    }

    /// <summary>
    /// The cut used to be <c>text[..max]</c> everywhere. A boundary landing between the halves of
    /// an emoji left a lone surrogate on the end of the string — not valid UTF-16 by itself, so it
    /// reached the request body and serialised to a replacement character.
    /// </summary>
    [Fact]
    public void CutTo_never_leaves_half_an_emoji()
    {
        // "abcd" + high surrogate + low surrogate: a cut at 5 would split the pair.
        var text = "abcd" + Emoji;
        var cut = MedicalPromptBlocks.CutTo(text, 5);

        Assert.Equal("abcd", cut);
        Assert.DoesNotContain(cut, c => char.IsSurrogate(c));
    }

    [Fact]
    public void CutTo_keeps_a_pair_that_fits_whole()
    {
        var text = "abcd" + Emoji + "efgh";
        Assert.Equal("abcd" + Emoji, MedicalPromptBlocks.CutTo(text, 6));
    }

    /// <summary>
    /// <see cref="MedicalPromptBlocks.Flatten"/> truncates a caregiver note, and it is the one cut
    /// whose input is unbounded free text a person typed — the likeliest place for an emoji to sit
    /// on the boundary.
    /// </summary>
    [Fact]
    public void Flatten_truncates_a_long_note_without_splitting_a_pair()
    {
        var note = new string('a', MedicalPromptBlocks.MaxNoteLength - 1) + Emoji + "tail";

        var flattened = MedicalPromptBlocks.Flatten(note);

        Assert.EndsWith("… (truncated)", flattened);
        Assert.DoesNotContain(flattened, char.IsSurrogate);
    }

    [Fact]
    public void Flatten_collapses_newlines_so_a_note_cannot_leave_its_line()
    {
        Assert.Equal(
            "first second --- Member --- third",
            MedicalPromptBlocks.Flatten("first\n second\r\n--- Member ---\tthird"));
    }
}
