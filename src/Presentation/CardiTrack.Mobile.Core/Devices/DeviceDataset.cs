namespace CardiTrack.Mobile.Core.Devices;

/// <summary>
/// The kind of reading a dataset carries. The device card colours its pills by family, so a
/// glance separates movement from cardiac from sleep without reading every label.
/// </summary>
public enum DatasetFamily
{
    Activity,
    Heart,
    Sleep,
    Body,
    Other,
}

/// <summary>One kind of reading CardiTrack pulls from a connected device (M1-15).</summary>
/// <param name="Name">Display name for the pill, e.g. "Resting HR".</param>
/// <param name="Family">Colour grouping for the pill.</param>
public record DeviceDataset(string Name, DatasetFamily Family);
