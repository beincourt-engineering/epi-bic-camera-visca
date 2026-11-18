using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

// The plugin builds without nullable annotations; the test project links this file and has them on.
#nullable disable

namespace ViscaCameraPlugin
{
	/// <summary>
	/// What a camera says about itself on its web API. Cameras on the PTZOptics platform (and the many built on the
	/// same firmware) answer two read-only queries without a login: <c>/cgi-bin/param.cgi?get_device_conf</c>
	/// (<c>device_model</c>, <c>serial_num</c>, <c>versioninfo</c>, and <c>mirrors</c>, the maker's firmware site) and
	/// <c>/cgi-bin/param.cgi?get_network_conf</c> (<c>macaddr</c>). VISCA's version inquiry gives only vendor, model
	/// and ROM codes, and no serial number or MAC.
	/// </summary>
	/// <remarks>Free of Crestron types so it can be tested on its own.</remarks>
	public sealed class ViscaCameraWebInfo
	{
		private static readonly Regex Pair = new Regex("^\\s*([A-Za-z0-9_]+)\\s*=\\s*\"([^\"]*)\"\\s*$", RegexOptions.Multiline);
		private static readonly Regex MacShape = new Regex("^[0-9A-Fa-f]{2}([:-][0-9A-Fa-f]{2}){5}$");

		/// <summary>The query for the camera's model, serial number and firmware.</summary>
		public const string DeviceQuery = "/cgi-bin/param.cgi?get_device_conf";

		/// <summary>The query for the camera's network settings, its MAC among them.</summary>
		public const string NetworkQuery = "/cgi-bin/param.cgi?get_network_conf";

		/// <summary>The model, with the maker when the camera names its firmware site ("PTZOptics P7.HI").</summary>
		public string Model { get; private set; }

		public string SerialNumber { get; private set; }

		/// <summary>The firmware as the camera names it, e.g. "SOC v9.0.39".</summary>
		public string Firmware { get; private set; }

		/// <summary>The camera's MAC in capitals with colons, or null.</summary>
		public string MacAddress { get; private set; }

		/// <summary>True when either reply gave something.</summary>
		public bool Any
		{
			get { return Model != null || SerialNumber != null || Firmware != null || MacAddress != null; }
		}

		/// <summary>The <c>key="value"</c> lines of a reply; a key without a value is left out.</summary>
		public static Dictionary<string, string> Values(string reply)
		{
			var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (string.IsNullOrEmpty(reply)) return values;
			foreach (Match m in Pair.Matches(reply.Replace("\r", "")))
			{
				var value = m.Groups[2].Value.Trim();
				if (value.Length > 0) values[m.Groups[1].Value] = value;
			}

			return values;
		}

		/// <summary>The identity in the two replies; either may be null when its query failed.</summary>
		public static ViscaCameraWebInfo From(string deviceReply, string networkReply)
		{
			var device = Values(deviceReply);
			var network = Values(networkReply);

			string model;
			device.TryGetValue("device_model", out model);
			string mirrors;
			if (model != null && device.TryGetValue("mirrors", out mirrors) && mirrors.IndexOf("ptzoptics", StringComparison.OrdinalIgnoreCase) >= 0)
				model = "PTZOptics " + model;

			string serial;
			device.TryGetValue("serial_num", out serial);
			string firmware;
			device.TryGetValue("versioninfo", out firmware);

			string mac;
			network.TryGetValue("macaddr", out mac);
			if (mac != null && !MacShape.IsMatch(mac)) mac = null;

			return new ViscaCameraWebInfo
			{
				Model = model,
				SerialNumber = serial,
				Firmware = firmware,
				MacAddress = mac == null ? null : mac.Replace('-', ':').ToUpperInvariant(),
			};
		}
	}
}
