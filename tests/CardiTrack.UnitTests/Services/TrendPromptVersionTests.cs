using CardiTrack.Application.Services;
using CardiTrack.Infrastructure.Services;

namespace CardiTrack.UnitTests.Services;

/// <summary>
/// The stamp that decides whether a stored trend narrative is still current. It carries two
/// independent versions, and the way they are combined is the whole contract: a change to either
/// the brief or the pinned table has to make every older row compare as older.
/// </summary>
public class TrendPromptVersionTests
{
    [Fact]
    public void TheTableVersionFitsInsideTheRoomReservedForIt()
    {
        // The packing's one assumption, checked rather than assumed: a table version that reached
        // the stride would start carrying into the brief's digits, and the collision the packing
        // exists to prevent would come back silently.
        Assert.True(
            PinnedReferenceTable.Version < 100,
            $"PinnedReferenceTable.Version is {PinnedReferenceTable.Version}; the packed stamp "
            + "reserves 100. Widen the stride in TrendInterpretationService before going further.");
    }

    [Fact]
    public void TwoDifferentCombinationsNeverProduceTheSameStamp()
    {
        // The bug in the summed version: brief 1 + table 3 and brief 2 + table 2 both came to 4,
        // so moving between them left every stored narrative looking current.
        var stamps = new HashSet<int>();

        for (var brief = 1; brief <= 8; brief++)
        {
            for (var table = 0; table < 100; table++)
            {
                Assert.True(
                    stamps.Add((brief * 100) + table),
                    $"brief {brief} and table {table} collide with an earlier combination.");
            }
        }
    }

    [Fact]
    public void ThisBuildsStampIsBothOfItsParts()
    {
        Assert.Equal(
            (TrendInterpretationService.BriefVersion * 100) + PinnedReferenceTable.Version,
            TrendInterpretationService.CurrentPromptVersion);

        // And a row written before either moved is due, which is what the stamp is for.
        Assert.True(TrendInterpretationService.CurrentPromptVersion > 0);
    }
}
