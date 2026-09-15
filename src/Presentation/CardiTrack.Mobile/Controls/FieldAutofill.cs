using Microsoft.Maui.Handlers;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// Keeps the platform autofill service out of every <see cref="Entry"/> and <see cref="Editor"/>
/// except the handful that ask for it with <c>Controls:FieldAutofill.Enabled="True"</c>.
/// </summary>
/// <remarks>
/// <para>
/// The app's own credentials are worth autofilling: the sign-in email, the password, the
/// caregiver's own name on Create Account. Nothing else on this app's forms belongs to the
/// person holding the phone. A CardiMember's name, phone, emergency contact and medical notes
/// are someone else's details, and an autofill service has only ever one identity to offer —
/// the account holder's.
/// </para>
/// <para>
/// Android decides what to offer from the field's own hints, and a box labelled "Full name" on
/// the Edit CardiMember screen looks exactly like the "Full name" it learned from Create
/// Account. Filling it writes the caregiver's saved username over the member's name, and the
/// screens that save a phone number re-send whatever <c>Name</c> they are holding
/// (<c>CardiMemberDetailPage.SaveContactAsync</c>), so a stray fill persists on the next
/// unrelated save.
/// </para>
/// <para>
/// Opt-out is therefore the default and opt-in is explicit, the opposite of a per-field
/// suppression that the next field added to a form would not carry. Registered through the
/// handler mappers alongside <see cref="FieldChrome"/>, and named so a later mapping can be
/// ordered against it.
/// </para>
/// </remarks>
internal static class FieldAutofill
{
    public const string MappingName = "AutofillPolicy";

    /// <summary>
    /// Attached property: <c>True</c> lets the platform autofill this field. Default
    /// <c>False</c> — the field is hidden from the autofill service.
    /// </summary>
    public static readonly BindableProperty EnabledProperty = BindableProperty.CreateAttached(
        "Enabled",
        typeof(bool),
        typeof(FieldAutofill),
        defaultValue: false);

    public static bool GetEnabled(BindableObject view) => (bool)view.GetValue(EnabledProperty);

    public static void SetEnabled(BindableObject view, bool value) => view.SetValue(EnabledProperty, value);

    /// <summary>Registers the mappings. Call once, while the app is being built.</summary>
    public static void ApplyPolicy()
    {
#if ANDROID
        EntryHandler.Mapper.AppendToMapping(MappingName, (handler, view) =>
            handler.PlatformView.ImportantForAutofill = Importance(view));
        EditorHandler.Mapper.AppendToMapping(MappingName, (handler, view) =>
            handler.PlatformView.ImportantForAutofill = Importance(view));
#elif IOS
        EntryHandler.Mapper.AppendToMapping(MappingName, (handler, view) =>
        {
            if (!IsEnabled(view))
                handler.PlatformView.TextContentType = NoContentType;
        });
        EditorHandler.Mapper.AppendToMapping(MappingName, (handler, view) =>
        {
            if (!IsEnabled(view))
                handler.PlatformView.TextContentType = NoContentType;
        });
#endif
    }

#if ANDROID
    /// <summary>
    /// <c>NoExcludeDescendants</c> rather than <c>No</c>: a MAUI Entry is a native view with
    /// children of its own, and excluding only the parent leaves them fillable.
    /// </summary>
    private static Android.Views.ImportantForAutofill Importance(IView view) =>
        IsEnabled(view)
            ? Android.Views.ImportantForAutofill.Auto
            : Android.Views.ImportantForAutofill.NoExcludeDescendants;
#elif IOS
    /// <summary>
    /// iOS offers AutoFill for the content type a field declares. The empty type declares none,
    /// which is how UIKit is told a field is not part of anyone's saved identity.
    /// </summary>
    /// <remarks>
    /// A field that wants AutoFill is left untouched rather than assigned a default, because on
    /// iOS there is nothing to assign: UIKit's "no declared type" is nil, and the
    /// <c>UITextField.TextContentType</c> binding rejects it — <c>GetNonNullHandle</c> raises
    /// <see cref="ArgumentNullException"/>. Assigning it crashed the app in the handler mapper,
    /// on the first opted-in <see cref="Entry"/> to be given a handler; that is the sign-in
    /// email, so every launch. Not assigning is also the more truthful policy: a field this
    /// policy permits is one it has not touched at all, and the keyboard's own heuristics apply.
    /// </remarks>
    private static readonly Foundation.NSString NoContentType = new(string.Empty);
#endif

#if ANDROID || IOS
    private static bool IsEnabled(IView view) =>
        view is BindableObject bindable && GetEnabled(bindable);
#endif
}
