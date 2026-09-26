namespace CardiTrack.Mobile.Core.Chat;

/// <summary>
/// The "did you know?" line the chat greets a new conversation with: one thing the assistant can
/// really do, a different one each time, so the greeting teaches the whole range over a few
/// visits instead of repeating one paragraph that listed it all at once.
/// </summary>
/// <remarks>
/// Every line names something the assistant is built to do — a rung of the chat router or one of
/// its actions (docs/technical/member_chat_routing.md) — and none promises more: a greeting that
/// oversells is a question that comes back "I can't do that". Keep this list in step with the
/// router when a capability is added or taken away.
/// </remarks>
public static class ChatFunFacts
{
    private static readonly Func<Subject, string>[] Facts =
    [
        s => $"I can read out a single moment in seconds. Try \"What was {s.Possessive} heart rate this morning?\"",
        s => $"I compare {s.Possessive} readings with their own usual and with the published healthy range, not just one number on its own.",
        s => $"I can dig into why something changed. Try \"Why did {s.Name} sleep less this week?\"",
        s => $"I keep a \"Something to try\" suggestion for {s.Name}, grounded in published wellness guidance. Just ask what might help.",
        s => $"I can show, list or write again any of {s.Possessive} CardiJournal books.",
        s => $"I can switch {s.Possessive} alerts on or off for you. Just say which one.",
        s => $"I can set an alarm on one of {s.Possessive} readings, so you hear when it crosses a line you choose.",
        s => "Some of my answers come with a chart, so you can see the trend and not just the number.",
    ];

    /// <summary>How many facts there are to rotate through.</summary>
    public static int Count => Facts.Length;

    /// <summary>
    /// The fact at <paramref name="index"/>, wrapping round, about the member named — or about
    /// "them" when the conversation has nobody chosen yet.
    /// </summary>
    public static string At(int index, string? firstName)
    {
        var i = ((index % Facts.Length) + Facts.Length) % Facts.Length;
        return Facts[i](Subject.For(firstName));
    }

    private readonly record struct Subject(string Name, string Possessive)
    {
        public static Subject For(string? firstName) =>
            string.IsNullOrWhiteSpace(firstName)
                ? new Subject("them", "their")
                : new Subject(firstName.Trim(), firstName.Trim().EndsWith('s') ? $"{firstName.Trim()}'" : $"{firstName.Trim()}'s");
    }
}
