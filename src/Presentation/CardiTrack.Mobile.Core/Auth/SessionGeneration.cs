namespace CardiTrack.Mobile.Core.Auth;

/// <summary>
/// Bumps on every sign-in and sign-out so a GET that outlived the session that started it
/// cannot write its body into the next caregiver's cache. A non-null token is not enough:
/// caregiver B can already be signed in when caregiver A's response arrives.
/// </summary>
public sealed class SessionGeneration
{
    private int _value;

    public int Current => Volatile.Read(ref _value);

    public int Advance() => Interlocked.Increment(ref _value);
}
