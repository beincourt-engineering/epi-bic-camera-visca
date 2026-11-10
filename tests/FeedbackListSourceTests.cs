using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace PepperDash.Essentials.Plugins.Camera.Visca.Tests;

/// <summary>
/// Source-level guards for the camera's feedback list. The device needs a processor to run, so
/// these pin what a later edit could undo without anything else failing: monitoring consumers read
/// only keyed feedbacks on <c>Feedbacks</c>, subscribe to the list once after activation, and
/// re-send the whole device on any change to a listed feedback.
/// </summary>
public class FeedbackListSourceTests
{
    private const string ConstructorSignature =
        "public ViscaCameraDevice(string key, string name, IBasicCommunication comms, ViscaCameraConfig config)";

    private static string Source()
    {
        var path = Path.Combine(AssemblyFixture.SourceDirectory, "ViscaCameraDevice.cs");
        File.Exists(path).Should().BeTrue($"the source should be at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>The text from the opening brace after <paramref name="signature"/> to its match.</summary>
    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"'{signature}' should be in the source");

        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
        }

        throw new InvalidOperationException($"unbalanced braces after '{signature}'");
    }

    [Fact]
    public void The_List_Is_Built_In_The_Constructor_After_The_Socket_Feedback()
    {
        var ctor = Body(Source(), ConstructorSignature);

        var socket = ctor.IndexOf("SocketStatusFeedback = new IntFeedback(", StringComparison.Ordinal);
        var list = ctor.IndexOf("Feedbacks = new FeedbackCollection<Feedback>", StringComparison.Ordinal);

        socket.Should().BeGreaterThan(-1);
        list.Should().BeGreaterThan(socket, "the socket feedback must exist before the list is built");
    }

    [Theory]
    [InlineData("PowerIsOnFeedback")]
    [InlineData("CameraIsOffFeedback")]
    [InlineData("SocketStatusFeedback")]
    public void State_Feedback_Is_On_The_List(string property)
    {
        var ctor = Body(Source(), ConstructorSignature);
        var list = ctor.Substring(ctor.IndexOf("Feedbacks = new FeedbackCollection<Feedback>", StringComparison.Ordinal));

        Regex.IsMatch(list, $@"(Feedbacks\.Add\({property}\)|^\s*{property},?\s*$)", RegexOptions.Multiline)
            .Should().BeTrue($"{property} should be added to the list");
    }

    [Theory]
    [InlineData("MonitorStatusFeedback")]
    [InlineData("AutoFocusFeedback")]
    [InlineData("PanSpeedFeedback")]
    [InlineData("TiltSpeedFeedback")]
    [InlineData("ZoomSpeedFeedback")]
    [InlineData("FocusSpeedFeedback")]
    [InlineData("NumberOfPresetsFeedback")]
    [InlineData("PresetStoredFeedback")]
    public void Other_Feedbacks_Stay_Off_The_List(string property)
    {
        var source = Source();

        source.Should().NotContain($"Feedbacks.Add({property})");
        Regex.IsMatch(Body(source, ConstructorSignature), $@"^\s*{property},?\s*$", RegexOptions.Multiline)
            .Should().BeFalse($"{property} should not be in the list initializer");
    }

    [Fact]
    public void Monitor_Status_Feedback_Fires_On_Monitor_Status_Change()
    {
        Body(Source(), ConstructorSignature).Should()
            .MatchRegex(@"CommunicationMonitor\.StatusChange \+= \(o, a\) => MonitorStatusFeedback\.FireUpdate\(\);");
    }
}
