using PepperDash.Essentials.Core.DeviceInfo;

namespace ViscaCameraPlugin
{
	/// <summary>A camera's identity, with the model telemetry reads by name.</summary>
	public class ViscaCameraDeviceInfo : DeviceInfo
	{
		/// <summary>The model the camera names on its web API, e.g. "PTZOptics P7.HI"; null until read.</summary>
		public string Model { get; set; }
	}
}
