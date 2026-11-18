using FluentAssertions;
using ViscaCameraPlugin;
using Xunit;

namespace PepperDash.Essentials.Plugins.Camera.Visca.Tests;

/// <summary>
/// A camera's identity from its web API. The replies are a PTZOptics camera's (firmware SOC v9.0.39), shortened, with
/// made-up serial number, MAC and addresses.
/// </summary>
public class ViscaCameraWebInfoTests
{
    private const string Device = "devname=\"ptzoptics\"\r\nmirrors=\"https://firmware.ptzoptics.com/\"\r\nversioninfo=\"SOC v9.0.39\"\r\nserial_num=\"v1i00000001\"\r\ndevice_model=\" P7.HI \"\r\n";
    private const string Network = "dhcp=\"1\"\nipaddr=\"192.0.2.34\"\nmacaddr=\"dc:ed:84:00:00:01\"\nhttpport=\"80\"\nptzport=\"5678\"\n";

    [Fact]
    public void Reads_model_serial_firmware_and_mac()
    {
        var info = ViscaCameraWebInfo.From(Device, Network);

        info.Model.Should().Be("PTZOptics P7.HI");
        info.SerialNumber.Should().Be("v1i00000001");
        info.Firmware.Should().Be("SOC v9.0.39");
        info.MacAddress.Should().Be("DC:ED:84:00:00:01");
        info.Any.Should().BeTrue();
    }

    [Fact]
    public void Names_the_maker_only_when_the_camera_names_its_firmware_site()
    {
        ViscaCameraWebInfo.From(Device.Replace("https://firmware.ptzoptics.com/", ""), null).Model.Should().Be("P7.HI");
    }

    [Fact]
    public void Either_reply_alone_gives_what_it_holds()
    {
        ViscaCameraWebInfo.From(null, Network).Should().BeEquivalentTo(new { Model = (string?)null, MacAddress = "DC:ED:84:00:00:01" });
        ViscaCameraWebInfo.From(Device, null).MacAddress.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>login</html>")]
    [InlineData("macaddr=\"not-a-mac\"")]
    public void A_reply_without_identity_gives_nothing(string reply)
    {
        ViscaCameraWebInfo.From(reply, reply).Any.Should().BeFalse();
    }

    [Fact]
    public void Blank_values_are_left_out()
    {
        ViscaCameraWebInfo.Values("serial_num=\"\"\ndevice_model=\"X\"").Should().ContainKey("device_model").And.NotContainKey("serial_num");
    }
}
