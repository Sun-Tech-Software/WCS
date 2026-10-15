namespace Wcs.Engine.Test;

/// <summary>
/// Un orologio che si muove solo quando glielo si dice: serve a provare i freni a tempo senza
/// aspettare davvero. Un collaudo con attese vere sarebbe lento e, sotto carico, rosso su codice
/// sano.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan value) => _now += value;
}
