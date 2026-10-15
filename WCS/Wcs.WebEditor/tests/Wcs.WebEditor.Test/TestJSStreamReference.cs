using System.Text;
using Microsoft.JSInterop;

namespace Wcs.WebEditor.Test;

/// <summary>
/// Queue item 13: what Blazor hands the page when the bridge answers with bytes - a stream it can
/// open once, of a length it declares up front. <paramref name="declaredLength"/> lets a test declare
/// a length without holding that many bytes (a drawing over the page's ceiling), and the reads are
/// counted so that a test can say the stream was never opened. <paramref name="disposeFailure"/> is
/// what the dispose throws, when a test needs one that does: disposing is a round trip to the browser,
/// and after a dropped circuit it fails like any other.
/// </summary>
internal sealed class TestJSStreamReference(byte[] bytes, long? declaredLength = null, Exception? disposeFailure = null)
    : IJSStreamReference
{
    public TestJSStreamReference(string text) : this(Encoding.UTF8.GetBytes(text))
    {
    }

    public long Length => declaredLength ?? bytes.Length;

    public int Opened { get; private set; }

    public bool Disposed { get; private set; }

    public ValueTask<Stream> OpenReadStreamAsync(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
    {
        Opened++;
        // What Blazor's own stream does with a length over the one allowed.
        if (Length > maxAllowedSize)
            throw new ArgumentOutOfRangeException(nameof(maxAllowedSize),
                $"The incoming data stream of length {Length} exceeds the maximum allowed length {maxAllowedSize}.");
        return ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return disposeFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(disposeFailure);
    }
}
