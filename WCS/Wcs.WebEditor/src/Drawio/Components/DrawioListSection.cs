using Microsoft.AspNetCore.Components;

namespace Wcs.WebEditor.Drawio.Components;

/// <summary>
/// What the lists of the draw.io page (compito 2, rows 58 and 59, and the ones after them) share,
/// written once: a section of the page whose floating window the PAGE opens and shuts (any number of
/// windows open together - see DrawioEditor.razor), where the window opens, the filter and the sort
/// of the rows, and the rule that a render of the page that changes nothing a list shows does not
/// render the list again.
/// </summary>
/// <remarks>
/// <para>
/// <b>Open and shut belong to the page.</b> The button of the strip and the window's X do not open
/// or shut anything themselves: they ask the page (<see cref="OnToggle"/>, <see cref="OnClose"/>),
/// and the page hands back <see cref="Open"/>. The page is what remembers which windows were open for the next visit, and a
/// list reads and renders only while the page says its window is open - shut, the window is only
/// hidden (DrawioWindow), and a list that decided for itself would keep reading behind it.
/// </para>
/// <para>
/// <b>The render rule.</b> The page renders often (every live frame that moves the selected device,
/// every change of its coordinators), and each of its renders hands the lists their parameters
/// again. A list states what it shows from its parameters (<see cref="ParametersArrived"/>); when
/// that did not change, the render the parameters would cause is skipped. Its own gestures, reads
/// and live frames always render. The flag is also cleared after every render
/// (<see cref="OnAfterRender"/>): a render already queued when the page's parameters arrive
/// swallows the page's own <c>StateHasChanged</c> without ever asking <see cref="ShouldRender"/>, and
/// a flag left standing would then skip the list's NEXT own render - a keystroke in the filter while
/// a row's selection is still on its way to the drawing.
/// </para>
/// </remarks>
public abstract class DrawioListSection : ComponentBase
{
    /// <summary>Whether the page has this section's window open. Shut, a list renders its button and an empty, hidden window.</summary>
    [Parameter] public bool Open { get; set; }

    /// <summary>
    /// The button of the strip was pressed: the page opens the window, brings it to the front if it is
    /// open behind another, or shuts it if it is the one in front - and leaves the other windows as they are.
    /// </summary>
    [Parameter] public EventCallback OnToggle { get; set; }

    /// <summary>The window's X was pressed, or Esc in it: the page shuts the window, and does nothing if it is already shut.</summary>
    [Parameter] public EventCallback OnClose { get; set; }

    /// <summary>Where the window opens when this browser remembers nothing about it (see DrawioWindowPlace).</summary>
    [Parameter, EditorRequired] public DrawioWindowPlace Place { get; set; } = null!;

    private object? _shownInputs;
    private bool _inputsUnchanged;

    protected string Filter { get; private set; } = "";

    protected string? SortKey { get; private set; }

    protected bool SortDescending { get; private set; }

    protected Task ToggleAsync() => OnToggle.InvokeAsync();

    protected Task CloseAsync() => OnClose.InvokeAsync();

    protected void FilterTyped(ChangeEventArgs e) => Filter = e.Value as string ?? "";

    /// <summary>A column header pressed: sorts by it, or - pressed again - reverses the order.</summary>
    protected void SortBy(string key)
    {
        if (SortKey == key)
        {
            SortDescending = !SortDescending;
            return;
        }

        SortKey = key;
        SortDescending = false;
    }

    protected void ClearSort()
    {
        SortKey = null;
        SortDescending = false;
    }

    protected string AriaSort(string key) =>
        SortKey != key ? "none" : SortDescending ? "descending" : "ascending";

    /// <summary>The rows whose text, in any of <paramref name="keys"/>, contains what the filter holds (case ignored); every row when it holds nothing.</summary>
    protected IEnumerable<T> Filtered<T>(IEnumerable<T> rows, IReadOnlyList<string> keys, Func<T, string, string> textOf)
    {
        if (string.IsNullOrWhiteSpace(Filter)) return rows;
        var wanted = Filter.Trim();
        return rows.Where(row => keys.Any(key => textOf(row, key).Contains(wanted, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The rows in the order of the chosen column, ascending or descending as <see cref="SortDescending"/> says.</summary>
    protected IOrderedEnumerable<T> Sorted<T, TKey>(IEnumerable<T> rows, Func<T, TKey> key, IComparer<TKey> comparer) =>
        SortDescending ? rows.OrderByDescending(key, comparer) : rows.OrderBy(key, comparer);

    /// <summary>
    /// Called from the list's <c>OnParametersSet</c>: what it shows from its parameters (a value tuple),
    /// and whether it must render anyway (a read has just started). <see cref="Open"/> is always part
    /// of what is shown.
    /// </summary>
    protected void ParametersArrived(object shownInputs, bool renderAnyway = false)
    {
        var inputs = (Open, shownInputs);
        _inputsUnchanged = !renderAnyway && Equals(inputs, _shownInputs);
        _shownInputs = inputs;
    }

    protected override bool ShouldRender()
    {
        var skip = _inputsUnchanged;
        _inputsUnchanged = false;
        return !skip;
    }

    protected override void OnAfterRender(bool firstRender) => _inputsUnchanged = false;
}
