namespace CardiTrack.Mobile.Controls;

/// <summary>
/// A Border that keeps itself square by giving its width whatever height layout hands it. The
/// header icons use it to span exactly from the title's top to the description's bottom — the
/// text block decides the height, and this makes the width follow. A binding from WidthRequest
/// to the element's own Height did the same job on paper, but Height settles after the first
/// layout pass and the binding was left holding a stale value on some devices; sizing inside
/// the allocation callback is what layout itself guarantees to be current.
/// </summary>
public sealed class SquareBorder : Border
{
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);

        if (height > 0 && Math.Abs(WidthRequest - height) > 0.5)
            WidthRequest = height;
    }
}
