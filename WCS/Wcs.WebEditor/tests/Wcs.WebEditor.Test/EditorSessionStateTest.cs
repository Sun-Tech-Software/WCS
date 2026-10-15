using Wcs.WebEditor.Session;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>
/// La chiave vive nella sessione del server: senza, non c'e' client con cui parlare all'host.
/// </summary>
public sealed class EditorSessionStateTest
{
    [Fact]
    public void WithoutAKeyThereIsNoClient()
    {
        var state = new EditorSessionState(new Uri("http://localhost:5580"));
        Assert.False(state.HasKey);
        Assert.Throws<InvalidOperationException>(() => state.Client);
    }

    /// <summary>
    /// Both reasons can reach the page (the live status line shows them in parentheses when the
    /// channel cannot be built): they are in English, as the rest of the page.
    /// </summary>
    [Fact]
    public void WithoutAKeyTheReasonsAreInEnglish()
    {
        var state = new EditorSessionState(new Uri("http://localhost:5580"));

        Assert.Equal("No key in this session: the Editor cannot talk to the host.",
            Assert.Throws<InvalidOperationException>(() => state.Client).Message);
        Assert.Equal("No key in this session: the Editor cannot listen to the host.",
            Assert.Throws<InvalidOperationException>(() => state.OpenEvents()).Message);
    }

    [Fact]
    public void TheKeyGivesAClientAndForgettingTakesItAway()
    {
        var state = new EditorSessionState(new Uri("http://localhost:5580"));
        state.UseKey("chiave-di-prova");
        Assert.True(state.HasKey);
        Assert.NotNull(state.Client);

        state.Forget();
        Assert.False(state.HasKey);
    }

    [Fact]
    public void AnEmptyKeyIsNotAKey()
    {
        var state = new EditorSessionState(new Uri("http://localhost:5580"));
        Assert.Throws<ArgumentException>(() => state.UseKey("   "));
        Assert.False(state.HasKey);
    }

    /// <summary>
    /// L'identita' mandata all'host per il permesso di modifica (compito 4): stabile per tutta la
    /// sessione (non ricalcolata a ogni lettura) e distinta da quella di un'altra sessione - due
    /// schede del browser non devono poter essere scambiate per la stessa.
    /// </summary>
    [Fact]
    public void HolderIsStablePerSessionAndDistinctAcrossSessions()
    {
        var state = new EditorSessionState(new Uri("http://localhost:5580"));
        Assert.False(string.IsNullOrWhiteSpace(state.Holder));
        Assert.Equal(state.Holder, state.Holder);

        var other = new EditorSessionState(new Uri("http://localhost:5580"));
        Assert.NotEqual(state.Holder, other.Holder);
    }
}
