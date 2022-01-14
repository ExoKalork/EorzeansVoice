﻿using EorzeansVoiceLib;
using EorzeansVoiceLib.Enums;
using EorzeansVoiceLib.NetworkMessageContent;
using EorzeansVoiceLib.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Timers;

namespace EorzeansVoiceServer {
	public class Program {
		private static readonly UdpClient udpClient = new UdpClient(NetworkConsts.port);
		private static readonly List<Client> clients = new List<Client>();
		private static readonly Timer TIM_CheckOffline = new Timer();

		public static void Main() {
			TIM_CheckOffline.Interval = 1000;
			TIM_CheckOffline.Elapsed += TIM_CheckOffline_Elapsed;
			TIM_CheckOffline.Enabled = true;

			Logging.console = Logging.LogType.Debug; // Replace with arg
			Logging.file = Logging.LogType.Info; // Replace with arg
			Logging.fileName = "Log"; // Replace with arg

			udpClient.BeginReceive(new AsyncCallback(ReceiveData), null);

			Logging.Info("##### Eorzean's Voice " + NetworkConsts.serverVersion + " #####\n");
			Logging.Info("Listening...");

			while (true) {
				// Handle line commands
				Console.ReadLine();
			}
		}

		private static void ReceiveData(IAsyncResult result) {
			IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, NetworkConsts.port);
			NetworkMessage msg = null;

			try {
				byte[] received = udpClient.EndReceive(result, ref remoteEP);
				msg = received.ToMessage();
			} catch (Exception e) {
				Logging.Error("Error receiving message : " + e.Message);
				Logging.Error(e.StackTrace);
				return;
			} finally {
				udpClient.BeginReceive(new AsyncCallback(ReceiveData), null);
			}

			NetworkMessage reply = Process(remoteEP, msg);
			if (reply != null) {
				byte[] data = reply.ToBytes();
				udpClient.SendAsync(data, data.Length, remoteEP);
			}
		}

		private static NetworkMessage Process(IPEndPoint remoteEP, NetworkMessage msg) {
			NetworkMessage reply = null;

			switch (msg.type) {
				case NetworkMessageType.Ping:
					reply = Ping(remoteEP);
					break;
				case NetworkMessageType.VersionCheck:
					reply = VersionCheck(remoteEP, msg);
					break;
				case NetworkMessageType.Connect:
					reply = Connect(remoteEP, msg);
					break;
				case NetworkMessageType.UpdateServer:
					reply = UpdateServer(remoteEP, msg);
					break;
				case NetworkMessageType.SendVoiceToServer:
					ReceiveVoice(remoteEP, msg);
					break;
				case NetworkMessageType.KeepAlive:
					KeepAlive(remoteEP, msg);
					break;
				case NetworkMessageType.Disconnect:
					UserDisconnecting(msg);
					break;
			}

			return reply;
		}

		private static NetworkMessage Ping(IPEndPoint remoteEP) {
			Logging.Debug("Received ping from " + remoteEP.ToString() + ", answering pong.");
			return new NetworkMessage(NetworkMessageType.Pong, "Pong");
		}

		private static NetworkMessage VersionCheck(IPEndPoint remoteEP, NetworkMessage received) {
			VersionCheck vCheck = received.content.ToObject<VersionCheck>();
			NetworkMessage reply = new NetworkMessage(NetworkMessageType.VersionCheckResult, VersionCheckAnswer.UpToDate);
			string verboseResult = "Up to date.";
			if (vCheck.clientVersion < NetworkConsts.clientVersion) {
				reply.content = VersionCheckAnswer.ClientOutOfDate;
				verboseResult = "Remote client is outdated !";
			} else if (vCheck.serverVersion < NetworkConsts.serverVersion) {
				reply.content = VersionCheckAnswer.ServerOutOfDate;
				verboseResult = "This server is outdated !";

				Logging.Warn("##### Please update this server ! New version : " + vCheck.serverVersion.ToString() + " #####");
			}

			Logging.Debug(remoteEP.ToString() + " is checking version. " + verboseResult);
			return reply;
		}

		private static NetworkMessage Connect(IPEndPoint remoteEP, NetworkMessage received) {
			Connect connect = received.content.ToObject<Connect>();

			Client newClient = new Client {
				id = GetNewID(),
				ipAddress = remoteEP.Address.ToString(),
				port = remoteEP.Port,
				worldID = connect.worldID,
				name = connect.name,
				mapID = connect.mapID,
				instanceID = connect.instanceID,
				position = connect.position,
				lastReceived = DateTime.Now
			};

			Logging.Info(newClient.ToStringDetailed() + " is now connected.");
			clients.Add(newClient);

			// TODO : Send update to all clients around new user

			return new NetworkMessage(NetworkMessageType.Connected, newClient.id);
		}

		private static NetworkMessage UpdateServer(IPEndPoint remoteEP, NetworkMessage received) {
			UpdateServer newInfo = received.content.ToObject<UpdateServer>();
			Client client = clients.FirstOrDefault(x => x.id == newInfo.id);

			if (client == null) {
				Logging.Warn("Received update from non-existant client.");
				ForceDisconnect(remoteEP, "UpdateServer");
				return null;
			}

			client.worldID = newInfo.worldID;
			client.mapID = newInfo.mapID;
			client.instanceID = newInfo.instanceID;
			client.position = newInfo.position;
			client.lastReceived = DateTime.Now;

			List<ClientInfo> infoOfAround = ClientInfo.FromClients(client.GetAround(clients));

			string verboseSuffix = ", not replying because there is no one around them.";
			if (infoOfAround.Count > 0) {
				verboseSuffix = ", replying with info of " + infoOfAround.Count + " users around them.";
			}
			Logging.Debug("Received update from " + client.ToString() + verboseSuffix);

			if (infoOfAround.Count > 0) {
				return new NetworkMessage(NetworkMessageType.UpdateClient, infoOfAround);
			}
			return null;
		}

		private static void ReceiveVoice(IPEndPoint remoteEP, NetworkMessage received) {
			SendVoice msg = received.content.ToObject<SendVoice>();
			Client client = clients.FirstOrDefault(x => x.id == msg.id);

			if (client == null) {
				Logging.Warn("Received voice from non-existant client.");
				ForceDisconnect(remoteEP, "ReceiveVoice");
				return;
			}

			client.lastReceived = DateTime.Now;

			SendVoice voice = new SendVoice() {
				id = msg.id,
				data = msg.data
			};

			NetworkMessage toOtherUsers = new NetworkMessage(NetworkMessageType.SendVoiceToClient, voice);
			byte[] toSend = toOtherUsers.ToBytes();

			foreach (Client c in clients) {
				if (c.id == msg.id) {
					continue;
				}

				// TODO : Check if remoteEP is valid, otherwise it prints error
				IPEndPoint remoteEPcurrent = new IPEndPoint(IPAddress.Parse(c.ipAddress), c.port);
				udpClient.SendAsync(toSend, toSend.Length, remoteEPcurrent);
			}
		}

		private static void KeepAlive(IPEndPoint remoteEP, NetworkMessage received) {
			int userID = (int)received.content;
			Client client = clients.FirstOrDefault(x => x.id == userID);

			if (client == null) {
				Logging.Warn("Received Keep Alive from non-existant client.");
				ForceDisconnect(remoteEP, "KeepAlive");
				return;
			}

			Logging.Debug("Received Keep Alive from " + client.ToString());
			client.lastReceived = DateTime.Now;

			// TODO : Send update
		}

		private static void UserDisconnecting(NetworkMessage received) {
			int userID = (int)received.content;
			Client client = clients.FirstOrDefault(x => x.id == userID);

			if (client == null) {
				Logging.Warn("Received Disconnect from non-existant client.");
				return;
			}

			Logging.Info(client.ToString() + " disconnecting.");
			clients.Remove(client);
		}

		private static void ForceDisconnect(IPEndPoint remoteEP, string error) {
			byte[] data = new NetworkMessage(NetworkMessageType.ForceDisconnect, error).ToBytes();
			udpClient.SendAsync(data, data.Length, remoteEP);
		}

		private static void TIM_CheckOffline_Elapsed(object sender, ElapsedEventArgs e) {
			DateTime now = DateTime.Now;

			foreach (Client c in clients.ToList()) {
				if (c.lastReceived < now - TimeSpan.FromSeconds(30)) {
					Logging.Info(c.ToString() + " stopped updating, disconnecting.");

					IPEndPoint remoteEP = new IPEndPoint(IPAddress.Parse(c.ipAddress), c.port);
					ForceDisconnect(remoteEP, "CheckOffline");

					clients.Remove(c);
				}
			}
		}

		private static int GetNewID() {
			Random rnd = new Random();
			int final = 0;
			do {
				final = rnd.Next(1, 1000000);
			} while (clients.Exists(x => x.id == final));

			return final;
		}
	}
}
