using System.Security.Cryptography;
using System.Text;

namespace CardiTrack.Application.Services.Notifications;

/// <summary>
/// Content-derived identity for a notification.
/// </summary>
/// <remarks>
/// Because the fingerprint is computed from the rule, the target and the scope rather than
/// generated, re-running detection converges on the row that already exists. That is what makes
/// reconciliation idempotent without a distributed lock, and it is enforced by a unique index —
/// a concurrent second run loses its insert instead of showing the user the same gap twice.
/// </remarks>
public static class NudgeFingerprint
{
    public static string Compute(string ruleCode, Guid userId, Guid scopeId, string discriminator)
    {
        var payload = $"{ruleCode}|{userId:N}|{scopeId:N}|{discriminator}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
