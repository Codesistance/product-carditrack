namespace CardiTrack.Worker;

public class DeviceSyncAuditOptions
{
    /// <summary>
    /// How many connections one audit run re-fetches over the wide window. Kept small on purpose:
    /// the run exists to characterise a provider, and a provider's revision behaviour is a property
    /// of the provider, not of any one wearer — so a sample answers it at a fraction of the cost of
    /// auditing everyone.
    /// </summary>
    public int SampleSize { get; set; } = 25;
}
