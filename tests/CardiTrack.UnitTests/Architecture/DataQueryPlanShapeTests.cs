using System.Reflection;
using CardiTrack.Application.DTOs.Common;

namespace CardiTrack.UnitTests.Architecture;

/// <summary>
/// <see cref="DataQueryPlan"/> is the query-planner's structured AI output — the type the security
/// review identified as the one place a prompt-injected question could attempt cross-member access,
/// by asking the model to name a subject the plan then fetches. The fix is that the type cannot
/// carry one: this asserts that invariant at the type's public shape, not just in code review, so it
/// stays true as <see cref="DataQueryKind"/> grows with future scenarios.
/// </summary>
public class DataQueryPlanShapeTests
{
    [Fact]
    public void DataQueryPlan_HasNoGuidProperty()
    {
        var guidProperties = typeof(DataQueryPlan).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(Guid) || p.PropertyType == typeof(Guid?))
            .Select(p => p.Name)
            .ToList();

        Assert.True(guidProperties.Count == 0,
            $"DataQueryPlan carries a Guid-typed property ({string.Join(", ", guidProperties)}) — " +
            "this is the exact shape a prompt-injected question could steer into naming a member or " +
            "user id. The CardiMemberId a plan executes against must come from the authenticated " +
            "caller (see DataQueryWhitelist.ExecuteAsync), never from model output. If a new field is " +
            "genuinely needed here, it must not be an identifier of any kind.");
    }

    [Fact]
    public void DataQueryPlan_HasNoFreeTextProperty()
    {
        // A free-text field is the other way an identifier could sneak in — "member: <guid>"
        // typed as a string still lets a plan name a subject. Sources is the one allowed exception:
        // it is a list of the closed DataQueryKind enum, not free text.
        var stringProperties = typeof(DataQueryPlan).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .ToList();

        Assert.True(stringProperties.Count == 0,
            $"DataQueryPlan carries a free-text property ({string.Join(", ", stringProperties)}) — " +
            "every field on this type must be a closed enum or a bounded number, never text a model " +
            "could use to smuggle an identifier through.");
    }
}
