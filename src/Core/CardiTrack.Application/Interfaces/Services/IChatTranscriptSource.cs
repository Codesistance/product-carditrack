using CardiTrack.Application.Reports;

namespace CardiTrack.Application.Interfaces.Services;

/// <summary>
/// Reads one member-chat conversation back as plaintext for a transcript export.
/// </summary>
/// <remarks>
/// A seam of its own rather than another method on <c>IMemberChatService</c>: the only thing an
/// export needs from chat is the stored conversation, and the report pipeline should not have to
/// resolve the whole send pipeline — a router, a planner and three model clients — to read six
/// rows. It also keeps decryption in exactly one place per concern, which is what makes the
/// unreadable-row fallback testable.
/// </remarks>
public interface IChatTranscriptSource
{
    /// <summary>
    /// The conversation and its turns, decrypted. Throws <see cref="KeyNotFoundException"/> when
    /// the session is not this caregiver's own conversation about this member — the same
    /// existence-hiding 404 reading a session through the chat API gets, so a guessed id in an
    /// export request learns nothing either.
    /// </summary>
    Task<ChatTranscript> GetAsync(
        Guid userId, Guid cardiMemberId, Guid sessionId, CancellationToken ct = default);

    /// <summary>
    /// Throws the same <see cref="KeyNotFoundException"/> as <see cref="GetAsync"/> unless the
    /// session is this caregiver's own conversation about this member, without reading the
    /// conversation itself.
    /// </summary>
    /// <remarks>
    /// For the gate on the request path, where the answer is only yes or no. <see cref="GetAsync"/>
    /// loads every turn and decrypts every one of them plus its stored charts; running that to
    /// decide an authorization question would do the work twice — generation reads the
    /// conversation again on its own scope — and would materialise the plaintext of a health
    /// conversation on a request that is about to discard it.
    /// </remarks>
    Task RequireOwnedAsync(
        Guid userId, Guid cardiMemberId, Guid sessionId, CancellationToken ct = default);
}
