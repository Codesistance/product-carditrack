using Microsoft.Maui.Handlers;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// Takes the platform's own underline off every <see cref="Entry"/> and <see cref="Editor"/>.
/// </summary>
/// <remarks>
/// <para>
/// The app's text fields sit inside <c>AuthEntryBorder</c>, which draws the field's edge: white
/// fill, <c>InputBorder</c> hairline, rounded corners. Each platform then drew its own edge as
/// well — Android's Material underline beneath the text, iOS's rounded-rect text field — so every
/// field carried two edges, and the inner one was the OS's, in the OS's colours. The Border is the
/// field; the native chrome was a second edge, and it goes.
/// </para>
/// <para>
/// Done once here, through the handler mappers, rather than per field: there is no XAML property
/// for it, and a property that had to be repeated on every Entry would be forgotten on the next
/// one. The mapping is named so a later mapping can be ordered against it, or remove it.
/// </para>
/// </remarks>
internal static class FieldChrome
{
    public const string MappingName = "NoUnderline";

    /// <summary>Registers the mappings. Call once, while the app is being built.</summary>
    public static void RemoveNativeUnderline()
    {
#if ANDROID
        // A null Background is no underline, and no Material state colours to go with it; the
        // focus tint that used to travel with the underline goes too, which is right — the
        // Border does not change on focus either.
        EntryHandler.Mapper.AppendToMapping(MappingName, (handler, _) => handler.PlatformView.Background = null);
        EditorHandler.Mapper.AppendToMapping(MappingName, (handler, _) => handler.PlatformView.Background = null);
#elif IOS
        // Only the Entry: a UITextField draws a border style, a UITextView draws none by default.
        EntryHandler.Mapper.AppendToMapping(MappingName, (handler, _) =>
            handler.PlatformView.BorderStyle = UIKit.UITextBorderStyle.None);
#endif
    }
}
