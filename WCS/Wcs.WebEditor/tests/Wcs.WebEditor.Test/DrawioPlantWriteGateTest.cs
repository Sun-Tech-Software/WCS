using System.Reflection;
using Wcs.WebEditor.Drawio.Coordination;
using Xunit;

namespace Wcs.WebEditor.Test;

public sealed class DrawioPlantWriteGateTest
{
    [Fact]
    public void OnlyTheLeaseOwnerCanReleaseTheWriter()
    {
        var gate = new DrawioPlantWriteGate();
        Assert.True(gate.TryEnter(DrawioPlantWriter.Creating, out var creation));
        Assert.False(gate.TryEnter(DrawioPlantWriter.Drawing, out _));
        Assert.Equal(DrawioPlantWriter.Creating, gate.Current);

        creation!.Dispose();

        Assert.Equal(DrawioPlantWriter.Nobody, gate.Current);
    }

    [Fact]
    public void ADisposedLeaseCannotReleaseANewerOwner()
    {
        var gate = new DrawioPlantWriteGate();
        gate.TryEnter(DrawioPlantWriter.Creating, out var first);
        first!.Dispose();
        gate.TryEnter(DrawioPlantWriter.Drawing, out var second);

        first.Dispose();

        Assert.Equal(DrawioPlantWriter.Drawing, gate.Current);
        second!.Dispose();
    }

    [Fact]
    public void ChangedIsRaisedOnceForEachSuccessfulTransitionOnly()
    {
        var gate = new DrawioPlantWriteGate();
        var transitions = new List<DrawioPlantWriter>();
        gate.Changed += () => transitions.Add(gate.Current);

        Assert.True(gate.TryEnter(DrawioPlantWriter.Panel, out var lease));
        Assert.False(gate.TryEnter(DrawioPlantWriter.Cells, out _));
        lease!.Dispose();
        lease.Dispose();

        Assert.Equal([DrawioPlantWriter.Panel, DrawioPlantWriter.Nobody], transitions);
    }

    /// <summary>
    /// The subjects come from the code, not from a list written here: the owners from the enum, and
    /// the GESTURES from <see cref="DrawioWriteRefusal"/>'s own public static methods, taken by
    /// reflection - every refusal throws on an owner it does not list, so a writer added to the enum
    /// without its arms, or a new <c>ForX</c> without an arm for some writer, throws here.
    /// </summary>
    /// <remarks>
    /// Fix round 1 of compito 2: the gestures used to be one hand-written call each, and a new
    /// <c>ForY</c> would have gone unasked. The shape is checked too: a gesture whose first parameter
    /// is not the owner, or whose other parameters are not words, is a new kind of refusal this test
    /// does not know how to ask - it fails and says so instead of skipping it.
    /// </remarks>
    [Fact]
    public void EveryWriterHasARefusalForEveryGesture()
    {
        var gestures = typeof(DrawioWriteRefusal).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        // A FLOOR, not the list: the list is whatever DrawioWriteRefusal declares, and the loop below
        // asks every one of them. Ten were declared when this floor was last raised (compito 5, fix
        // round 3, ForDuplication the tenth); fewer would mean the reflection itself lost its subjects,
        // more is only a newer gesture.
        Assert.True(gestures.Length >= 10, $"Only {gestures.Length} refusals found by reflection.");

        foreach (var gesture in gestures)
        {
            var parameters = gesture.GetParameters();
            Assert.True(gesture.ReturnType == typeof(string), $"{gesture.Name} does not return the refusal's words.");
            Assert.True(parameters.Length >= 1 && parameters[0].ParameterType == typeof(DrawioPlantWriter),
                $"{gesture.Name}: its first parameter is not the writer that holds the gate.");
            Assert.True(parameters.Skip(1).All(parameter => parameter.ParameterType == typeof(string)),
                $"{gesture.Name}: a parameter that is not words, which this test does not know how to fill.");

            foreach (var owner in Enum.GetValues<DrawioPlantWriter>().Where(writer => writer != DrawioPlantWriter.Nobody))
            {
                object?[] arguments = [owner, .. parameters.Skip(1).Select(_ => (object?)"the gesture")];
                var words = gesture.Invoke(null, arguments) as string;
                Assert.False(string.IsNullOrWhiteSpace(words), $"{gesture.Name}({owner}) has no words.");
            }
        }
    }
}
