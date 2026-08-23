using System.Reflection;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Services;

namespace CardiTrack.UnitTests.Architecture;

/// <summary>
/// The DPIA row A20 boundary as the security review asked for it: a type, not a test — these
/// assert the two properties of that type the compiler cannot state. <see cref="ClinicalOnlyData"/>
/// must have exactly one way out (the clinical-prompt render), and the accidental path —
/// interpolation into some other prompt string — must yield a marker, never member data.
/// </summary>
public class SlotBoundaryTests
{
    private const string Payload = "age 81, atrial fibrillation noted, questionnaire: reports dizziness";

    [Fact]
    public void ClinicalOnlyData_InterpolatedIntoAString_LeaksNothing()
    {
        var wrapped = ClinicalOnlyData.Wrap(Payload);

        // The one accidental path a compiler cannot refuse: $"{wrapped}" inside a prompt meant
        // for the rewrite slot. It must produce the marker, not the payload.
        var interpolated = $"prompt preamble {wrapped} prompt tail";

        Assert.DoesNotContain(Payload, interpolated);
        Assert.DoesNotContain("dizziness", interpolated);
        Assert.Contains("clinical-only", interpolated);
    }

    [Fact]
    public void ClinicalOnlyData_HasNoPublicReadPathExceptTheClinicalRender()
    {
        // Reflection over the public surface: every public member that could return the payload
        // must be the render method. A future convenience property ("Content", "Value") would
        // reopen the by-convention boundary this type exists to close.
        var leaks = typeof(ClinicalOnlyData)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m switch
            {
                PropertyInfo p => p.PropertyType == typeof(string),
                FieldInfo f => f.FieldType == typeof(string),
                MethodInfo f when f.ReturnType == typeof(string) =>
                    f.Name is not (nameof(ClinicalOnlyData.RenderForClinicalPrompt) or nameof(ToString)),
                _ => false,
            })
            .Select(m => m.Name)
            .ToList();

        Assert.True(leaks.Count == 0,
            $"ClinicalOnlyData exposes the payload beyond the clinical render: {string.Join(", ", leaks)}. " +
            "The A20 boundary is this type having exactly one exit; add a second and the boundary " +
            "is a convention again.");
    }

    [Fact]
    public void ClinicalOnlyData_RendersInFullForTheClinicalPrompt()
    {
        // The boundary must not cost the clinical prompt anything — the Private slot is the one
        // place this content is for.
        Assert.Equal(Payload, ClinicalOnlyData.Wrap(Payload).RenderForClinicalPrompt());
    }

    // ------------------------------------------------------------------------------------------
    // The registry the planning calls render
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void EveryRegistryEntry_DescribesADistinctFetchableSource()
    {
        // One entry per DataQueryKind, no more and no fewer: the registry is a description of the
        // whitelist's actual capability, so a missing entry hides a real source from every
        // planner, and a duplicate or orphan describes capability that is not there.
        var registered = ChatDataRegistry.All.Select(e => e.Kind).ToList();

        Assert.Equal(registered.Count, registered.Distinct().Count());
        Assert.Equal(
            Enum.GetValues<DataQueryKind>().OrderBy(k => k),
            registered.OrderBy(k => k));
    }

    [Fact]
    public void EveryRegistryLine_StartsWithTheNameTheParserMatches()
    {
        // The parser matches enum member names; the prompt teaches whatever the line leads with.
        // If those disagree the model is taught a name the parse then drops.
        var wrong = ChatDataRegistry.All
            .Where(e => !e.Line.StartsWith(e.Kind.ToString(), StringComparison.Ordinal))
            .Select(e => e.Kind)
            .ToList();

        Assert.True(wrong.Count == 0,
            $"Registry line(s) not led by their parseable name: {string.Join(", ", wrong)}.");
    }

    [Fact]
    public void RegistrySlices_FilterToTheCatalogueEntry()
    {
        foreach (var entry in ChatWorkflowCatalogue.All.Where(w => w.AllowedDatasets.Count > 0))
        {
            var slice = ChatDataRegistry.For(entry.AllowedDatasets);
            Assert.Equal(entry.AllowedDatasets.OrderBy(k => k), slice.Select(e => e.Kind).OrderBy(k => k));
        }

        // The zero-dataset entries (advise, the steers, clarify) get an empty slice, not a default.
        Assert.Empty(ChatDataRegistry.For([]));
    }

    [Fact]
    public void PublishedBands_CoverExactlyTheMetricsWithAnAccreditedRange()
    {
        // Heart rate and sleep have published, attributable ranges; steps deliberately has none —
        // the absence is load-bearing (see ChatDataRegistry's remarks), so a well-meaning addition
        // of a "10,000 steps" band must fail here and cite a source in review.
        Assert.Equal(
            new[] { ChartMetricKind.RestingHeartRate, ChartMetricKind.Sleep },
            ChatDataRegistry.Bands.Select(b => b.Metric).OrderBy(m => m));

        Assert.Contains("Steps has no published typical range", ChatDataRegistry.BandsBlock);
    }
}
