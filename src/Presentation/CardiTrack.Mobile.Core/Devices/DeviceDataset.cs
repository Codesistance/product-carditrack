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

/// <summary>
/// The datasets one family contributes to a connection — the unit the M1-15 card pills are built
/// from. One pill per family rather than per dataset: a fully-granted Fitbit shares nine readings,
/// and nine equal-weight pills wrap into a block that costs more to read than it tells you.
/// </summary>
/// <param name="Family">Colour grouping shared by every dataset in the group.</param>
/// <param name="Datasets">The group's datasets, in catalogue order.</param>
public record DeviceDatasetGroup(DatasetFamily Family, IReadOnlyList<DeviceDataset> Datasets)
{
    /// <summary>
    /// What the pill says. A family contributing a single dataset names that dataset — "Weight"
    /// carries strictly more than "Body 1" at the same width — and only a family carrying several
    /// falls back to the family name plus a count.
    /// </summary>
    public string Label => Datasets.Count == 1 ? Datasets[0].Name : DisplayName(Family);

    /// <summary>The count shown beside <see cref="Label"/>, or null when the label is the dataset.</summary>
    public int? Count => Datasets.Count == 1 ? null : Datasets.Count;

    /// <summary>Every dataset in the group, for the expanded detail line: "Steps · Distance · …".</summary>
    public string Detail => string.Join(" · ", Datasets.Select(d => d.Name));

    /// <summary>What a screen calls this family.</summary>
    public static string DisplayName(DatasetFamily family) => family switch
    {
        DatasetFamily.Activity => "Activity",
        DatasetFamily.Heart => "Heart",
        DatasetFamily.Sleep => "Sleep",
        DatasetFamily.Body => "Body",
        _ => "Other",
    };
}
