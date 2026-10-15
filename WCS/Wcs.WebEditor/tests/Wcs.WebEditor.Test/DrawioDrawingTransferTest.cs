using System.Text;
using Microsoft.JSInterop;
using Wcs.WebEditor.Drawio;
using Xunit;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Queue item 13: the drawing taken from the browser as a stream (<see cref="DrawioDrawingTransfer"/>)
/// - the text when it comes, nothing when there is none, and a sentence when it cannot be taken,
/// never an exception of the transport's own reaching the page.
/// </summary>
public sealed class DrawioDrawingTransferTest
{
    private const string Drawing = "<mxfile><diagram id=\"d\">a drawing è €</diagram></mxfile>";

    [Fact]
    public async Task TheDrawingComesBackAsTheTextTheBridgeEncoded()
    {
        var stream = new TestJSStreamReference(Drawing);
        var module = new Module(stream);

        Assert.Equal(Drawing, await DrawioDrawingTransfer.FetchAsync(module, "handedOverDrawing", [7L]));
        Assert.Equal("handedOverDrawing", module.Identifier);
        Assert.Equal(new object?[] { 7L }, module.Args);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task NoBytesOrNoReferenceIsNoDrawing()
    {
        var empty = new TestJSStreamReference([]);
        Assert.Null(await DrawioDrawingTransfer.FetchAsync(new Module(empty), "currentDrawing", []));
        Assert.True(empty.Disposed);
        Assert.Null(await DrawioDrawingTransfer.FetchAsync(new Module((IJSStreamReference?)null), "currentDrawing", []));
    }

    /// <summary>
    /// Over the ceiling the drawing is refused on its declared length, before a byte of it crosses,
    /// with a sentence that names both sizes.
    /// </summary>
    [Fact]
    public async Task ADrawingOverTheCeilingIsRefusedWithoutBeingRead()
    {
        var stream = new TestJSStreamReference([], declaredLength: DrawioDrawingTransfer.MaxBytes + 1);

        var refused = await Assert.ThrowsAsync<DrawioDrawingNotReceivedException>(
            () => DrawioDrawingTransfer.FetchAsync(new Module(stream), "handedOverDrawing", [1L]));

        Assert.Equal(0, stream.Opened);
        Assert.True(stream.Disposed);
        Assert.Contains("16.0 MB", refused.Message, StringComparison.Ordinal);
        Assert.Contains("over the 16 MB", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BytesThatAreNotUtf8AreNotTakenForTheDrawing()
    {
        var stream = new TestJSStreamReference([0x3c, 0xff, 0xfe, 0x3e]);

        var refused = await Assert.ThrowsAsync<DrawioDrawingNotReceivedException>(
            () => DrawioDrawingTransfer.FetchAsync(new Module(stream), "handedOverDrawing", [1L]));

        Assert.Contains("could not be brought across", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Row 37 of the queue, extended to the whole page on 10/10: the words say why in the page's own
    /// terms, from the failure's type; what the browser or the circuit threw is the inner exception,
    /// which the page shows behind the line's "?", and never part of the words.
    /// </summary>
    [Theory]
    [InlineData("js", "the browser did not hand the drawing over, because the page's script failed")]
    [InlineData("disconnected", "the browser did not hand the drawing over, because the connection to the browser was lost")]
    [InlineData("canceled", "the browser did not hand the drawing over, because the page's script did not answer in time")]
    public async Task ABrowserThatDoesNotAnswerIsSaidInWords(string failure, string words)
    {
        Exception thrown = failure switch
        {
            "js" => new JSException("the bridge failed (test)"),
            "disconnected" => new JSDisconnectedException("the circuit is gone (test)"),
            _ => new TaskCanceledException("no answer in time (test)"),
        };

        var refused = await Assert.ThrowsAsync<DrawioDrawingNotReceivedException>(
            () => DrawioDrawingTransfer.FetchAsync(new Module(thrown), "currentDrawing", []));

        Assert.Equal(words, refused.Message);
        Assert.Same(thrown, refused.InnerException);
    }

    /// <summary>
    /// Disposing the reference is a round trip to the browser, and after a dropped circuit it fails -
    /// it waits out the interop timeout and is cancelled, or finds the circuit gone. A failure there
    /// changes nothing of what was read: the text still comes back.
    /// </summary>
    [Theory]
    [InlineData("canceled")]
    [InlineData("disconnected")]
    public async Task ADisposeThatFailsDoesNotTakeAwayAGoodRead(string failure)
    {
        var stream = new TestJSStreamReference(Encoding.UTF8.GetBytes(Drawing), disposeFailure: DisposeFailure(failure));

        Assert.Equal(Drawing, await DrawioDrawingTransfer.FetchAsync(new Module(stream), "handedOverDrawing", [2L]));
        Assert.True(stream.Disposed);
    }

    /// <summary>...nor does it replace the words of a read that went wrong, nor of a drawing refused unread.</summary>
    [Theory]
    [InlineData("canceled")]
    [InlineData("disconnected")]
    public async Task ADisposeThatFailsDoesNotReplaceTheWordedRefusal(string failure)
    {
        var bad = new TestJSStreamReference([0x3c, 0xff, 0xfe, 0x3e], disposeFailure: DisposeFailure(failure));
        var large = new TestJSStreamReference([], declaredLength: DrawioDrawingTransfer.MaxBytes + 1, disposeFailure: DisposeFailure(failure));
        var empty = new TestJSStreamReference([], disposeFailure: DisposeFailure(failure));

        Assert.Contains("could not be brought across", (await Assert.ThrowsAsync<DrawioDrawingNotReceivedException>(
            () => DrawioDrawingTransfer.FetchAsync(new Module(bad), "handedOverDrawing", [3L]))).Message, StringComparison.Ordinal);
        Assert.Contains("over the 16 MB", (await Assert.ThrowsAsync<DrawioDrawingNotReceivedException>(
            () => DrawioDrawingTransfer.FetchAsync(new Module(large), "handedOverDrawing", [4L]))).Message, StringComparison.Ordinal);
        Assert.Null(await DrawioDrawingTransfer.FetchAsync(new Module(empty), "currentDrawing", []));
    }

    /// <summary>
    /// The words are a clause the page puts into its own sentence, which ends it, and the browser's own
    /// message - with its period - stays out of them (row 37 of the queue, extended on 10/10): it is the
    /// inner exception, for the line's "?". So for a drawing that could not be brought across.
    /// </summary>
    [Fact]
    public async Task TheBrowsersOwnMessageStaysOutOfTheWords()
    {
        var thrown = new JSException("The bridge failed (test).");
        var refused = await Assert.ThrowsAsync<DrawioDrawingNotReceivedException>(
            () => DrawioDrawingTransfer.FetchAsync(new Module(thrown), "currentDrawing", []));

        Assert.Equal("the browser did not hand the drawing over, because the page's script failed", refused.Message);
        Assert.Same(thrown, refused.InnerException);

        var notUtf8 = await Assert.ThrowsAsync<DrawioDrawingNotReceivedException>(
            () => DrawioDrawingTransfer.FetchAsync(new Module(new TestJSStreamReference([0x3c, 0xff, 0xfe, 0x3e])), "handedOverDrawing", [1L]));

        Assert.Equal("the drawing could not be brought across from the browser", notUtf8.Message);
        Assert.IsAssignableFrom<ArgumentException>(notUtf8.InnerException);
    }

    private static Exception DisposeFailure(string failure) => failure == "canceled"
        ? new TaskCanceledException("the dispose waited out the interop timeout (test)")
        : new JSDisconnectedException("the circuit is gone (test)");

    /// <summary>The bridge module as Blazor hands it to the page, answering one call.</summary>
    private sealed class Module : IJSObjectReference
    {
        private readonly IJSStreamReference? _answer;
        private readonly Exception? _failure;

        public Module(IJSStreamReference? answer) => _answer = answer;

        public Module(Exception failure) => _failure = failure;

        public string? Identifier { get; private set; }

        public object?[]? Args { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Identifier = identifier;
            Args = args;
            if (_failure is not null) return ValueTask.FromException<TValue>(_failure);
            return ValueTask.FromResult((TValue)(object?)_answer!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
