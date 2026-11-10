using FluentAssertions;
using Xunit;

namespace PepperDash.Essentials.Plugins.Camera.Visca.Tests;

public class DeviceInterfaceTests
{
    private static Type DeviceType =>
        AssemblyFixture.PluginAssembly.GetTypes()
            .First(t => t.Name == "ViscaCameraDevice");

    private static List<string> DeviceInterfaceNames =>
        DeviceType.GetInterfaces().Select(i => i.FullName!).ToList();

    [Theory]
    [InlineData("PepperDash.Essentials.Core.IHasPowerControl")]
    [InlineData("PepperDash.Essentials.Core.IHasPowerControlWithFeedback")]
    [InlineData("PepperDash.Essentials.Devices.Common.Cameras.IHasCameraPresets")]
    [InlineData("PepperDash.Essentials.Devices.Common.Cameras.IHasCameraOff")]
    [InlineData("PepperDash.Essentials.Devices.Common.Cameras.IHasCameraPtzControl")]
    [InlineData("PepperDash.Essentials.Devices.Common.Cameras.IHasCameraFocusControl")]
    [InlineData("PepperDash.Essentials.Core.DeviceInfo.IDeviceInfoProvider")]
    [InlineData("PepperDash.Essentials.Core.IHasFeedback")]
    [InlineData("PepperDash.Essentials.Core.ICommunicationMonitor")]
    public void Device_Implements_Interface(string interfaceFullName)
    {
        DeviceInterfaceNames.Should().Contain(interfaceFullName,
            $"ViscaCameraDevice should implement {interfaceFullName}");
    }

    [Fact]
    public void Device_Has_PowerIsOnFeedback_Property()
    {
        var prop = DeviceType.GetProperty("PowerIsOnFeedback");
        prop.Should().NotBeNull();
        prop!.PropertyType.Name.Should().Be("BoolFeedback");
    }

    [Fact]
    public void Device_Feedbacks_Property_Is_FeedbackCollection()
    {
        var prop = DeviceType.GetProperty("Feedbacks");
        prop.Should().NotBeNull();
        prop!.PropertyType.IsGenericType.Should().BeTrue();
        prop.PropertyType.GetGenericTypeDefinition().FullName
            .Should().Be("PepperDash.Essentials.Core.FeedbackCollection`1");
    }

    [Fact]
    public void Device_Presets_Property_Is_CameraPreset_List()
    {
        var prop = DeviceType.GetProperty("Presets");
        prop.Should().NotBeNull();
        prop!.PropertyType.IsGenericType.Should().BeTrue();
        prop.PropertyType.GetGenericTypeDefinition().Name.Should().Be("List`1");
        prop.PropertyType.GetGenericArguments()[0].FullName
            .Should().Be("PepperDash.Essentials.Devices.Common.Cameras.CameraPreset");
    }

    [Fact]
    public void Device_Has_PresetsListHasChanged_Event()
    {
        var evt = DeviceType.GetEvent("PresetsListHasChanged");
        evt.Should().NotBeNull();
    }
}
