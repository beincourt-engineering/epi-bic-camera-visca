using System;
using System.Net.Http;

namespace ViscaCameraPlugin
{
	/// <summary>
	/// Reads <see cref="ViscaCameraWebInfo"/> from a camera's web API: two plain HTTP GETs, read-only, no login. Kept
	/// apart from the device, whose Crestron namespaces have network types of the same names.
	/// </summary>
	public static class ViscaCameraWebReader
	{
		private static readonly HttpClient Http = CreateClient();

		// The camera's device query answers 404 to a request without a User-Agent (HttpClient sends none by default);
		// the network query answers either way.
		private static HttpClient CreateClient()
		{
			var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
			client.DefaultRequestHeaders.UserAgent.ParseAdd("Essentials-ViscaCamera/1.0");
			return client;
		}

		/// <summary>The camera's identity, or null when neither query answered. Blocks for up to ten seconds; never throws.</summary>
		public static ViscaCameraWebInfo Read(string host, out string outcome)
		{
			var device = Get("http://" + host + ViscaCameraWebInfo.DeviceQuery, out var deviceError);
			var network = Get("http://" + host + ViscaCameraWebInfo.NetworkQuery, out var networkError);
			if (device == null && network == null)
			{
				outcome = "no web API: " + deviceError;
				return null;
			}

			var info = ViscaCameraWebInfo.From(device, network);
			outcome = info.Any
				? "read from the web API" + (device == null ? " (device query failed: " + deviceError + ")" : network == null ? " (network query failed: " + networkError + ")" : "")
				: "a web API without model, serial, firmware or MAC";
			return info.Any ? info : null;
		}

		private static string Get(string url, out string error)
		{
			error = null;
			try
			{
				using (var response = Http.GetAsync(url).GetAwaiter().GetResult())
				{
					if (!response.IsSuccessStatusCode)
					{
						error = "HTTP " + (int)response.StatusCode;
						return null;
					}

					return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
				}
			}
			catch (Exception e)
			{
				error = e.Message;
				return null;
			}
		}
	}
}
