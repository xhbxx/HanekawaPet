namespace HanekawaPet.Core;

// Uses elapsed process time for inactivity and local wall time only for daily rest windows.
public sealed class PetRoutine
{
    private readonly Func<double> _random;
    private double _lastInteraction;
    private bool _interacted;
    public double NextYawnAt { get; private set; }
    public double NextKnockAt { get; private set; }
    public const double LongIdleMs = 3 * 60 * 60 * 1000;
    public const double WakeGraceMs = 60 * 1000;

    public PetRoutine(double now, Func<double>? random = null)
    {
        _random = random ?? Random.Shared.NextDouble;
        _lastInteraction = now;
        YawnPlayed(now);
        KnockPlayed(now);
    }

    public static bool IsRestTime(TimeSpan localTime) =>
        localTime >= TimeSpan.Zero && localTime < TimeSpan.FromHours(6) ||
        localTime >= TimeSpan.FromHours(13) && localTime < TimeSpan.FromHours(14);

    public void Interact(double now)
    {
        _lastInteraction = now; _interacted = true; YawnPlayed(now); KnockPlayed(now);
    }

    public bool ShouldRest(double now, TimeSpan localTime) =>
        now - _lastInteraction > LongIdleMs ||
        IsRestTime(localTime) && (!_interacted || now - _lastInteraction >= WakeGraceMs);

    public bool YawnDue(double now) => now >= NextYawnAt;
    public void YawnPlayed(double now) => NextYawnAt = now + (15 + 10 * _random()) * 60 * 1000;
    public bool KnockDue(double now) => now >= NextKnockAt;
    public void KnockPlayed(double now) => NextKnockAt = now + (3 + 4 * _random()) * 60 * 1000;

    public static string RestVariant(double sample) => sample < .8 ? "sleep" : sample < .9 ? "sleep-ear-twitch" : "sleep-tail-flick";
}
