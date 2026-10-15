using System.Text;
using Microsoft.Playwright;

namespace Wcs.WebEditor.Browser.Test;

/// <summary>
/// Queue item 13: one message of the page's Blazor circuit as the browser sent or received it.
/// </summary>
/// <param name="FromTheBrowser">True for a message the browser sent to the server (JS to .NET).</param>
/// <param name="Bytes">Its size on the wire: what SignalR's ceiling on a message is measured against.</param>
/// <param name="Words">
/// The readable runs at its head - Blazor's protocol is MessagePack, so the hub method
/// (<c>BeginInvokeDotNetFromJS</c>, <c>EndInvokeJSFromDotNet</c>, <c>JS.BeginInvokeJS</c>...) and
/// the name of what an interop call calls are there as plain text among the binary.
/// </param>
public sealed record CircuitFrame(bool FromTheBrowser, int Bytes, string Words)
{
    private const int WordsKept = 5;
    private const int LongestWord = 40;

    internal static CircuitFrame Of(bool fromTheBrowser, IWebSocketFrame frame)
    {
        var bytes = frame.Binary ?? Encoding.UTF8.GetBytes(frame.Text ?? "");
        return new CircuitFrame(fromTheBrowser, bytes.Length, WordsOf(bytes));
    }

    public override string ToString() =>
        $"{(FromTheBrowser ? "browser -> server" : "server -> browser")} {Bytes,9:N0} B  {Words}";

    private static string WordsOf(byte[] bytes)
    {
        var words = new List<string>();
        var run = new StringBuilder();
        foreach (var b in bytes)
        {
            if (b is >= 0x20 and < 0x7f)
            {
                if (run.Length < LongestWord) run.Append((char)b);
                continue;
            }
            if (run.Length >= 4) words.Add(run.ToString());
            run.Clear();
            if (words.Count == WordsKept) break;
        }
        if (words.Count < WordsKept && run.Length >= 4) words.Add(run.ToString());
        return string.Join(" | ", words);
    }
}
