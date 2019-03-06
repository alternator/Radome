using System.Collections;
using System.Collections.Generic;
using System.Net;
using Unity.Collections;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.LowLevel.Unsafe;
using UnityEngine;
using UdpCNetworkDriver = Unity.Networking.Transport.BasicNetworkDriver<Unity.Networking.Transport.IPv4UDPSocket>;

namespace ICKX.Radome {

	public class UDPClientNetworkManager<PlayerInfo> : ClientNetworkManager<UdpCNetworkDriver, PlayerInfo> where PlayerInfo : DefaultPlayerInfo, new() {

		public UDPClientNetworkManager (PlayerInfo playerInfo) : base (playerInfo) {
		}

		private IPAddress serverAdress;
		private int serverPort;

		/// <summary>
		/// クライアント接続開始
		/// </summary>
		public void Start (IPAddress adress, int port) {
			serverAdress = adress;
			serverPort = port;

			if (!driver.IsCreated) {
				var parm = new NetworkConfigParameter () {
					connectTimeoutMS = 1000 * 5,
					disconnectTimeoutMS = 1000 * 5,
				};
				driver = new UdpCNetworkDriver (new INetworkParameter[] { parm });
			}

			var endpoint = new IPEndPoint (serverAdress, port);
			networkLinker = new NetworkLinker<UdpCNetworkDriver> (driver, driver.Connect (endpoint), NetworkParameterConstants.MTU);

			Start ();
		}

		protected override void Reconnect () {
			var endpoint = new IPEndPoint (serverAdress, serverPort);
			state = State.Connecting;

			networkLinker.Reconnect (driver.Connect (endpoint));
			Debug.Log ("Reconnect");
		}
	}

	public abstract class ClientNetworkManager<Driver, PlayerInfo> : NetworkManagerBase
			where Driver : struct, INetworkDriver where PlayerInfo : DefaultPlayerInfo, new() {

		public override bool isFullMesh => false;

		public Driver driver;
		public NetworkLinker<Driver> networkLinker;
		public List<PlayerInfo> activePlayerInfoList = new List<PlayerInfo> (16);

		public PlayerInfo MyPlayerInfo { get; protected set; }

		public ClientNetworkManager (PlayerInfo playerInfo) : base () {
			MyPlayerInfo = playerInfo;
		}

		public override void Dispose () {
			if (state != State.Offline) {
				StopComplete ();
			}
			driver.Dispose ();
			base.Dispose ();
		}

		protected void Start () {
			state = State.Connecting;
		}

		//再接続
		protected abstract void Reconnect ();

		/// <summary>
		/// クライアント接続停止
		/// </summary>
		public override void Stop () {
			if (!jobHandle.IsCompleted) {
				Debug.LogError ("NetworkJob実行中に停止できない");
				return;
			}
			state = State.Disconnecting;

			//Playerリストから削除するリクエストを送る
			using (var unregisterPlayerPacket = new DataStreamWriter (3, Allocator.Temp)) {
				unregisterPlayerPacket.Write ((byte)BuiltInPacket.Type.UnregisterPlayer);
				unregisterPlayerPacket.Write (playerId);
				Send (ServerPlayerId, unregisterPlayerPacket, QosType.Reliable);
			}
			Debug.Log ("Stop");
		}

		// サーバーから切断されたらLinkerを破棄して停止
		public override void StopComplete () {
			UnregisterPlayerId (playerId);

			playerId = 0;
			state = State.Offline;
			jobHandle.Complete ();
			driver.Disconnect (networkLinker.connection);   //ここはserverからDisconnectされたら行う処理
			networkLinker.Dispose ();
			networkLinker = null;

			Debug.Log ("StopComplete");
		}

		//新しいPlayerを登録する処理
		protected override void RegisterPlayerId (ushort id) {
			base.RegisterPlayerId (id);

			//ConnectionInfo更新
			var connectionInfo = new ConnectionInfo (State.Online);

			while (id >= activeConnectionInfoList.Count) {
				activeConnectionInfoList.Add (default);
			}
			activeConnectionInfoList[id] = connectionInfo;

			//playerInfo領域確保
			while (id >= activePlayerInfoList.Count) {
				activePlayerInfoList.Add (null);
			}

			if (id == playerId) {
				//自分のユーザー情報をリストに登録
				activePlayerInfoList[playerId] = MyPlayerInfo;

				//自分のPlayerInfoをサーバーに通知
				using (var updatePlayerPacket = MyPlayerInfo.CreateUpdatePlayerInfoPacket (playerId)) {
					Send (ServerPlayerId, updatePlayerPacket, QosType.Reliable);
				}
			}

			//イベント通知
			ExecOnRegisterPlayer (id);
		}

		//Playerを登録解除する処理
		protected override void UnregisterPlayerId (ushort id) {
			base.UnregisterPlayerId (id);

			if (id < activeConnectionInfoList.Count) {
				activeConnectionInfoList[id] = default;
				activePlayerInfoList[id] = null;
				ExecOnUnregisterPlayer (id);
			}
		}

		//Playerを再接続させる処理
		protected virtual void ReconnectPlayerId (ushort id) {

			if (id < activeConnectionInfoList.Count) {
				var info = activeConnectionInfoList[id];
				if (info.isCreated) {
					info.state = State.Online;
					activeConnectionInfoList[id] = info;
					ExecOnReconnectPlayer (id);
				} else {
					Debug.LogError ($"ReconnectPlayerId Error. ID={id}は未登録");
				}
			} else {
				Debug.LogError ($"ReconnectPlayerId Error. ID={id}は未登録");
			}
		}

		//Playerを一旦切断状態にする処理
		protected virtual void DisconnectPlayerId (ushort id) {

			if (id < activeConnectionInfoList.Count) {
				var info = activeConnectionInfoList[id];
				if (info.isCreated) {
					info.state = State.Offline;
					info.disconnectTime = Time.realtimeSinceStartup;
					activeConnectionInfoList[id] = info;
					ExecOnDisconnectPlayer (id);
				} else {
					Debug.LogError ($"DisconnectPlayerId Error. ID={id}は未登録");
				}
			} else {
				Debug.LogError ($"DisconnectPlayerId Error. ID={id}は未登録");
			}
		}

		/// <summary>
		/// Player1人にパケットを送信
		/// </summary>
		public override ushort Send (ushort targetPlayerId, DataStreamWriter data, QosType qos) {
			if (state == State.Offline) {
				Debug.LogError ("Send Failed : State.Offline");
				return 0;
			}
			return networkLinker.Send (data, qos, targetPlayerId, playerId, false);
		}

		public override void Multicast (NativeList<ushort> playerIdList, DataStreamWriter data, QosType qos) {
			if (state == State.Offline) {
				Debug.LogError ("Send Failed : State.Offline");
				return;
			}

			ushort dataLength = (ushort)data.Length;
			using (var writer = new DataStreamWriter (dataLength + 2 + 2 * playerIdList.Length, Allocator.Temp)) {
				unsafe {
					byte* dataPtr = DataStreamUnsafeUtility.GetUnsafeReadOnlyPtr (data);
					writer.Write ((ushort)playerIdList.Length);
					for (int i = 0; i < playerIdList.Length; i++) {
						writer.Write (playerIdList[i]);
					}
					writer.WriteBytes (dataPtr, dataLength);
				}

				networkLinker.Send (writer, qos, NetworkLinkerConstants.MulticastId, playerId, false);
			}
		}

		/// <summary>
		/// 全Playerにパケットを送信
		/// </summary>
		public override void Brodcast (DataStreamWriter data, QosType qos, bool noChunk = false) {
			if (state == State.Offline) {
				Debug.LogError ("Send Failed : State.Offline");
				return;
			}
			networkLinker.Send (data, qos, NetworkLinkerConstants.BroadcastId, playerId, noChunk);
		}

		/// <summary>
		/// 受信パケットの受け取りなど、最初に行うべきUpdateループ
		/// </summary>
		public override void OnFirstUpdate () {
			jobHandle.Complete ();
			if (networkLinker == null) {
				return;
			}

			networkLinker.Complete ();

			if (state == State.Offline) {
				return;
			}

			//受け取ったパケットを処理に投げる.
			if (networkLinker.IsConnected) {
				//Debug.Log ("IsConnected : dataLen=" + linker.dataStreams.Length);
			}
			if (networkLinker.IsDisconnected) {
				//Debug.Log ("IsDisconnected");
				if (state == State.Disconnecting) {
					StopComplete ();
				} else {
					DisconnectPlayerId (playerId);
					Reconnect ();
				}
				return;
			}

			for (int j = 0; j < networkLinker.dataStreams.Length; j++) {
				var stream = networkLinker.dataStreams[j];
				var ctx = default (DataStreamReader.Context);
				if (!ReadQosHeader (stream, ref ctx, out var qosType, out var seqNum, out var ackNum, out ushort targetPlayerId, out ushort senderPlayerId)) {
					continue;
				}

				//chunkをバラして解析
				while (true) {
					if (!ReadChunkHeader (stream, ref ctx, out var chunk, out var ctx2)) {
						break;
					}
					byte type = chunk.ReadByte (ref ctx2);
					//Debug.Log ("Linker streamLen=" + stream.Length + ", chunkLen=" + chunk.Length + ",type=" + type + ",target=" + targetPlayerId + ",sender=" + senderPlayerId);
					if (type != (byte)BuiltInPacket.Type.RelayChunkedPacket) {
						DeserializePacket (senderPlayerId, type, ref chunk, ctx2);
					}else {
						while (true) {
							//Debug.Log ("Linker chunkLen=" + chunk.Length + ", ctx2=" + ctx2.GetReadByteIndex() + ",target=" + targetPlayerId + ",sender=" + senderPlayerId);
							if (!ReadChunkHeader (chunk, ref ctx2, out var chunk2, out var ctx3)) {
								Debug.Log(string.Join(" ", chunk.ReadBytesAsArray(ref ctx2, chunk.Length - ctx2.GetReadByteIndex ())));
								break;
							}
							byte type2 = chunk2.ReadByte (ref ctx3);
							DeserializePacket (senderPlayerId, type2, ref chunk2, ctx3);
						}
					}
				}
			}
		}

		protected virtual void DeserializePacket (ushort senderPlayerId, byte type, ref DataStreamReader chunk, DataStreamReader.Context ctx2) {
			//自分宛パケットの解析
			switch (type) {
				case (byte)BuiltInPacket.Type.RegisterPlayer:
					state = State.Online;
					playerId = chunk.ReadUShort (ref ctx2);
					leaderStatTime = chunk.ReadLong (ref ctx2);

					ushort syncSelfSeqNum = chunk.ReadUShort (ref ctx2);
					networkLinker.SyncSeqNum (syncSelfSeqNum);

					byte count = chunk.ReadByte (ref ctx2);
					activePlayerIdList.Clear ();
					for (int i = 0; i < count; i++) {
						activePlayerIdList.Add (chunk.ReadByte (ref ctx2));
					}

					for (int i = 0; i < activePlayerIdList.Count; i++) {
						Debug.Log ("activePlayerIdList : " + i + " : " + activePlayerIdList[i]);
					}

					for (ushort i = 0; i < activePlayerIdList.Count * 8; i++) {
						if (IsActivePlayerId (i)) {
							RegisterPlayerId (i);
						}
					}
					if (syncSelfSeqNum != 0) {
						ReconnectPlayerId (playerId);
					}
					break;
				case (byte)BuiltInPacket.Type.NotifyRegisterPlayer:
					ushort addPlayerId = chunk.ReadUShort (ref ctx2);
					if (state == State.Online) {
						if (addPlayerId != playerId) {
							RegisterPlayerId (addPlayerId);
						}
					} else {
						throw new System.Exception ("接続完了前にNotifyAddPlayerが来ている");
					}
					break;
				case (byte)BuiltInPacket.Type.NotifyUnegisterPlayer:
					ushort removePlayerId = chunk.ReadUShort (ref ctx2);
					if (state == State.Online) {
						if (removePlayerId != playerId) {
							UnregisterPlayerId (removePlayerId);
						}
					} else {
						throw new System.Exception ("接続完了前にNotifyRemovePlayerが来ている");
					}
					break;
				case (byte)BuiltInPacket.Type.NotifyReconnectPlayer:
					ushort reconnectPlayerId = chunk.ReadUShort (ref ctx2);
					if (state == State.Online) {
						if (reconnectPlayerId != playerId) {
							ReconnectPlayerId (reconnectPlayerId);
						}
					}
					break;
				case (byte)BuiltInPacket.Type.NotifyDisconnectPlayer:
					ushort disconnectPlayerId = chunk.ReadUShort (ref ctx2);
					if (state == State.Online) {
						if (disconnectPlayerId != playerId) {
							DisconnectPlayerId (disconnectPlayerId);
						}
					}
					break;
				case (byte)BuiltInPacket.Type.StopNetwork:
					Stop ();
					break;
				case (byte)BuiltInPacket.Type.UpdatePlayerInfo:
					DeserializeUpdatePlayerInfoPacket (senderPlayerId, ref chunk, ref ctx2);
					break;
				default:
					RecieveData (senderPlayerId, type, chunk, ctx2);
					break;
			}
		}

		protected virtual void DeserializeUpdatePlayerInfoPacket (ushort senderPlayerId, ref DataStreamReader chunk, ref DataStreamReader.Context ctx2) {
			var id = chunk.ReadUShort (ref ctx2);

			if (id == playerId) return;

			if (activePlayerInfoList[id] == null) {
				activePlayerInfoList[id] = new PlayerInfo ();
			}
			var playerInfo = activePlayerInfoList[id];

			//PlayerInfo更新
			playerInfo.Deserialize (ref chunk, ref ctx2);
		}

		/// <summary>
		/// まとめたパケット送信など、最後に行うべきUpdateループ
		/// </summary>
		public override void OnLastUpdate () {
			if (state == State.Offline) {
				return;
			}

			if (networkLinker == null) {
				return;
			}
			if (state == State.Online || state == State.Disconnecting) {
				networkLinker.SendMeasureLatencyPacket ();
				//networkLinker.SendReliableChunks ();
			}

			jobHandle = networkLinker.ScheduleSendChunks (default (JobHandle));

			jobHandle = driver.ScheduleUpdate (jobHandle);

			jobHandle = networkLinker.ScheduleRecieve (jobHandle);
		}
	}
}
