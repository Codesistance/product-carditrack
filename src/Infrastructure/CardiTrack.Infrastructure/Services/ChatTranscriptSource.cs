using System.Text.Json;
using CardiTrack.Application.DTOs.Common;
using CardiTrack.Application.Interfaces.Repositories;
using CardiTrack.Application.Interfaces.Security;
using CardiTrack.Application.Interfaces.Services;
using CardiTrack.Application.Reports;
using CardiTrack.Domain.Entities;

namespace CardiTrack.Infrastructure.Services;

/// <summary>
/// Reads one stored conversation back as plaintext for a transcript export.
/// </summary>
/// <remarks>
/// <para>
/// Ownership is the whole of the authorization here, and it is stricter than the member gate the
/// export pipeline already applied: a session is one caregiver's conversation about one member,
/// so another caregiver who can view the same member still cannot export their thread. A session
/// that is not theirs reads as one that never existed — the same existence-hiding 404
/// <c>MemberChatService.GetSessionAsync</c> gives, so a guessed id in an export request learns
/// nothing either.
/// </para>
/// <para>
/// The decryption fallbacks mirror <c>MemberChatService</c>'s deliberately: an unreadable turn
/// comes back empty and an unreadable chart blob comes back as no charts, rather than failing the
/// export. A caregiver asking for a copy of a conversation is better served by the conversation
/// with one blank line in it than by a generation that fails with nothing to show.
/// </para>
/// </remarks>
public class ChatTranscriptSource : IChatTranscriptSource
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IEncryptionService _encryption;

    public ChatTranscriptSource(IUnitOfWork unitOfWork, IEncryptionService encryption)
    {
        _unitOfWork = unitOfWork;
        _encryption = encryption;
    }

    public async Task<ChatTranscript> GetAsync(
        Guid userId, Guid cardiMemberId, Guid sessionId, CancellationToken ct = default)
    {
        var session = RequireOwned(
            await _unitOfWork.MemberChatSessions.GetByIdWithTurnsAsync(sessionId, ct),
            userId, cardiMemberId);

        return new ChatTranscript(
            session.Id,
            session.CardiMemberId,
            session.Theme is null ? null : Reveal(session.Theme) is { Length: > 0 } theme ? theme : null,
            Utc(session.StartedAtUtc),
            Utc(session.LastTurnAtUtc),
            session.Turns
                .OrderBy(t => t.CreatedAtUtc)
                .Select(ToTurn)
                .ToList());
    }

    public async Task RequireOwnedAsync(
        Guid userId, Guid cardiMemberId, Guid sessionId, CancellationToken ct = default)
    {
        // The session row by its key, without its turns: this answers yes or no, and pulling a
        // conversation in to decrypt and throw away would be the expensive half of GetAsync run
        // for none of its result.
        RequireOwned(
            await _unitOfWork.MemberChatSessions.GetByIdAsync(sessionId), userId, cardiMemberId);
    }

    /// <summary>
    /// The one ownership predicate both reads use — stricter than the member gate the export
    /// pipeline already applied, and failing the same way for a session that is not theirs as
    /// for one that never existed, so a guessed id learns nothing either way. Returns the
    /// session so the caller carries it on as non-null.
    /// </summary>
    private static MemberChatSession RequireOwned(
        MemberChatSession? session, Guid userId, Guid cardiMemberId)
    {
        if (session is null || session.UserId != userId || session.CardiMemberId != cardiMemberId)
            throw new KeyNotFoundException("We couldn't find that conversation.");

        return session;
    }

    private ChatTranscriptTurn ToTurn(MemberChatTurn turn) => new(
        turn.Role,
        Reveal(turn.Content),
        Utc(turn.CreatedAtUtc),
        RevealCharts(turn.Charts));

    private string Reveal(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return string.Empty;

        try
        {
            return _encryption.Decrypt(stored);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return string.Empty;
        }
    }

    private IReadOnlyList<ChartSeries> RevealCharts(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<ChartSeries>>(_encryption.Decrypt(stored)) ?? [];
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException
                                      or JsonException)
        {
            return [];
        }
    }

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
