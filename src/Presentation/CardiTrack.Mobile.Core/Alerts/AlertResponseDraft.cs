using System.Text.Json;
using CardiTrack.Application.DTOs.Requests;
using CardiTrack.Application.DTOs.Responses;

namespace CardiTrack.Mobile.Core.Alerts;

/// <summary>The two answers a caregiver can give an alert (D-20).</summary>
public enum AlertAnswerKind
{
    Acknowledge,
    Close,
}

/// <summary>
/// What the response page is holding before it is sent: a canned code, a note, or both.
/// </summary>
/// <remarks>
/// A canned pick alone is enough, and so is a note alone (Story 4.10). The note is capped at the
/// server's 500 with a visible count, and the draft round-trips through a string so the page can
/// park it in the device's keystore while the app is away and pick it up again on return. The
/// keystore rather than preferences: the note is free text about the wearer, which is why the
/// server encrypts it at rest.
/// </remarks>
public sealed class AlertResponseDraft
{
    public const int NoteLimit = 500;

    public string? Code { get; set; }

    public string Note { get; set; } = string.Empty;

    public bool CanSubmit => Code is not null || !string.IsNullOrWhiteSpace(Note);

    public bool IsEmpty => Code is null && string.IsNullOrEmpty(Note);

    public string Counter => $"{Note.Length}/{NoteLimit}";

    public bool IsOverLimit => Note.Length > NoteLimit;

    public AlertAnswerRequest ToRequest() => new()
    {
        ResponseCode = Code,
        Note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim(),
    };

    public string Serialize() => JsonSerializer.Serialize(new Stored(Code, Note));

    public static AlertResponseDraft? Deserialize(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<Stored>(stored);
            if (parsed is null)
                return null;
            var note = parsed.Note ?? string.Empty;
            return new AlertResponseDraft
            {
                Code = string.IsNullOrWhiteSpace(parsed.Code) ? null : parsed.Code,
                Note = note.Length > NoteLimit ? note[..NoteLimit] : note,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Stored(string? Code, string? Note);
}

public static class AlertAnswerKinds
{
    public static IReadOnlyList<AlertResponseOptionResponse> OptionsFor(AlertDetailResponse alert, AlertAnswerKind kind) =>
        kind == AlertAnswerKind.Close ? alert.ResponseOptions.Close : alert.ResponseOptions.Acknowledge;

    public static string Title(AlertAnswerKind kind) =>
        kind == AlertAnswerKind.Close ? "Close this alert" : "Acknowledge this alert";

    public static string Prompt(AlertAnswerKind kind) => kind == AlertAnswerKind.Close
        ? "It's dealt with. Tell the family what happened — the rule can fire again if it comes back."
        : "You're on it. Tell the family what you're doing so nobody else has to guess.";

    /// <summary>The one button on the page, naming the action it takes.</summary>
    public static string ButtonText(AlertAnswerKind kind) =>
        kind == AlertAnswerKind.Close ? "Close alert" : "Acknowledge";

    public static string Wire(AlertAnswerKind kind) => kind == AlertAnswerKind.Close ? "close" : "acknowledge";

    public static AlertAnswerKind? Parse(string? value) => value?.ToLowerInvariant() switch
    {
        "close" => AlertAnswerKind.Close,
        "acknowledge" => AlertAnswerKind.Acknowledge,
        _ => null,
    };
}
