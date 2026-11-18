using FluentAssertions;
using Xunit;

namespace PepperDash.Essentials.Plugins.Camera.Visca.Tests;

/// <summary>
/// Source guards for the camera's identity from its web API: read once in the background with the VISCA version
/// inquiry, carried on a DeviceInfo with a public Model telemetry reads by name, and preferred over the VISCA ROM codes.
/// </summary>
public class WebIdentitySourceTests
{
    private static string Source(string file) =>
        File.ReadAllText(Path.Combine(AssemblyFixture.SourceDirectory, file));

    [Fact]
    public void The_device_info_carries_a_model()
    {
        Source("ViscaCameraDeviceInfo.cs").Should().Contain("public class ViscaCameraDeviceInfo : DeviceInfo")
            .And.Contain("public string Model { get; set; }");
        Source("ViscaCameraDevice.cs").Should().Contain("public DeviceInfo DeviceInfo { get; } = new ViscaCameraDeviceInfo();");
    }

    [Fact]
    public void The_web_identity_is_read_with_the_version_inquiry_and_wins_over_it()
    {
        var device = Source("ViscaCameraDevice.cs");

        device.Should().MatchRegex(@"SendBytes\(ViscaVersionReply\.Inquiry\(_address\)\);\s*ReadWebIdentity\(\);");
        device.Should().Contain("ViscaCameraWebReader.Read(_webAddress, out var outcome)");
        device.Should().Contain("if (!_webIdentityRead && firmware != DeviceInfo.FirmwareVersion)");
        // the camera's device query answers 404 without a User-Agent
        Source("ViscaCameraWebReader.cs").Should().Contain("client.DefaultRequestHeaders.UserAgent.ParseAdd(");
    }
}
