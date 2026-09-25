namespace CardiTrack.Mobile.Core.Forms;

/// <summary>The colour an action button wears; each is a meaning, not a decoration.</summary>
public enum ActionTone
{
    /// <summary>Do the thing: Save, Connect, Add, Edit, and any step forward not named below.</summary>
    Blue,

    /// <summary>Yes.</summary>
    Green,

    /// <summary>No, and anything that removes or ends: Delete, Remove, Leave, Sign out.</summary>
    Red,

    /// <summary>Back out with nothing changed: Cancel, Later, Keep.</summary>
    Dark,

    /// <summary>Undo.</summary>
    Amber,
}

/// <summary>An action button's colour and icon (an <c>icon_btn_*</c> file name, or null for none).</summary>
public sealed record ActionLook(ActionTone Tone, string? Icon);

/// <summary>
/// How an action button looks, from the words on it — for the popups whose buttons are worded by
/// their callers (<c>AppPopupPage</c>), so "Remove" is red with a bin wherever it is asked for,
/// without every call site choosing its own colour. Screens that know their buttons set the
/// style directly; this is the rule they follow too.
/// </summary>
public static class ActionLooks
{
    /// <param name="label">The button's text, as shown.</param>
    /// <param name="isDismiss">
    /// True for the button that backs out of a dialog (its Cancel slot). Whatever it says, it is
    /// the way out — dark, unless it is a plain "No", which answers a question and is red.
    /// </param>
    public static ActionLook For(string? label, bool isDismiss = false)
    {
        var text = (label ?? string.Empty).Trim().ToLowerInvariant();
        var first = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

        if (isDismiss)
        {
            return first switch
            {
                "no" => new ActionLook(ActionTone.Red, "icon_btn_close.svg"),
                "later" or "not" => new ActionLook(ActionTone.Dark, "icon_btn_later.svg"),
                _ => new ActionLook(ActionTone.Dark, "icon_btn_close.svg"),
            };
        }

        return first switch
        {
            "yes" => new ActionLook(ActionTone.Green, "icon_check_white.svg"),
            "no" => new ActionLook(ActionTone.Red, "icon_btn_close.svg"),
            "delete" or "remove" => new ActionLook(ActionTone.Red, "icon_btn_delete.svg"),
            "leave" or "revoke" or "discard" or "decline" => new ActionLook(ActionTone.Red, "icon_btn_close.svg"),
            "sign" when text.StartsWith("sign out", StringComparison.Ordinal) => new ActionLook(ActionTone.Red, null),
            "undo" => new ActionLook(ActionTone.Amber, "icon_btn_undo.svg"),
            "cancel" or "later" or "keep" => new ActionLook(ActionTone.Dark, "icon_btn_close.svg"),
            "save" => new ActionLook(ActionTone.Blue, "icon_btn_save.svg"),
            "connect" => new ActionLook(ActionTone.Blue, "icon_btn_connect.svg"),
            "add" => new ActionLook(ActionTone.Blue, "icon_btn_plus.svg"),
            "edit" => new ActionLook(ActionTone.Blue, "icon_btn_edit.svg"),
            "close" or "resolve" => new ActionLook(ActionTone.Blue, "icon_btn_resolve.svg"),
            "ok" or "done" or "confirm" or "acknowledge" => new ActionLook(ActionTone.Blue, "icon_check_white.svg"),
            _ => new ActionLook(ActionTone.Blue, null),
        };
    }
}
