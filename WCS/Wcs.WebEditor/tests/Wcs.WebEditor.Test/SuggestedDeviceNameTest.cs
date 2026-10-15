using Wcs.Api.Contracts.Dtos;
using Wcs.WebEditor.Drawio;
using Xunit;
using static Wcs.Common.Enums;

namespace Wcs.WebEditor.Test;

/// <summary>
/// The rule itself, on its own: the label of the type plus the first number that no device of the
/// plant is using. The page's side of it - that the plant handed over is the plant as it is at that
/// instant, and not a snapshot - is measured in <c>DrawioEditorCreateTest</c>, where the twenty
/// cells of a paste and two drops in a row go through the real creation.
/// </summary>
public sealed class SuggestedDeviceNameTest
{
    private static PlantItemDto Named(string? name) =>
        new(Guid.NewGuid(), name, DeviceTypes.Automation, null, null, null, default, default, 0, null,
            0, 0, 0, 0, default!, 0, 0, 10, 10, 0);

    private static string For(string label, params string?[] names) =>
        SuggestedDeviceName.For(label, [.. names.Select(Named)]);

    [Fact]
    public void OnAnEmptyPlantItIsTheLabelAndOne() =>
        Assert.Equal("Automation 1", SuggestedDeviceName.For("Automation", []));

    /// <summary>The real names of this plant look like this, and none of them is one of ours.</summary>
    [Fact]
    public void ThePlantsOwnNamesAreNotOurs() =>
        Assert.Equal("Automation 1", For("Automation", "IN01.0001.0001", "IN02.0002.0007", null, ""));

    [Fact]
    public void TheFirstFreeNumberIsTheFirstFreeOne() =>
        Assert.Equal("Automation 2", For("Automation", "Automation 1", "Automation 3", "Automation 4"));

    [Fact]
    public void ANumberedNameOfAnotherTypeDoesNotCount() =>
        Assert.Equal("Buffer 1", For("Buffer", "Automation 1", "Automation 2"));

    /// <summary>
    /// A label that is a PREFIX of another one must not be confused with it: "Circular Distributor 1"
    /// is not a "Circular" - the space is part of what is compared, and a naive
    /// <c>StartsWith(label)</c> would take it.
    /// </summary>
    [Fact]
    public void ALabelThatBeginsLikeAnotherOneIsNotTheSameLabel() =>
        Assert.Equal("Circular 1", For("Circular", "Circular Distributor 1", "Circular Distributor 2"));

    /// <summary>The other way round as well: the longer label does not see the shorter one's numbers.</summary>
    [Fact]
    public void AndTheLongerLabelDoesNotSeeTheShorterOnes() =>
        Assert.Equal("Circular Distributor 1", For("Circular Distributor", "Circular 1", "Circular 2"));

    /// <summary>
    /// Case is ignored, because to whoever reads a list "automation 1" and "Automation 1" are the
    /// same name: suggesting the second next to the first would be suggesting a duplicate.
    /// </summary>
    [Fact]
    public void TheSameNameInAnotherCaseIsTheSameName() =>
        Assert.Equal("Automation 2", For("Automation", "automation 1"));

    /// <summary>
    /// Names that merely BEGIN like ours are somebody else's, and taking them for ours would burn a
    /// number for nothing. Each one of these would be taken by a rule written a little too loosely.
    /// </summary>
    /// <remarks>
    /// <b>On its own this Theory is a weak net, and the review measured how weak</b>: it always
    /// asserts <c>Automation 1</c>, and on a plant holding one single name the number 1 stays free
    /// whatever the rule decides about that name - so removing either guard of
    /// <c>SuggestedDeviceName.NumberOf</c> left the whole suite green. It is kept because it is the
    /// cheap statement of the family ("this name does not make us move"), and the Theory below is
    /// the one with teeth: there the contested name carries the number that is about to be handed
    /// out, so taking it for ours changes the answer.
    /// </remarks>
    [Theory]
    [InlineData("Automation 1x")]
    [InlineData("Automation 1 ")]
    [InlineData("Automation  1")]
    [InlineData("Automation +1")]
    [InlineData("Automation -1")]
    [InlineData("Automation 007")]
    [InlineData("Automation 1.0")]
    [InlineData("Automation 1\0")]
    [InlineData("Automation")]
    [InlineData("Automation ")]
    [InlineData("Automation 99999999999999999999")]
    public void ANameThatOnlyBeginsLikeOursIsNotOurs(string other) =>
        Assert.Equal("Automation 1", For("Automation", other));

    /// <summary>
    /// The same family, but with the contested name carrying <b>the very number about to be handed
    /// out</b>: 1 to 6 are taken, so a rule that read any of these as "ours" would move on to
    /// <c>Automation 8</c> and this Theory goes red. It is the review's finding of this batch - the
    /// Theory above could not fail for two of its cases.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two of these kill a guard of their own, and it was measured by taking that guard out.</b>
    /// <c>Automation 007</c> is the only case rejected by <c>digits[0] == '0'</c> alone (the digit
    /// loop lets it through and <c>int.TryParse</c> reads it as 7), and <c>Automation 7\0</c> is the
    /// only case rejected by the digit loop alone: <c>int.TryParse("7\0", NumberStyles.None,
    /// InvariantCulture)</c> answers <b>true</b> with 7 - measured on .NET 10, not deduced, and it
    /// is the parser's own allowance for trailing NUL characters. A name like that is not a fantasy
    /// on this plant: a fixed-width CHAR column and a string coming back from a PLC both end that
    /// way.
    /// </para>
    /// <para>
    /// The other cases are rejected by the digit loop AND by <c>NumberStyles.None</c>, so no single
    /// mutation makes them red; they stay because they are what a rule rewritten "a little too
    /// loosely" - a leading-digit scan, or <c>NumberStyles.Integer</c> - would swallow.
    /// </para>
    /// <para>
    /// <b>No case of this shape exists for <c>Automation 99999999999999999999</c></b>, and that is
    /// a property of the rule rather than an omission: the number handed out is at most one more
    /// than the count of devices, so a name too big for an <see cref="int"/> can never BE the
    /// contested number. What the case above still pins is that such a name neither throws nor
    /// counts - a <c>Parse</c> instead of a <c>TryParse</c>, or a rule counting matching prefixes
    /// instead of collecting numbers, turns it red.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("Automation 7x")]
    [InlineData("Automation 7 ")]
    [InlineData("Automation  7")]
    [InlineData("Automation +7")]
    [InlineData("Automation -7")]
    [InlineData("Automation 007")]
    [InlineData("Automation 7.0")]
    [InlineData("Automation 7\0")]
    public void ANameThatOnlyBeginsLikeOursDoesNotTakeTheNumberAboutToBeHandedOut(string other) =>
        Assert.Equal("Automation 7", For("Automation",
            "Automation 1", "Automation 2", "Automation 3", "Automation 4", "Automation 5",
            "Automation 6", other));

    /// <summary>
    /// The numbers do not have to be in order on the plant, and a repeated one does not shift
    /// anything: it is a SET of what is taken, not a count and not a maximum.
    /// </summary>
    [Fact]
    public void TheOrderOnThePlantDoesNotMatterAndNeitherDoesARepeat() =>
        Assert.Equal("Automation 4", For("Automation", "Automation 3", "Automation 1", "Automation 3", "Automation 2"));
}
