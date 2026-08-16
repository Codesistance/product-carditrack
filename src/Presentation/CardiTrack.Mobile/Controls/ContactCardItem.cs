using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CardiTrack.Mobile.Controls;

/// <summary>
/// One slide of the Member Detail contact carousel: emergency contact or the CardiMember's
/// own phone. The page binds the template off these flags rather than naming controls on
/// each card, because a <c>CarouselView</c> recycles the template. Properties notify so a
/// silent refresh can update the copy without handing the carousel a new
/// <c>ItemsSource</c> — that snap is what used to jolt Key Metric Trends under a caregiver.
/// </summary>
public sealed class ContactCardItem : INotifyPropertyChanged
{
    public const string Emergency = "emergency";
    public const string Phone = "phone";

    private string _title = string.Empty;
    private string _primary = string.Empty;
    private string _secondary = string.Empty;
    private bool _showCall;
    private bool _showMessage;
    private bool _showEdit;
    private bool _canEditPrimary;

    public required string Kind { get; init; }

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public string Primary
    {
        get => _primary;
        set => Set(ref _primary, value);
    }

    public string Secondary
    {
        get => _secondary;
        set
        {
            if (!Set(ref _secondary, value))
                return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSecondary)));
        }
    }

    public bool HasSecondary => _secondary.Length > 0;

    public bool ShowCall
    {
        get => _showCall;
        set => Set(ref _showCall, value);
    }

    public bool ShowMessage
    {
        get => _showMessage;
        set => Set(ref _showMessage, value);
    }

    public bool ShowEdit
    {
        get => _showEdit;
        set => Set(ref _showEdit, value);
    }

    public bool CanEditPrimary
    {
        get => _canEditPrimary;
        set => Set(ref _canEditPrimary, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
