using System.Globalization;
using System.Text;
using Microsoft.JSInterop;
using Wcs.WebEditor.Drawio.Coordination;

namespace Wcs.WebEditor.Drawio;

/// <summary>
/// Queue item 13: the drawing, taken from the browser to the page's circuit as a STREAM and not as
/// one interop message. The bridge answers with the drawing's bytes (<c>handedOverDrawing</c> for a
/// save, <c>currentDrawing</c> for the engine switch), Blazor wraps them in an
/// <see cref="IJSStreamReference"/>, and reading it brings them across in pieces each under the
/// circuit's own ceiling on a message.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> As one message the drawing of a real plant is far over that ceiling (measured: 417,924
/// bytes for 152 devices, 430,077 as one message; SignalR's default is 32 KB), and SignalR's answer to a message over it is to close
/// the connection: the circuit came back by itself, but the save round - or the engine's start - was
/// gone with the message, and the page never said anything. Raising the ceiling for the whole circuit
/// would only move the edge: a device weighs about 2.7 KB of drawing, because its picture travels
/// inside it, and a picture pasted onto the canvas can weigh megabytes on its own.
/// </para>
/// <para>
/// <b>The ceiling of its own</b>, <see cref="MaxBytes"/>, is what the page accepts in one drawing, and
/// a drawing over it is refused with a sentence instead of read: about 40 times the 152-device drawing
/// measured, some 6,000 devices; a sixteenth of what the host accepts in one request
/// (<c>Wcs:MaxRequestBodyMegabytes</c>, 256), which the decoration is written with; and a bound on
/// what one save can make the server hold (the bytes, then the text twice their size).
/// </para>
/// </remarks>
internal static class DrawioDrawingTransfer
{
    /// <summary>The most a drawing may weigh, in bytes of UTF-8, to be taken from the browser.</summary>
    internal const long MaxBytes = 16L * 1024 * 1024;

    // Strict: the bridge encodes with TextEncoder, which never writes an invalid sequence, so one that
    // arrives means the bytes are not the drawing - better said than read as replacement characters.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Asks the bridge <paramref name="module"/> for a drawing by <paramref name="identifier"/> and
    /// reads it: the text, or null when the bridge had no drawing to give (no bytes at all).
    /// </summary>
    /// <exception cref="DrawioDrawingNotReceivedException">
    /// The drawing is over <paramref name="maxBytes"/>, or it could not be brought across - the
    /// circuit dropped, the browser stopped answering, the bytes are not UTF-8. The message says which,
    /// in a form the page shows as it is.
    /// </exception>
    internal static async Task<string?> FetchAsync(IJSObjectReference module, string identifier, object?[] args,
        long maxBytes = MaxBytes)
    {
        IJSStreamReference? drawing;
        try
        {
            drawing = await module.InvokeAsync<IJSStreamReference?>(identifier, args);
        }
        catch (Exception ex) when (IsTransferFailure(ex))
        {
            throw new DrawioDrawingNotReceivedException(
                $"the browser did not hand the drawing over, because {DrawioFailureWords.WhyTheScriptDidNotAnswer(ex)}", ex);
        }

        if (drawing is null) return null;
        try
        {
            if (drawing.Length == 0) return null;
            if (drawing.Length > maxBytes) throw DrawioDrawingNotReceivedException.TooLarge(drawing.Length, maxBytes);
            try
            {
                await using var stream = await drawing.OpenReadStreamAsync(maxBytes);
                using var reader = new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: false);
                return await reader.ReadToEndAsync();
            }
            catch (Exception ex) when (IsTransferFailure(ex) || ex is IOException or TimeoutException or ArgumentException)
            {
                // ArgumentException covers both a stream longer than it said (ArgumentOutOfRange from
                // Blazor) and bytes that are not UTF-8 (DecoderFallbackException).
                throw new DrawioDrawingNotReceivedException("the drawing could not be brought across from the browser", ex);
            }
        }
        finally
        {
            await LetGoOfAsync(drawing);
        }
    }

    /// <summary>
    /// Tells the browser it may drop the bytes, and nothing more: disposing is a round trip, and after a
    /// dropped circuit it waits out the interop timeout and is cancelled, or finds the circuit gone. Its
    /// failure must neither take away a drawing already read nor replace the words of one that was not
    /// (review of item 13): a save would end with an empty report, a start would throw out of the
    /// switch's callback and take the circuit down. What is left behind is the browser's copy of the
    /// bytes, which goes with the page.
    /// </summary>
    private static async Task LetGoOfAsync(IJSStreamReference drawing)
    {
        try
        {
            await drawing.DisposeAsync();
        }
        catch (Exception ex) when (IsTransferFailure(ex))
        {
        }
    }

    private static bool IsTransferFailure(Exception ex) =>
        ex is JSException or JSDisconnectedException or OperationCanceledException or InvalidOperationException;
}

/// <summary>
/// Queue item 13: a drawing the page could not take from the browser, with the reason in words the
/// page shows as they are. What the browser or the circuit threw is the inner exception, never part of
/// the message: the page shows it behind the line's "?" (row 37 of the queue, extended to the whole
/// page on 10/10).
/// </summary>
/// <remarks>
/// The message is a clause the page puts into a sentence of its own, which ends it: a period at its end
/// is taken off, so that the page never shows two.
/// </remarks>
internal sealed class DrawioDrawingNotReceivedException(string message, Exception? inner = null)
    : Exception(message.TrimEnd().TrimEnd('.'), inner)
{
    internal static DrawioDrawingNotReceivedException TooLarge(long bytes, long maxBytes) =>
        new(string.Create(CultureInfo.InvariantCulture,
            $"the drawing weighs {bytes / 1048576.0:0.0} MB, over the {maxBytes / 1048576} MB this page takes from draw.io in one go - something on the canvas is very large, a pasted picture for instance"));
}
