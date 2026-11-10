using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Crestron.SimplSharp;
using Crestron.SimplSharpPro.DeviceSupport;
using PepperDash.Core;
using PepperDash.Core.Logging;
using PepperDash.Essentials.Core;
using PepperDash.Essentials.Core.Bridges;
using PepperDash.Essentials.Core.DeviceInfo;
using PepperDash.Essentials.Devices.Common.Cameras;
using Feedback = PepperDash.Essentials.Core.Feedback;

namespace ViscaCameraPlugin
{
	public class ViscaCameraDevice : EssentialsBridgeableDevice, ICommunicationMonitor, IRoutingSource,
		IHasCameraOff, IHasCameraPtzControl, IHasCameraFocusControl, ICameraCapabilities,
		IHasPowerControlWithFeedback, IHasCameraPresets, IDeviceInfoProvider, IHasFeedback
	{
		/// <summary>
		/// The camera's address from configuration and the version it reports. One instance for the
		/// life of the device, so what is written to it is what readers see.
		/// </summary>
		public DeviceInfo DeviceInfo { get; } = new DeviceInfo();

		public event DeviceInfoChangeHandler DeviceInfoChanged;

		/// <summary>How long after the connection settles to ask the camera for its version.</summary>
		private const int VersionInquiryDelayMs = 2000;

		public bool CanPan { get; private set; }

		public bool CanTilt { get; private set; }

		public bool CanZoom { get; private set; }

		public bool CanFocus { get; private set; }

		public RoutingPortCollection<RoutingOutputPort> OutputPorts { get; private set; }

		private RoutingOutputPort AnyOut;

		public StatusMonitorBase CommunicationMonitor { get; private set; }
		private readonly IBasicCommunication _comms;
		private readonly bool _commsIsSerial;
		private readonly bool _useHeader;
		private uint _counter;

		/// <summary>
		/// Held from choosing a sequence number until the message carrying it is sent, and around a
		/// sequence reset. Without it two callers can take numbers 1 and 2 and send 2 first, which a
		/// camera that tracks sequence numbers rejects.
		/// </summary>
		private readonly object _counterLock = new object();

		/// <summary>The last VISCA payload sent, kept so it can be sent again after a sequence reset.</summary>
		private byte[] _lastCommand;

		/// <summary>Sequence numbers rejected in a row, so reset-and-resend cannot run away.</summary>
		private int _sequenceErrors;

		// VISCA over IP framing: an 8-byte header carrying the payload type, the payload length and
		// a 4-byte sequence number - length and sequence number both big-endian.
		private const int HeaderLength = 8;
		private static readonly byte[] PayloadTypeCommand = { 0x01, 0x00 };
		private static readonly byte[] PayloadTypeInquiry = { 0x01, 0x10 };
		private static readonly byte[] PayloadTypeControl = { 0x02, 0x00 };
		private const int PayloadTypeControlValue = 0x0200;
		private const int PayloadTypeControlReplyValue = 0x0201;

		/// <summary>How long to let a sequence number reset settle before sending the next command.</summary>
		private const int SequenceResetSettleMs = 250;

		/// <summary>Stop resending after this many rejections in a row.</summary>
		private const int SequenceErrorsMax = 3;

		private readonly byte _address = 0x81;
		private const uint AddressMax = 7;

		private readonly int _pollTimeMs = 30000; // 30s
		private const uint PanSpeedDefault = 9; // 00...18 (hex)
		private const uint PanSpeedMax = 18;
		private const uint TiltSpeedDefault = 9; // 00...18 (hex)
		private const uint TiltSpeedMax = 18;
		private const uint ZoomSpeedDefault = 4; // 00...07 (hex)
		private const uint ZoomSpeedMax = 7;
		private const uint FocusSpeedDefault = 4; // 00...07 (hex)
		private const uint FocusSpeedMax = 7;
		private const int PresetStoreHoldTimeMs = 5000; // 5s

		private bool _cameraIsOff;
		public bool CameraIsOff
		{
			get { return _cameraIsOff; }
			set
			{
				if (_cameraIsOff == value) return;
				_cameraIsOff = value;
				CameraIsOffFeedback.FireUpdate();
				PowerIsOnFeedback.FireUpdate();
			}
		}

		private bool _autoFocus;
		public bool AutoFocus
		{
			get { return _autoFocus; }
			set
			{
				if (_autoFocus == value) return;
				_autoFocus = value;
				AutoFocusFeedback.FireUpdate();
			}
		}

		private uint _panSpeed = PanSpeedDefault;
		public uint PanSpeed
		{
			get { return _panSpeed; }
			set
			{
				if (_panSpeed == value) return;
				_panSpeed = (value < 1 || value > PanSpeedMax) ? PanSpeedDefault : value;
				PanSpeedFeedback.FireUpdate();
			}
		}

		private uint _tiltSpeed = TiltSpeedDefault;
		public uint TiltSpeed
		{
			get { return _tiltSpeed; }
			set
			{
				if (_tiltSpeed == value) return;
				_tiltSpeed = (value < 1 || value > TiltSpeedMax) ? TiltSpeedDefault : value;
				TiltSpeedFeedback.FireUpdate();
			}
		}

		private uint _zoomSpeed = ZoomSpeedDefault;
		public uint ZoomSpeed
		{
			get { return _zoomSpeed; }
			set
			{
				if (_zoomSpeed == value) return;
				_zoomSpeed = (value < 1 || value > ZoomSpeedMax) ? ZoomSpeedDefault : value;
				ZoomSpeedFeedback.FireUpdate();
			}
		}

		private uint _focusSpeed = FocusSpeedDefault;
		public uint FocusSpeed
		{
			get { return _focusSpeed; }
			set
			{
				if (_focusSpeed == value) return;
				_focusSpeed = (value < 1 || value > FocusSpeedMax) ? FocusSpeedDefault : value;
				FocusSpeedFeedback.FireUpdate();
			}
		}

		private int _numberOfPresets;
		public int NumberOfPresets
		{
			get { return _numberOfPresets; }
			set
			{
				if (value == _numberOfPresets) return;
				_numberOfPresets = value;
				NumberOfPresetsFeedback.FireUpdate();
			}
		}

		private bool _presetStored;

		public bool PresetStored
		{
			get { return _presetStored; }
			set
			{
				if (value == _presetStored) return;
				_presetStored = value;
				PresetStoredFeedback.FireUpdate();
			}
		}

		private readonly uint _privacyOnPreset;
		private readonly uint _privacyOffPreset;

		public IntFeedback NumberOfPresetsFeedback { get; private set; }
		public BoolFeedback PresetStoredFeedback { get; private set; }

		/// <summary>
		/// Camera presets list, ordered by config order. CameraPreset.ID is the 1-based
		/// index passed to PresetSelect/PresetStore; the raw VISCA preset number from
		/// config is kept internally in _presetsByIndex.
		/// </summary>
		public List<CameraPreset> Presets { get; private set; }

		/// <summary>
		/// Raised when the presets list has been (re)built
		/// </summary>
		public event EventHandler<EventArgs> PresetsListHasChanged;

		private readonly Dictionary<uint, ViscaCameraPresetsConfig> _presetsByIndex = new Dictionary<uint, ViscaCameraPresetsConfig>();

		public Dictionary<uint, StringFeedback> PresetNamesFeedbacks { get; private set; }

		public BoolFeedback OnlineFeedback { get { return CommunicationMonitor.IsOnlineFeedback; } }
		public IntFeedback SocketStatusFeedback { get; private set; }
		public IntFeedback MonitorStatusFeedback { get; private set; }
		public BoolFeedback CameraIsOffFeedback { get; private set; }
		public BoolFeedback PowerIsOnFeedback { get; private set; }
		public BoolFeedback AutoFocusFeedback { get; private set; }
		public IntFeedback PanSpeedFeedback { get; private set; }
		public IntFeedback TiltSpeedFeedback { get; private set; }
		public IntFeedback ZoomSpeedFeedback { get; private set; }
		public IntFeedback FocusSpeedFeedback { get; private set; }

		/// <summary>
		/// The device's state feedbacks: power, camera off and, for IP control, the socket status.
		/// Built once in the constructor so a consumer that subscribes to them after activation sees
		/// the full set. Speeds, presets and the comm monitor status stay off it; the monitor is read
		/// through <see cref="CommunicationMonitor"/>.
		/// </summary>
		public FeedbackCollection<Feedback> Feedbacks { get; private set; }



		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="key">device key</param>
		/// <param name="name">device name</param>
		/// <param name="config">device config</param>
		/// <param name="comms">IBasicCommunications</param>
		public ViscaCameraDevice(string key, string name, IBasicCommunication comms, ViscaCameraConfig config)
			: base(key, name)
		{
			this.LogInformation("Constructing new VISCA Camera instance");

			AnyOut = new RoutingOutputPort(RoutingPortNames.AnyOut, eRoutingSignalType.Video,
			eRoutingPortConnectionType.None, null, this);

			OutputPorts = new RoutingPortCollection<RoutingOutputPort>();

			OutputPorts.Add(AnyOut);

			MonitorStatusFeedback = new IntFeedback("monitorStatus", () => (int)CommunicationMonitor.Status);
			CameraIsOffFeedback = new BoolFeedback("cameraIsOff", () => CameraIsOff);
			PowerIsOnFeedback = new BoolFeedback("powerIsOn", () => !CameraIsOff);
			AutoFocusFeedback = new BoolFeedback("autoFocus", () => AutoFocus);
			PanSpeedFeedback = new IntFeedback("panSpeed", () => (int)PanSpeed);
			TiltSpeedFeedback = new IntFeedback("tiltSpeed", () => (int)TiltSpeed);
			ZoomSpeedFeedback = new IntFeedback("zoomSpeed", () => (int)ZoomSpeed);
			FocusSpeedFeedback = new IntFeedback("focusSpeed", () => (int)FocusSpeed);

			_pollTimeMs = config.PollTimeMs > 0 ? config.PollTimeMs : _pollTimeMs;
			_address = (config.Address > 0 && config.Address <= AddressMax)
				? Convert.ToByte(0x80 + config.Address)
				: Convert.ToByte(0x81);

			PanSpeed = config.PanSpeed == 0 ? PanSpeedDefault : config.PanSpeed;
			TiltSpeed = config.TiltSpeed == 0 ? TiltSpeedDefault : config.TiltSpeed;
			ZoomSpeed = config.ZoomSpeed == 0 ? ZoomSpeedDefault : config.ZoomSpeed;
			FocusSpeed = config.FocusSpeed == 0 ? FocusSpeedDefault : config.FocusSpeed;

			if (config.Capabilities != null)
			{
				CanPan = config.Capabilities.CanPan;
				CanTilt = config.Capabilities.CanTilt;
				CanZoom = config.Capabilities.CanZoom;
				CanFocus = config.Capabilities.CanFocus;
			}
			else
			{
				CanPan = true;
				CanTilt = true;
				CanZoom = true;
				CanFocus = true;
			}

			_privacyOnPreset = config.PrivacyOnPreset;
			_privacyOffPreset = config.PrivacyOffPreset;

			_comms = comms;

			var address = config.Control?.TcpSshProperties?.Address;
			if (!string.IsNullOrEmpty(address))
			{
				DeviceInfo.IpAddress = address;
				DeviceInfo.HostName = address;
			}

			if (config.Control.Method.ToString().ToLower() == "udp")
			{
				_useHeader = true;

				// Each datagram is one complete VISCA-over-IP message, and the header in front of it
				// can hold 0xFF as part of a sequence number, so gathering on 0xFF would cut messages
				// in the wrong place.
				_comms.BytesReceived += Handle_MessageReceived;

				// No separate poll timer: the communication monitor created below polls UDP too.
			}
			else
			{
				var commsGather = new CommunicationGather(_comms, (char)0xFF);
				commsGather.LineReceived += Handle_BytesRecieved;
			}

			CommunicationMonitor = new GenericCommunicationMonitor(this, _comms, _pollTimeMs, 120000, 300000, Poll);

			// keep the monitor status feedback current whenever the monitor changes, not only when a
			// bridge comes online
			CommunicationMonitor.StatusChange += (o, a) => MonitorStatusFeedback.FireUpdate();

			var socket = _comms as ISocketStatus;
			if (socket != null)
			{
				// device is configured for IP control
				_commsIsSerial = false;
				socket.ConnectionChange += Socket_ConnectionChange;

				SocketStatusFeedback = new IntFeedback("socketStatus", () => (int)socket.ClientStatus);
			}
			else
			{
				// device is configured for RS232 control
				_commsIsSerial = true;
				CommunicationMonitor.Start();
				InitializeCamera();
			}

			Feedbacks = new FeedbackCollection<Feedback>
			{
				PowerIsOnFeedback,
				CameraIsOffFeedback
			};
			if (SocketStatusFeedback != null)
				Feedbacks.Add(SocketStatusFeedback);

			Presets = new List<CameraPreset>();
			PresetNamesFeedbacks = new Dictionary<uint, StringFeedback>();
			NumberOfPresetsFeedback = new IntFeedback("numberOfPresets", () => NumberOfPresets);
			PresetStoredFeedback = new BoolFeedback("presetStored", () => PresetStored);
			InitializePresets(config.Presets);
		}


		/// <summary>
		/// Use the Initialize to connect the device and start the comms monitor
		/// </summary>
		/// <returns></returns>
		protected override void Initialize()
		{
			try
			{
				// Essentials will handle the connect method to the device
				_comms.Connect();
				// Essentials will handle starting the comms monitor
				CommunicationMonitor.Start();

				base.Initialize();
			}
			catch (Exception ex)
			{
				this.LogError(ex, "Exception in Initialize: {0}", ex.Message);
				throw;
			}
		}


		private void InitializePresets(List<ViscaCameraPresetsConfig> presets)
		{
			if (presets == null)
			{
				this.LogInformation("InitializePresets failed, preset list is null");
				return;
			}

			this.LogInformation("Initializing {0} presets", presets.Count());

			// clear so the method is safe to call more than once (e.g. re-initialization)
			_presetsByIndex.Clear();
			Presets.Clear();
			PresetNamesFeedbacks.Clear();

			uint index = 1;
			foreach (var preset in presets)
			{
				var id = preset.Id;
				var name = preset.Name;

				this.LogInformation("Initializing Preset-{0}: Name-{1}, Id-{2}",
					index, name, id);


				_presetsByIndex.Add(index, preset);
				Presets.Add(new CameraPreset((int)index, name, true, true));
				PresetNamesFeedbacks.Add(index, new StringFeedback("preset" + id, () => name));
				index++;
			}

			NumberOfPresets = Presets.Count();
			foreach (var feedback in PresetNamesFeedbacks)
				feedback.Value.FireUpdate();

			PresetsListHasChanged?.Invoke(this, EventArgs.Empty);
		}

		#region Overrides of EssentialsBridgeableDevice

		/// <summary>
		/// Link to API method replaces bridge class, this method will be called by the bridge directly
		/// </summary>
		/// <param name="trilist"></param>
		/// <param name="joinStart"></param>
		/// <param name="joinMapKey"></param>
		/// <param name="bridge"></param>
		public override void LinkToApi(BasicTriList trilist, uint joinStart, string joinMapKey, EiscApiAdvanced bridge)
		{
			var joinMap = new ViscaCameraBridgeJoinMap(joinStart);

			// This adds the join map to the collection on the bridge
			if (bridge != null)
			{
				bridge.AddJoinMap(Key, joinMap);
			}

			var customJoins = JoinMapHelper.TryGetJoinMapAdvancedForDevice(joinMapKey);
			if (customJoins != null)
			{
				joinMap.SetCustomJoinData(customJoins);
			}

			this.LogDebug("Linking to Trilist '{0}'", trilist.ID.ToString("X"));
			this.LogInformation("Linking to Bridge Type {0}", GetType().Name);

			// link joins to bridge
			trilist.SetString(joinMap.DeviceName.JoinNumber, Name);

			OnlineFeedback.LinkInputSig(trilist.BooleanInput[joinMap.IsOnline.JoinNumber]);
			MonitorStatusFeedback.LinkInputSig(trilist.UShortInput[joinMap.SocketStatus.JoinNumber]);
			if (SocketStatusFeedback != null)
				SocketStatusFeedback.LinkInputSig(trilist.UShortInput[joinMap.SocketStatus.JoinNumber]);

			// power
			trilist.SetSigTrueAction(joinMap.PowerOn.JoinNumber, CameraOn);
			trilist.SetSigTrueAction(joinMap.PowerOff.JoinNumber, CameraOff);

			CameraIsOffFeedback.LinkComplementInputSig(trilist.BooleanInput[joinMap.PowerOn.JoinNumber]);
			CameraIsOffFeedback.LinkInputSig(trilist.BooleanInput[joinMap.PowerOff.JoinNumber]);

			// home
			trilist.SetSigTrueAction(joinMap.Home.JoinNumber, PositionHome);

			// pan
			trilist.SetBoolSigAction(joinMap.PanLeft.JoinNumber, sig =>
			{
				if (sig) PanLeft();
				else PanStop();
			});

			trilist.SetBoolSigAction(joinMap.PanRight.JoinNumber, sig =>
			{
				if (sig) PanRight();
				else PanStop();
			});

			// tilt
			trilist.SetBoolSigAction(joinMap.TiltDown.JoinNumber, sig =>
			{
				if (sig) TiltDown();
				else TiltStop();
			});

			trilist.SetBoolSigAction(joinMap.TiltUp.JoinNumber, sig =>
			{
				if (sig) TiltUp();
				else TiltStop();
			});

			// zoom
			trilist.SetBoolSigAction(joinMap.ZoomIn.JoinNumber, sig =>
			{
				if (sig) ZoomIn();
				else ZoomStop();
			});

			trilist.SetBoolSigAction(joinMap.ZoomOut.JoinNumber, sig =>
			{
				if (sig) ZoomOut();
				else ZoomStop();
			});


			// focus
			trilist.SetBoolSigAction(joinMap.FocusNear.JoinNumber, sig =>
			{
				if (sig) FocusNear();
				else FocusStop();
			});

			trilist.SetBoolSigAction(joinMap.FocusFar.JoinNumber, sig =>
			{
				if (sig) FocusFar();
				else FocusStop();
			});

			trilist.SetSigTrueAction(joinMap.TriggerAutoFocus.JoinNumber, TriggerAutoFocus);

			trilist.SetUShortSigAction(joinMap.PanSpeed.JoinNumber, panSpeed => PanSpeed = panSpeed);
			trilist.SetUShortSigAction(joinMap.TiltSpeed.JoinNumber, tiltSpeed => TiltSpeed = tiltSpeed);
			trilist.SetUShortSigAction(joinMap.ZoomSpeed.JoinNumber, zoomSpeed => ZoomSpeed = zoomSpeed);
			trilist.SetUShortSigAction(joinMap.FocusSpeed.JoinNumber, focusSpeed => FocusSpeed = focusSpeed);

			PanSpeedFeedback.LinkInputSig(trilist.UShortInput[joinMap.PanSpeed.JoinNumber]);
			TiltSpeedFeedback.LinkInputSig(trilist.UShortInput[joinMap.TiltSpeed.JoinNumber]);
			ZoomSpeedFeedback.LinkInputSig(trilist.UShortInput[joinMap.ZoomSpeed.JoinNumber]);
			FocusSpeedFeedback.LinkInputSig(trilist.UShortInput[joinMap.FocusSpeed.JoinNumber]);

			// privacy
			trilist.SetSigTrueAction(joinMap.PrivacyOn.JoinNumber, PrivacyOn);
			trilist.SetSigTrueAction(joinMap.PrivacyOff.JoinNumber, PrivacyOff);

			// preset - analog recall & save by number
			trilist.SetUShortSigAction(joinMap.PresetSelectByNumber.JoinNumber, value =>
			{
				PresetSelect(value);
				this.LogDebug("LinkToApi PresetSelectByNumber[{0}] => RecallPreset({1})", joinMap.PresetSelectByNumber.JoinNumber, value);
			});
			trilist.SetUShortSigAction(joinMap.PresetStoreByNumber.JoinNumber, value =>
			{
				PresetStore(value, "");
				this.LogDebug("LinkToApi PresetStoreByNumber[{0}] => SavePreset({1})", joinMap.PresetStoreByNumber.JoinNumber, value);
			});
			trilist.SetUShortSigAction(joinMap.PresetRawSelect.JoinNumber, value =>
			{
				PresetRecallRaw(value);
				this.LogDebug("LinkToApi PresetRawSelect[{0}] => PresetRecallRaw({1})", joinMap.PresetRawSelect.JoinNumber, value);
			});

			// presets
			NumberOfPresetsFeedback.LinkInputSig(trilist.UShortInput[joinMap.NumberOfPresets.JoinNumber]);
			PresetStoredFeedback.LinkInputSig(trilist.BooleanInput[joinMap.PresetStoredFeedback.JoinNumber]);
			foreach (var preset in PresetNamesFeedbacks)
			{
				var presetNumber = preset.Key;
				var nameJoin = joinMap.PresetNames.JoinNumber + presetNumber - 1;
				this.LogDebug("Linking: join-{0}, Preset-{1} Name-{2}", nameJoin, preset.Key, preset.Value);
				preset.Value.LinkInputSig(trilist.StringInput[nameJoin]);
				preset.Value.FireUpdate();

				var selectJoin = joinMap.PresetSelect.JoinNumber + presetNumber - 1;
				var storeJoin = joinMap.PresetStore.JoinNumber + presetNumber - 1;

				trilist.SetSigHeldAction(selectJoin, PresetStoreHoldTimeMs,
					() => PresetStore((int)presetNumber, ""),
					() => PresetSelect((int)presetNumber));
				trilist.SetSigTrueAction(storeJoin, () => PresetStore((int)presetNumber, ""));
			}

			// custom commands
			trilist.SetStringSigAction(joinMap.DeviceComs.JoinNumber, SendCustomCommand);

			// online status 
			trilist.OnlineStatusChange += (o, a) =>
			{
				if (!a.DeviceOnLine) return;
				trilist.SetString(joinMap.DeviceName.JoinNumber, Name);
				UpdateFeedbacks();
			};
		}

		private void UpdateFeedbacks()
		{
			OnlineFeedback.FireUpdate();
			if (SocketStatusFeedback != null)
				SocketStatusFeedback.FireUpdate();
			MonitorStatusFeedback.FireUpdate();

			CameraIsOffFeedback.FireUpdate();
			PanSpeedFeedback.FireUpdate();
			TiltSpeedFeedback.FireUpdate();
			ZoomSpeedFeedback.FireUpdate();
			NumberOfPresetsFeedback.FireUpdate();

			foreach (var item in PresetNamesFeedbacks)
				item.Value.FireUpdate();
		}

		#endregion

		private void Socket_ConnectionChange(object sender, GenericSocketStatusChageEventArgs args)
		{
			this.LogDebug(args.Client.ClientStatus.ToString());

			OnlineFeedback.FireUpdate();
			// must null check so LinkToApi doesn't except when the device is TCP or UDP
			if (SocketStatusFeedback != null)
				SocketStatusFeedback.FireUpdate();

			if (args.Client.IsConnected) InitializeCamera();
		}


		/// <summary>
		/// Send bytes to device
		/// </summary>
		/// <param name="bytes"></param>
		public void SendBytes(byte[] bytes)
		{
			if (bytes == null) return;

			if (_commsIsSerial)
			{
				_comms.SendBytes(bytes);
				return;
			}

			if (!_comms.IsConnected)
				_comms.Connect();

			if (!_useHeader)
			{
				_comms.SendBytes(bytes);
				return;
			}

			lock (_counterLock)
			{
				_lastCommand = bytes;
				_comms.SendBytes(BuildMessage(GetPayloadType(bytes), NextSequenceNumber(), bytes));
			}
		}

		/// <summary>
		/// The payload type for a VISCA message. An inquiry - 0x09 in its second byte - has one of
		/// its own, and a camera that checks the type answers nothing when an inquiry arrives marked
		/// as a command.
		/// </summary>
		private static byte[] GetPayloadType(byte[] payload)
		{
			return payload.Length > 1 && payload[1] == 0x09 ? PayloadTypeInquiry : PayloadTypeCommand;
		}

		/// <summary>
		/// Wraps a payload in the VISCA-over-IP header.
		/// </summary>
		private static byte[] BuildMessage(byte[] payloadType, uint sequence, byte[] payload)
		{
			var message = new byte[HeaderLength + payload.Length];

			message[0] = payloadType[0];
			message[1] = payloadType[1];
			message[2] = (byte)(payload.Length >> 8);
			message[3] = (byte)payload.Length;
			message[4] = (byte)(sequence >> 24);
			message[5] = (byte)(sequence >> 16);
			message[6] = (byte)(sequence >> 8);
			message[7] = (byte)sequence;

			payload.CopyTo(message, HeaderLength);

			return message;
		}

		private uint NextSequenceNumber()
		{
			lock (_counterLock)
			{
				_counter = _counter == uint.MaxValue ? 1 : _counter + 1;
				return _counter;
			}
		}

		/// <summary>
		/// Tells the camera to count sequence numbers from zero again. Without this, a camera that
		/// tracks them rejects everything sent after a program restart: the processor starts counting
		/// from zero while the camera carries on from where the last program left off, and only a
		/// power cycle clears it.
		/// </summary>
		public void ResetSequenceNumber()
		{
			if (!_useHeader) return;

			this.LogDebug("Resetting the VISCA over IP sequence number");

			lock (_counterLock)
			{
				_counter = 0;
				_comms.SendBytes(BuildMessage(PayloadTypeControl, 0, new byte[] { 0x01 }));
			}
		}

		public void SendCustomCommand(string cmd)
		{
			throw new NotImplementedException("Not implemented");
		}

		public static string ByteArrayToHexString(byte[] byteArray)
		{
			return BitConverter.ToString(byteArray).Replace("-", "");
		}

		public static bool ContainsSequence(byte[] byteArray, byte[] sequence)
		{
			return Enumerable.Range(0, byteArray.Length - sequence.Length + 1)
				.Any(i => sequence.SequenceEqual(byteArray.Skip(i).Take(sequence.Length)));
		}

		private void Handle_BytesRecieved(object sender, GenericCommMethodReceiveTextArgs args)
		{
			ParseViscaPayload(System.Text.Encoding.GetEncoding(28591).GetBytes(args.Text));
		}

		/// <summary>
		/// Handles one complete VISCA-over-IP datagram: strips the header and hands the payload to
		/// the VISCA parser, or to the control handler for the camera's own messages.
		/// </summary>
		private void Handle_MessageReceived(object sender, GenericCommMethodReceiveBytesArgs args)
		{
			var message = args.Bytes;

			if (message == null || message.Length < HeaderLength)
			{
				this.LogVerbose("Ignoring a message too short to hold a header: {message}",
					ComTextHelper.GetEscapedText(message ?? new byte[0]));
				return;
			}

			var payloadType = (message[0] << 8) | message[1];
			var length = (message[2] << 8) | message[3];

			if (length <= 0 || HeaderLength + length > message.Length)
			{
				this.LogWarning("Message claims a payload of {length} bytes but carries {actual}: {message}",
					length, message.Length - HeaderLength, ComTextHelper.GetEscapedText(message));
				return;
			}

			var payload = new byte[length];
			Array.Copy(message, HeaderLength, payload, 0, length);

			if (payloadType == PayloadTypeControlValue || payloadType == PayloadTypeControlReplyValue)
			{
				HandleControlPayload(payload);
				return;
			}

			ParseViscaPayload(payload);
		}

		/// <summary>
		/// Handles the camera's control messages. The one that matters is a rejected sequence number:
		/// everything sent after it is rejected too, so reset the count and send the command again.
		/// </summary>
		private void HandleControlPayload(byte[] payload)
		{
			if (payload.Length < 2 || payload[0] != 0x0F || payload[1] != 0x01)
			{
				this.LogVerbose("Control message: {payload}", ComTextHelper.GetEscapedText(payload));
				return;
			}

			_sequenceErrors++;

			if (_sequenceErrors > SequenceErrorsMax)
			{
				this.LogWarning("Camera rejected the sequence number {count} times in a row, not sending it again", _sequenceErrors);
				return;
			}

			this.LogWarning("Camera rejected the sequence number, resetting it and sending the command again");

			ResetSequenceNumber();

			var lastCommand = _lastCommand;
			if (lastCommand != null)
				new CTimer(o => SendBytes(lastCommand), null, SequenceResetSettleMs);
		}

		private void ParseViscaPayload(byte[] byteArray)
		{
			try
			{
				this.LogVerbose("ParseViscaPayload: {byteArray}", ComTextHelper.GetEscapedText(byteArray));

				// an answer of any kind means the camera is taking our messages again
				_sequenceErrors = 0;

				if (byteArray.Length < 3)
				{
					this.LogVerbose("byteArray.Length < 3, power status is held in byteArray[2]");
					return;
				}

				// Before the power reply: both start y0 50, and only the length tells them apart.
				if (ViscaVersionReply.TryParse(byteArray, out var vendor, out var model, out var rom))
				{
					var firmware = ViscaVersionReply.Describe(vendor, model, rom);
					if (firmware != DeviceInfo.FirmwareVersion)
					{
						DeviceInfo.FirmwareVersion = firmware;
						this.LogInformation("Camera reports firmware {firmware}", firmware);
						DeviceInfoChanged?.Invoke(this, new DeviceInfoEventArgs(DeviceInfo));
					}
					return;
				}

				if (byteArray[1] == 0x50)
				{
					this.LogVerbose("Handle_BytesRecieved: power status");

					// power on: [90][50][02]
					if (byteArray[2] == 0x02)
					{
						CameraIsOff = false;
						this.LogVerbose("power on");
					}
					// power off: [90][50][03]
					else if (byteArray[2] == 0x03)
					{
						CameraIsOff = true;
						this.LogVerbose("power off");
					}
					// focus auto:		0xy0, 0x50, 0x02, 0xFF	??? same as power on in document
					// focus manual:	0xy0, 0x50, 0x03, 0xFF	??? same as power off in document
				}

			}
			catch (Exception err)
			{
				this.LogVerbose("Error parsing feedback: ", err);
			}
		}

		public void InitializeCamera()
		{
			if (_useHeader)
			{
				// the camera has to forget the sequence number it was counting from before it will
				// take anything from a freshly started program
				ResetSequenceNumber();
				new CTimer(o => SendInitializationCommands(), null, SequenceResetSettleMs);
				return;
			}

			SendInitializationCommands();
		}

		private void SendInitializationCommands()
		{
			// send address set broadcast
			SendBytes(new byte[] { 0x88, 0x30, 0x01, 0xFF });

			// send IF clear on connection
			SendBytes(new byte[] { 0x88, 0x01, 0x00, 0x01, 0xFF });

			// ask what the camera is once it has taken the address and cleared
			new CTimer(o => UpdateDeviceInfo(), null, VersionInquiryDelayMs);
		}

		/// <summary>
		/// Asks the camera for its vendor, model and ROM revision; the reply fills
		/// <see cref="DeviceInfo"/>.
		/// </summary>
		public void UpdateDeviceInfo()
		{
			SendBytes(ViscaVersionReply.Inquiry(_address));
		}

		public void Poll()
		{
			// power inquiry
			SendBytes(new byte[] { _address, 0x09, 0x04, 0x00, 0xFF });
		}

		public void CameraOn()
		{
			SendBytes(new byte[] { _address, 0x01, 0x04, 0x00, 0x02, 0xFF });
			new CTimer(o => Poll(), null, 1000);
		}

		public void CameraOff()
		{
			SendBytes(new byte[] { _address, 0x01, 0x04, 0x00, 0x03, 0xFF });
			new CTimer(o => Poll(), null, 1000);
		}

		/// <summary>
		/// Powers the camera on
		/// </summary>
		public void PowerOn()
		{
			CameraOn();
		}

		/// <summary>
		/// Powers the camera off
		/// </summary>
		public void PowerOff()
		{
			CameraOff();
		}

		/// <summary>
		/// Toggles the camera power state
		/// </summary>
		public void PowerToggle()
		{
			if (CameraIsOff)
				CameraOn();
			else
				CameraOff();
		}

		public void PanLeft()
		{
			SendBytes(new byte[] { _address, 0x01, 0x06, 0x01, Convert.ToByte(PanSpeed), Convert.ToByte(TiltSpeed), 0x01, 0x03, 0xFF });
		}

		public void PanRight()
		{
			SendBytes(new byte[] { _address, 0x01, 0x06, 0x01, Convert.ToByte(PanSpeed), Convert.ToByte(TiltSpeed), 0x02, 0x03, 0xFF });
		}

		public void PanStop()
		{
			SendBytes(new byte[] { _address, 0x01, 0x06, 0x01, Convert.ToByte(PanSpeed), Convert.ToByte(TiltSpeed), 0x03, 0x03, 0xFF });
		}

		public void TiltDown()
		{
			SendBytes(new byte[] { _address, 0x01, 0x06, 0x01, Convert.ToByte(PanSpeed), Convert.ToByte(TiltSpeed), 0x03, 0x02, 0xFF });
		}

		public void TiltUp()
		{
			SendBytes(new byte[] { _address, 0x01, 0x06, 0x01, Convert.ToByte(PanSpeed), Convert.ToByte(TiltSpeed), 0x03, 0x01, 0xFF });
		}

		public void TiltStop()
		{
			SendBytes(new byte[] { _address, 0x01, 0x06, 0x01, Convert.ToByte(PanSpeed), Convert.ToByte(TiltSpeed), 0x03, 0x03, 0xFF });
		}

		public void ZoomIn()
		{
			SendBytes(new byte[] { _address, 0x01, 0x04, 0x07, Convert.ToByte(0x20 + ZoomSpeed), 0xFF });
		}

		public void ZoomOut()
		{
			SendBytes(new byte[] { _address, 0x01, 0x04, 0x07, Convert.ToByte(0x30 + ZoomSpeed), 0xFF });
		}

		public void ZoomStop()
		{
			SendBytes(new byte[] { _address, 0x01, 0x04, 0x07, 0x00, 0xFF });
		}

		public void FocusNear()
		{
			SendBytes(new byte[] { _address, 0x01, 0x04, 0x08, Convert.ToByte(0x30 + FocusSpeed), 0xFF });
		}

		public void FocusFar()
		{
			SendBytes(new byte[] { _address, 0x01, 0x04, 0x08, Convert.ToByte(0x20 + FocusSpeed), 0xFF });
		}

		public void FocusStop()
		{
			SendBytes(new byte[] { _address, 0x01, 0x04, 0x08, 0x02, 0xFF });
		}

		public void TriggerAutoFocus()
		{
			var cmd = AutoFocus // ? off : on
				? new byte[] { _address, 0x01, 0x04, 0x38, 0x03, 0xFF }
				: new byte[] { _address, 0x01, 0x04, 0x38, 0x02, 0xFF };
			SendBytes(cmd);
		}

		public void PositionHome()
		{
			var cmd = new byte[] { _address, 0x01, 0x06, 0x04, 0xFF };
			SendBytes(cmd);
		}

		public void PresetSelect(int preset)
		{
			ViscaCameraPresetsConfig p;
			if (_presetsByIndex.TryGetValue((uint)preset, out p))
			{
				SendBytes(new byte[] { _address, 0x01, 0x04, 0x3F, 0x02, Convert.ToByte(p.Id), 0xFF });
			}
		}

		public void PresetRecallRaw(int preset)
		{
			// Guard against values > 255 to avoid OverflowException in Convert.ToByte
			if (preset > byte.MaxValue)
			{
				this.LogWarning("PresetRecallRaw received out-of-range value {0}", preset);
				return;
			}
			else if (preset < 0)
			{
				this.LogWarning("PresetRecallRaw received negative value {0}", preset);
				return;
			}

			SendBytes(new byte[] { _address, 0x01, 0x04, 0x3F, 0x02, Convert.ToByte(preset), 0xFF });
		}

		/// <summary>
		/// Recalls a camera preset using a raw VISCA preset identifier
		/// </summary>
		/// <param name="preset"></param>
		/// <param name="description"></param>
		public void PresetStore(int preset, string description)
		{
			ViscaCameraPresetsConfig p;
			if (_presetsByIndex.TryGetValue((uint)preset, out p))
			{
				SendBytes(new byte[] { _address, 0x01, 0x04, 0x3F, 0x01, Convert.ToByte(p.Id), 0xFF });

				PresetStored = true;
				CrestronEnvironment.Sleep(500);
				PresetStored = false;
			}
		}

		public void PrivacyOn()
		{
			if (_privacyOnPreset == 0) return;
			PresetSelect((int)_privacyOnPreset);
		}

		public void PrivacyOff()
		{
			if (_privacyOffPreset == 0) return;
			PresetSelect((int)_privacyOffPreset);
		}
	}
}

