﻿using EorzeansVoice.Utils;
using EorzeansVoiceLib;
using EorzeansVoiceLib.Enums;
using EorzeansVoiceLib.NetworkMessageContent;
using EorzeansVoiceLib.Utils;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Timers.Timer;

namespace EorzeansVoice {
	public static class LogicController {
		public class ClientAround {
			public int id;
			public string name;
			public Vector3 position;
			public BufferedWaveProvider waveProvider;
			public WaveChannel32 channel;
			public USC_ClientAround controls;
			public bool remove;
		}

		private static readonly Timer TIM_Process = new Timer();
		private static readonly Timer TIM_LoginWait = new Timer();
		private static readonly Timer TIM_KeepAlive = new Timer();
		private static readonly Timer TIM_SendInfo = new Timer();

		public static int userID;
		public static List<ClientAround> around = new List<ClientAround>();

		private static Process gameProcess;
		private static bool processUpdateName = false;
		private static UpdateServer infoCache;
		private static DateTime lastSent;

		public static void Load(ComboBox inputs, ComboBox outputs, float voiceActivationThreshold) {
			Logging.AddLogger(Logging.LogType.File, Logging.LogLevel.Debug, "Log"); // Replace with Settings
			Logging.Info("##### Eorzeans' Voice " + NetworkConsts.clientVersion + " #####\n");
			Logging.Info("Loading...");

			Logging.Debug("Checking admin permissions...");
			AppDomain domain = Thread.GetDomain();
			domain.SetPrincipalPolicy(PrincipalPolicy.WindowsPrincipal);
			WindowsPrincipal p = (WindowsPrincipal)Thread.CurrentPrincipal;
			if (!p.IsInRole(WindowsBuiltInRole.Administrator)) {
				MessageBox.Show("You need to run this app as an administrator for full functionality.");
				Logging.Warn("Missing admin permissions, closing.");
				Application.Exit();
				return;
			}

			Logging.Debug("Checking networking...");
			if (!Network.IsNetworkWorking()) {
				MessageBox.Show("Couldn't establish a connection with the internet. Please check your internet connection.");
				Logging.Warn("Network not working, closing.");
				Application.Exit();
				return;
			}

			Logging.Debug("Checking server...");
			if (!Network.IsServerWorking()) {
				MessageBox.Show("Couldn't establish a connection with the server.");
				Logging.Warn("Server not working, closing.");
				Application.Exit();
				return;
			}

			Logging.Debug("Loading Audio...");
			InitAudio(inputs, outputs, voiceActivationThreshold);

			Logging.Debug("Initializing timers...");
			InitTimers();

			Logging.Info("Loading complete !");
		}

		private static void InitAudio(ComboBox inputs, ComboBox outputs, float voiceActivationThreshold) {
			AudioController.LoadAudioDevices(inputs, outputs);
			AudioController.Device input = (AudioController.Device)inputs.SelectedItem;
			AudioController.Device output = (AudioController.Device)outputs.SelectedItem;
			AudioController.Init(input, output);
			AudioInputProcessing.Init(voiceActivationThreshold);
		}

		private static void InitTimers() {
			TIM_Process.Elapsed += TIM_Process_Elapsed;

			TIM_LoginWait.Interval = 100;
			TIM_LoginWait.Elapsed += TIM_LoginWait_Elapsed;

			TIM_KeepAlive.Interval = 1000;
			TIM_KeepAlive.Elapsed += TIM_KeepAlive_Elapsed;

			TIM_SendInfo.Interval = 200;
			TIM_SendInfo.Elapsed += TIM_SendInfo_Elapsed;
		}

		public static void MainShown() {
			CheckVersion();
			if (FindProcess()) {
				LogInAndConnect();
			}
		}

		private static void CheckVersion() {
			Logging.Info("Checking version...");
			VersionCheckAnswer versionCheck = Network.IsUpToDate();

			if (versionCheck == VersionCheckAnswer.ClientOutOfDate) {
				// Start Auto Update here
				Main.instance.UpdateStatus("Update available !");
				MessageBox.Show("An update is available.");
				Logging.Warn("Client update available, closing.");
				Application.Exit();
				return;
			} else if (versionCheck == VersionCheckAnswer.ServerOutOfDate) {
				Main.instance.UpdateStatus("Server out of date.");
				MessageBox.Show("The server is out of date. Please wait a moment and try again.");
				Logging.Warn("Server out of date, closing.");
				Application.Exit();
				return;
			}

			Main.instance.UpdateStatus("Up to date.");
			Logging.Info("Up to date.");
		}

		public static bool FindProcess() {
			Logging.Info("Looking for the game's process...");
			Main.instance.UpdateStatus("Looking for the game's process...");

			ProcessFinder.Result result = ProcessFinder.GetProcess();
			if (result.type == ProcessFinder.ResultType.NoneAvailable) {
				Logging.Info("No FFXIV process available, checking again in 3s.");
				Main.instance.UpdateProcess("Please open FFXIV.");
				Main.instance.UpdateStatus("Please open FFXIV.");

				TIM_Process.Interval = 3000; // 3s
				TIM_Process.Enabled = true;

				return false;
			} else if (result.type == ProcessFinder.ResultType.ClosedSelector) {
				Logging.Info("User closed the process selector, waiting on user input.");
				Main.instance.ToggleProcessButton(true);

				return false;
			}

			gameProcess = result.process.process;
			Main.instance.UpdateProcess(result.process.ToString());
			Main.instance.ToggleProcessButton(false);
			processUpdateName = true;
			TIM_Process.Interval = 15000; // 15s
			TIM_Process.Enabled = true;
			Logging.Info("Process found.");
			return true;
		}

		private static void TIM_Process_Elapsed(object sender, System.Timers.ElapsedEventArgs e) {
			if (processUpdateName) {
				Main.instance.UpdateProcess(ProcessFinder.GetProcessInformation(gameProcess).ToString());
			} else if (FindProcess()) {
				LogInAndConnect();
			}
		}

		private static void LogInAndConnect() {
			if (GameData.IsLoggedIn(gameProcess)) {
				Logging.Info("User is logged in, connecting...");
				TIM_LoginWait.Enabled = false;
				Main.instance.UpdateStatus("Connecting to server...");

				short worldID = GameData.GetCurrentWorldID(gameProcess);
				string name = GameData.GetName(gameProcess);
				int mapID = GameData.GetMapID(gameProcess);
				int instanceID = GameData.GetInstanceID(gameProcess);
				Vector3 position = GameData.GetPosition(gameProcess);
				userID = Network.ConnectToVoiceChat(worldID, name, mapID, instanceID, position);

				if (userID == 0) {
					MessageBox.Show("Couldn't connect to voice chat server. Please restart Eorzeans' Voice.");
					Logging.Error("An error occurred while connecting to server, closing.");
					Application.Exit();
					return;
				}
				Logging.Info("User is now connected : " + name + " (" + userID + ")");

				AudioController.StartAudio();
				Network.StartReceivingData();

				lastSent = DateTime.Now;
				Main.instance.UpdateStatus("Connected !");
				Logging.Info("Audio started and now receiving data form server.");
				TIM_KeepAlive.Enabled = true;
				TIM_SendInfo.Enabled = true;
			} else {
				Logging.Info("User is not logged into a character, checking again in 3s.");
				Main.instance.UpdateStatus("Please log into a character.");
				TIM_LoginWait.Interval = 3000; // 3s
				TIM_LoginWait.Enabled = true;
			}
		}

		private static void TIM_LoginWait_Elapsed(object sender, System.Timers.ElapsedEventArgs e) {
			LogInAndConnect();
		}

		private static void TIM_KeepAlive_Elapsed(object sender, System.Timers.ElapsedEventArgs e) {
			if (DateTime.Now - lastSent >= TimeSpan.FromSeconds(5)) {
				lastSent = DateTime.Now;
				Logging.Debug("Hasn't sent anything in 5s or more, sending Keep Alive.");
				Network.SendKeepAlive(userID);
			}
		}

		private static void TIM_SendInfo_Elapsed(object sender, System.Timers.ElapsedEventArgs e) {
			UpdateServer newInfo = new UpdateServer {
				id = userID,
				position = GameData.GetPosition(gameProcess),
				worldID = GameData.GetCurrentWorldID(gameProcess),
				mapID = GameData.GetMapID(gameProcess),
				instanceID = GameData.GetInstanceID(gameProcess)
			};

			if (infoCache != newInfo) {
				Logging.Debug("Info changed, sending to server.");
				infoCache = newInfo;
				lastSent = DateTime.Now;
				Network.SendInfoToServer(newInfo);
			}
		}

		public static void UpdateAround(List<ClientInfo> info) {
			foreach (ClientInfo i in info) {
				ClientAround c = around.FirstOrDefault(x => x.id == i.id);

				if (c == null) {
					Tuple<BufferedWaveProvider, WaveChannel32> audio = AudioController.AddNewProvider();

					ClientAround newAround = new ClientAround {
						id = i.id,
						name = i.name,
						position = i.position,
						waveProvider = audio.Item1,
						channel = audio.Item2
					};

					around.Add(newAround);
					Logging.Debug("Adding user to around list : " + i.name + " (" + i.id + ")");
				} else {
					c.position = i.position;
				}
			}

			foreach (ClientAround c in around) {
				ClientInfo i = info.FirstOrDefault(x => x.id == c.id);

				if (i == null) {
					c.remove = true;
					Logging.Debug("Removing user from around list : " + c.name + " (" + c.id + ")");
				}
			}
		}

		public static void Closing() {
			if (userID != 0) {
				Logging.Info("Disconnect before closing.");
				Network.Disconnect(userID);
			}
		}
	}
}
