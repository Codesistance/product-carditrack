using CardiTrack.Application.DTOs.Requests;

namespace CardiTrack.API.Validators;

/// <summary>
/// Same snapshot ceilings as generate, without a token — reuse mints one
/// from a standing grant instead of asking the caller to present one.
/// </summary>
public sealed class ReuseExportConsentValidator : GenerateReportValidator
{
    public ReuseExportConsentValidator() : base(requireConsentToken: false)
    {
    }
}
