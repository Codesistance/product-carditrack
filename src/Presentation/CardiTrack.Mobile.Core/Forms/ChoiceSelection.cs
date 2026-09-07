namespace CardiTrack.Mobile.Core.Forms;

/// <summary>
/// The selection rule behind the app's own dropdown, kept out of the control so it can be pinned
/// without a MAUI host.
/// </summary>
/// <remarks>
/// <para>
/// The rule is the platform <c>Picker</c>'s, because the pages were written against that and
/// should not have to learn a new one: an index is held inside the options it is given, with
/// <c>-1</c> for none; a request past the last row settles on the last row; swapping the options
/// drops the selection to none, since the old index no longer means anything against a new
/// list; and a change is reported only when the index actually moved.
/// </para>
/// <para>
/// That last clause is the one that matters. The alarm builder rebuilds every dependent list on
/// every edit and then re-selects into it — a control that announced a change for each rebuild
/// would send that page round its own refresh loop.
/// </para>
/// </remarks>
public sealed class ChoiceSelection
{
    /// <summary>What can be chosen. Empty until the caller supplies a list.</summary>
    public IReadOnlyList<string> Options { get; private set; } = [];

    /// <summary>The chosen row, or <c>-1</c> for none. Always inside <see cref="Options"/>.</summary>
    public int SelectedIndex { get; private set; } = -1;

    /// <summary>The chosen label, or null for none.</summary>
    public string? SelectedItem =>
        SelectedIndex >= 0 && SelectedIndex < Options.Count ? Options[SelectedIndex] : null;

    /// <summary>
    /// Replaces the options and clears the selection — the old index pointed into a list that
    /// is gone.
    /// </summary>
    /// <returns>Whether the selection moved, which it did unless there was none.</returns>
    public bool SetOptions(IReadOnlyList<string>? options)
    {
        Options = options ?? [];
        return Select(-1);
    }

    /// <summary>Selects a row by index, held inside the options.</summary>
    /// <returns>Whether the selection moved.</returns>
    public bool Select(int index)
    {
        var settled = Clamp(index, Options.Count);
        if (settled == SelectedIndex)
            return false;

        SelectedIndex = settled;
        return true;
    }

    /// <summary>Selects a row by label; an unknown label, or null, selects none.</summary>
    /// <returns>Whether the selection moved.</returns>
    public bool Select(string? item)
    {
        var index = -1;
        if (item is not null)
        {
            for (var i = 0; i < Options.Count; i++)
            {
                if (Options[i] == item)
                {
                    index = i;
                    break;
                }
            }
        }

        return Select(index);
    }

    /// <summary>Holds a requested index inside a list of <paramref name="count"/> rows, or at none.</summary>
    public static int Clamp(int index, int count) => Math.Clamp(index, -1, Math.Max(-1, count - 1));
}
