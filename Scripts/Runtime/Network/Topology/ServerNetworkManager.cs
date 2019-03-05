using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using Unity.Collections;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Profiling;
using UdpCNetworkDriver = Unity.Networking.Transport.BasicNetworkDriver<Unity.Networking.Transport.IPv4UDPSocket>;

namespace ICKX.Radome {

	public class UDPServerNetworkManager<PlayerInfo> : ServerNetworkManager<UdpCNetworkDriver, PlayerInfo> where PlayerInfo : DefaultPlayerInfo, new() {

		public UDPServerNetworkManager (PlayerInfo playerInfo) : base (playerInfo) {
		}

		/// <summary>
		/// サーバー起動
		/// </summary>
		public void Start (int port) {
			var config = new NetworkConfigParameter () {
				connectTimeoutMS = 1000 * 5,
				disconnectTimeoutMS = 1000 * 5,
			};
			Start (port, config);
		}

		public void Start (int port, NetworkConfigParameter config) {
			if (state != State.Offline) {
				Debug.LogError ("Start Failed  currentState = " + state);
				return;
			}

			if (!driver.IsCreated) {
				driver = new UdpCNetworkDriver (new INetworkParameter[] { config });
			}

			var endPoint = new IPEndPoint (IPAddress.Any, port);
			if (driver.Bind (endPoint) != 0) {
				Debug.Log ("Failed to bind to port 9000");
			} else {
				driver.Listen ();
			}

			Start ();
		}

		protected override bool IsReconnect (NetworkConnection connection, out ushort disconnectedPlayerId) {
			var remoteEndPoint = driver.RemoteEndPoint (connection);
			disconnectedPlayerId = ContainEndPointPlayerInfoList (remoteEndPoint);
			return disconnectedPlayerId != 0;
		}

		/// <summary>
		/// 今までに接続してきたplayerのEndPointがあればPlayerIDを返す
		/// </summary>
		public ushort ContainEndPointPlayerInfoList (NetworkEndPoint endPoint) {
			for (ushort i = 0; i < networkLinkers.Count; i++) {
				if (networkLinkers[i] != null) {
					var linker = networkLinkers[i];
					var remoteEndPoint = driver.RemoteEndPoint (linker.connection);

					if (endPoint.GetIp () == remoteEndPoint.GetIp () && endPoint.Port == remoteEndPoint.Port) {
						return i;
					}
				}
			}
			return 0;
		}
	}

	public abstract class ServerNetworkManager<Driver, PlayerInfo> : NetworkManagerBase
			where Driver : struct, INetworkDriver where PlayerInfo : DefaultPlayerInfo, new() {

		public float registrationTimeOut { get; set; } = 60.0f;

		public override bool isFullMesh => false;

		public Driver driver;
		public List<NetworkLinker<Driver>> networkLinkers;
		public List<PlayerInfo> activePlayerInfoList = new List<PlayerInfo> (16);

		private DataStreamWriter relayWriter;

		public PlayerInfo MyPlayerInfo { get; protected set; }

		public ServerNetworkManager (PlayerInfo playerInfo) : base () {
			MyPlayerInfo = playerInfo;
			relayWriter = new DataStreamWriter (NetworkParameterConstants.MTU, Allocator.Persistent);
		}

		public override void Dispose () {
			if (state != State.Offline) {
				StopComplete ();
			}
			if (driver.IsCreated) {
				driver.Dispose ();
			}
			relayWriter.Dispose ();
			base.Dispose ();
		}

		protected void Start () {
			leaderStatTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ();
			state = State.Connecting;

			networkLinkers = new List<NetworkLinker<Driver>> (16);
			RegisterPlayerId (0, default);
		}

		/// <summary>
		/// サーバー停止
		/// </summary>
		public override void Stop () {
			if (state == State.Offline) {
				Debug.LogError ("Start Failed  currentState = " + state);
				return;
			}
			if (!jobHandle.IsCompleted) {
				Debug.LogError ("NetworkJob実行中に停止できない");
				return;
			}

			state = State.Disconnecting;

			//すべてのPlayerに停止を伝えてからサーバーも停止
			if (GetPlayerCount () == 1) {
				StopComplete ();
			} else {
				BroadcastStopNetworkPacket ();
				Debug.Log ("Stop");
			}
		}

		// すべてのClientが切断したら呼ぶ
		public override void StopComplete () {
			if (state == State.Offline) {
				Debug.LogError ("CompleteStop Failed  currentState = " + state);
				return;
			}

			state = State.Offline;
			jobHandle.Complete ();

			driver.Dispose ();

			if (networkLinkers != null && networkLinkers.Count > 0) {
				for (int i = 0; i < networkLinkers.Count; i++) {
					if (networkLinkers[i] != null) {
						networkLinkers[i].Dispose ();
						networkLinkers[i] = null;
					}
				}
			}
			Debug.Log ("StopComplete");
		}

		//新しいPlayerを登録する処理
		protected virtual void RegisterPlayerId (ushort id, NetworkConnection connection) {
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

			if (id == 0) {
				networkLinkers.Add (null);
				//自分のユーザー情報をリストに登録
				activePlayerInfoList[0] = MyPlayerInfo;
			} else {
				if (id == networkLinkers.Count) {
					networkLinkers.Add (new NetworkLinker<Driver> (driver, connection, NetworkParameterConstants.MTU));
				} else {
					networkLinkers[id] = new NetworkLinker<Driver> (driver, connection, NetworkParameterConstants.MTU);
				}

				//playerIDを通知するパケットを送信.
				SendRegisterPlayerPacket (id);

				//他の接続済みplayerに通知
				BroadcastNotifyAddPlayerPacket (id);

				//新しく接続してきたプレイヤーにユーザー情報を通知
				for (ushort i = 0; i < activePlayerInfoList.Count; i++) {
					if (activePlayerInfoList[i] == null) continue;
					using (var updatePlayerPacket = activePlayerInfoList[i].CreateUpdatePlayerInfoPacket (i)) {
						Send (id, updatePlayerPacket, QosType.Reliable);
					}
				}
			}

			//完了イベント
			ExecOnRegisterPlayer (id);
		}

		//Playerを登録解除する処理
		protected override void UnregisterPlayerId (ushort id) {
			base.UnregisterPlayerId (id);

			if (id < activeConnectionInfoList.Count) {
				driver.Disconnect (networkLinkers[id].connection);
				networkLinkers[id].Dispose ();
				networkLinkers[id] = null;
				activeConnectionInfoList[id] = default;
				activePlayerInfoList[id] = null;
			}

			if (state == State.Disconnecting) {
				//すべて切断したらサーバー完全停止.
				if (GetPlayerCount () == 1) {
					StopComplete ();
				}
			} else {
				//接続済みplayerに通知
				BroadcastNotifyRemovePlayerPacket (id);
			}
			ExecOnUnregisterPlayer (id);
		}

		//Playerを再接続させる処理
		protected virtual void ReconnectPlayerId (ushort id, NetworkConnection connection) {

			if (id < activeConnectionInfoList.Count) {
				var info = activeConnectionInfoList[id];
				if (info.isCreated) {
					info.state = State.Online;
					activeConnectionInfoList[id] = info;

					networkLinkers[id].Reconnect (connection);

					//playerIDを通知するパケットを送信.
					SendRegisterPlayerPacket (id);
					//他の接続済みplayerに通知
					BroadcastNotifyReconnectPlayerPacket (id);

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
					info.state = State.Connecting;
					info.disconnectTime = Time.realtimeSinceStartup;
					activeConnectionInfoList[id] = info;

					//他の接続済みplayerに通知
					BroadcastNotifyDisconnectPlayerPacket (id);

					ExecOnDisconnectPlayer (id);
				} else {
					Debug.LogError ($"DisconnectPlayerId Error. ID={id}は未登録");
				}
			} else {
				Debug.LogError ($"DisconnectPlayerId Error. ID={id}は未登録");
			}
		}

		/// <summary>
		/// Player1人にパケットを送信 (chunk化されないので注意)
		/// </summary>
		public override ushort Send (ushort targetPlayerId, DataStreamWriter data, QosType qos) {
			if (state == State.Offline) {
				Debug.LogError ("Send Failed : State." + state);
				return 0;
			}
			ushort seqNum = 0;
			if (targetPlayerId < networkLinkers.Count && networkLinkers[targetPlayerId] != null) {
				seqNum = networkLinkers[targetPlayerId].Send (data, qos, targetPlayerId, playerId, false);
			} else {
				Debug.LogError ("Send Failed : is not create networkLinker ID = " + targetPlayerId);
			}
			return seqNum;
		}

		/// <summary>
		/// PlayerListの複数人にパケットを送信(chunk化されないので注意)
		/// </summary>
		public override void Multicast (NativeList<ushort> playerIdList, DataStreamWriter data, QosType qos) {
			if (state == State.Offline) {
				Debug.LogError ("Send Failed : State." + state);
				return;
			}
			for (int i = 1; i < playerIdList.Length; i++) {
				if (networkLinkers[playerIdList[i]] != null) {
					networkLinkers[playerIdList[i]].Send (data, qos, playerIdList[i], playerId, false);
				}
			}
		}

		/// <summary>
		/// 全Playerにパケットを送信
		/// </summary>
		public override void Brodcast (DataStreamWriter data, QosType qos, bool noChunk = false) {
			if (state == State.Offline) {
				Debug.LogError ("Send Failed : State." + state);
				return;
			}
			for (int i = 1; i < networkLinkers.Count; i++) {
				if (networkLinkers[i] != null) {
					networkLinkers[i].Send (data, qos, NetworkLinkerConstants.BroadcastId, playerId, noChunk);
				}
			}
		}

		private void SendRegisterPlayerPacket (ushort id) {
			var linker = networkLinkers[id];

			using (var registerPacket = new DataStreamWriter (14 + activePlayerIdList.Count, Allocator.Temp)) {
				registerPacket.Write ((byte)BuiltInPacket.Type.RegisterPlayer);
				registerPacket.Write (id);
				registerPacket.Write (leaderStatTime);
				registerPacket.Write (linker.OtherSeqNumber);
				registerPacket.Write ((byte)activePlayerIdList.Count);
				for (int i = 0; i < activePlayerIdList.Count; i++) {
					registerPacket.Write (activePlayerIdList[i]);
				}
				Send (id, registerPacket, QosType.Reliable);
			}
		}

		private void BroadcastNotifyAddPlayerPacket (ushort id) {
			using (var addPlayerPacket = new DataStreamWriter (3, Allocator.Temp)) {
				addPlayerPacket.Write ((byte)BuiltInPacket.Type.NotifyRegisterPlayer);
				addPlayerPacket.Write (id);
				Brodcast (addPlayerPacket, QosType.Reliable);
			}
		}

		private void BroadcastNotifyRemovePlayerPacket (ushort id) {
			using (var removePlayerPacket = new DataStreamWriter (3, Allocator.Temp)) {
				removePlayerPacket.Write ((byte)BuiltInPacket.Type.NotifyUnegisterPlayer);
				removePlayerPacket.Write (id);
				Brodcast (removePlayerPacket, QosType.Reliable);
			}
		}

		private void BroadcastStopNetworkPacket () {
			using (var stopNetworkPacket = new DataStreamWriter (2, Allocator.Temp)) {
				stopNetworkPacket.Write ((byte)BuiltInPacket.Type.StopNetwork);
				stopNetworkPacket.Write ((byte)0);    //TODO error code

				Brodcast (stopNetworkPacket, QosType.Reliable);
			}
		}
		private void BroadcastNotifyReconnectPlayerPacket (ushort id) {
			using (var removePlayerPacket = new DataStreamWriter (3, Allocator.Temp)) {
				removePlayerPacket.Write ((byte)BuiltInPacket.Type.NotifyReconnectPlayer);
				removePlayerPacket.Write (id);
				Brodcast (removePlayerPacket, QosType.Reliable);
			}
		}

		private void BroadcastNotifyDisconnectPlayerPacket (ushort id) {
			using (var removePlayerPacket = new DataStreamWriter (3, Allocator.Temp)) {
				removePlayerPacket.Write ((byte)BuiltInPacket.Type.NotifyDisconnectPlayer);
				removePlayerPacket.Write (id);
				Brodcast (removePlayerPacket, QosType.Reliable);
			}
		}

		private bool isFirstUpdateComplete = false;

		protected abstract bool IsReconnect (NetworkConnection connection, out ushort disconnectedPlayerId);

		/// <summary>
		/// 受信パケットの受け取りなど、最初に行うべきUpdateループ
		/// </summary>
		public override void OnFirstUpdate () {
			if (state == State.Offline) {
				return;
			}

			//job完了待ち
			jobHandle.Complete ();
			for (int i = 0; i < networkLinkers.Count; i++) {
				if (networkLinkers[i] != null) {
					networkLinkers[i].Complete ();
				}
			}

			//接続確認
			NetworkConnection connection;
			while ((connection = driver.Accept ()) != default) {
				state = State.Online;
				if (IsReconnect (connection, out ushort disconnectedPlayerId)) {
					//再接続として扱う
					if (activeConnectionInfoList[disconnectedPlayerId].state == State.Connecting) {
						ReconnectPlayerId (disconnectedPlayerId, connection);
						Debug.Log ("Accepted a reconnection  playerId=" + disconnectedPlayerId);
					}
				} else {
					//接続してきたクライアントとLinkerで接続
					ushort newPlayerId = GetDeactivePlayerId ();
					RegisterPlayerId (newPlayerId, connection);
					Debug.Log ("Accepted a connection  newPlayerId=" + newPlayerId);
				}
			}

			//一定時間切断したままのplayerの登録を解除
			for (ushort i = 0; i < activeConnectionInfoList.Count; i++) {
				var info = activeConnectionInfoList[i];
				if (info.isCreated) {
					if (info.state == State.Connecting) {
						if (Time.realtimeSinceStartup - info.disconnectTime > registrationTimeOut) {
							UnregisterPlayerId (i);
						}
					}
				}
			}

			//受け取ったパケットを処理に投げる.
			for (ushort i = 0; i < networkLinkers.Count; i++) {
				if (networkLinkers[i] == null) continue;

				var linker = networkLinkers[i];

				if (linker.IsDisconnected) {
					//Debug.Log ("IsDisconnected");
					//切断したClientを一時停止状態に.
					DisconnectPlayerId (i);
					continue;
				}

				bool finish = false;

				//受け取ったパケットを解析
				for (int j = 0; j < linker.dataStreams.Length; j++) {
					var stream = linker.dataStreams[j];
					var ctx = default (DataStreamReader.Context);
					if (!ReadQosHeader (stream, ref ctx, out var qosType, out var seqNum, out var ackNum, out ushort targetPlayerId, out ushort senderPlayerId)) {
						continue;
					}

					Profiler.BeginSample ("Update First Linker Relay");
					//Debug.Log ($"{j}, {qosType}, {seqNum}, {ackNum}, {stream.Length}");
					if (targetPlayerId == NetworkLinkerConstants.MulticastId) {
						//Multi Targetの場合
						ushort len = stream.ReadUShort (ref ctx);
						var ctxMulticast = ctx;
						var chunk = stream.ReadChunk (ref ctxMulticast, stream.Length - NetworkLinkerConstants.HeaderSize + 2 + 2 * len);
						for (int k = 0; k < len; k++) {
							var multiTatgetId = stream.ReadUShort (ref ctx);
							if (multiTatgetId == 0) {
								//targetPlayerId = 0;
							} else {
								RelayPacket (qosType, multiTatgetId, senderPlayerId, chunk);
							}
						}
					} else if ((targetPlayerId != ServerPlayerId)) {
						//パケットをリレーする
						var ctxBroadcast = ctx;
						RelayPacket (qosType, targetPlayerId, senderPlayerId
							, stream.ReadChunk(ref ctxBroadcast, stream.Length - NetworkLinkerConstants.HeaderSize));
					}
					Profiler.EndSample ();

					Profiler.BeginSample ("Update First Chunk");
					//chunkをバラして解析
					while (!finish) {
						if (!ReadChunkHeader (stream, ref ctx, out var chunk, out var ctx2)) {
							break;
						}
						//Debug.Log ("Linker streamLen=" + stream.Length + ", Pos=" + pos + ", chunkLen=" + chunk.Length + ",type=" + type + ",target=" + targetPlayerId + ",sender=" + senderPlayerId);
						byte type = chunk.ReadByte (ref ctx2);

						if ((targetPlayerId == playerId || targetPlayerId == NetworkLinkerConstants.BroadcastId)) {
							//自分宛パケットの解析
							finish = DeserializePacket (senderPlayerId, type, ref chunk, ref ctx2);
						}
					}
					Profiler.EndSample ();
					if (finish) {
						if (state == State.Offline) {
							//server停止ならUpdate完全終了
							return;
						} else {
							//1 clientが停止ならパケット解析だけ終了
							break;
						}
					}
				}
			}
			isFirstUpdateComplete = true;
		}

		private void RelayPacket (QosType qosType, ushort targetPlayerId, ushort senderPlayerId, DataStreamReader stream) {
			relayWriter.Clear ();
			unsafe {
				byte* chunkPtr = stream.GetUnsafeReadOnlyPtr ();
				relayWriter.WriteBytes (chunkPtr, (ushort)stream.Length);
			}
			if (targetPlayerId == NetworkLinkerConstants.BroadcastId) {
				for (ushort k = 1; k < networkLinkers.Count; k++) {
					if (senderPlayerId == k) continue;
					if (k >= networkLinkers.Count) continue;
					var relayLinker = networkLinkers[k];
					if (relayLinker == null) continue;
					//BroadCastならchunk済みなので再度chunk化しない
					relayLinker.Send (relayWriter, qosType, k, senderPlayerId, true);
				}
			} else {
				if (targetPlayerId >= networkLinkers.Count) {
					var relayLinker = networkLinkers[targetPlayerId];
					if (relayLinker != null) {
						relayLinker.Send (relayWriter, qosType, targetPlayerId, senderPlayerId, true);
					}
				}
			}
		}

		protected virtual bool DeserializePacket (ushort senderPlayerId, byte type, ref DataStreamReader chunk, ref DataStreamReader.Context ctx2) {
			switch (type) {
				case (byte)BuiltInPacket.Type.UpdatePlayerInfo:
					DeserializeUpdatePlayerInfoPacket (senderPlayerId, ref chunk, ref ctx2);
					break;
				case (byte)BuiltInPacket.Type.UnregisterPlayer:
					//登録解除リクエスト
					ushort unregisterPlayerId = chunk.ReadUShort (ref ctx2);
					UnregisterPlayerId (unregisterPlayerId);
					return true;
				default:
					//自分宛パケットの解析
					RecieveData (senderPlayerId, type, chunk, ctx2);
					break;
			}
			return false;
		}

		protected virtual void DeserializeUpdatePlayerInfoPacket (ushort senderPlayerId, ref DataStreamReader chunk, ref DataStreamReader.Context ctx2) {
			var id = chunk.ReadUShort (ref ctx2);

			if (id == playerId) return;

			if (activePlayerInfoList[id] == null) {
				activePlayerInfoList[id] = new PlayerInfo ();
			}
			var clientPlayerInfo = activePlayerInfoList[id];
			//クライアントのPlayerInfo更新
			clientPlayerInfo.Deserialize (ref chunk, ref ctx2);

			//接続してきたクライアントのPlayer情報に問題なければ、全クライアントに共有
			using (var updatePlayerPacket = clientPlayerInfo.CreateUpdatePlayerInfoPacket (id)) {
				Brodcast (updatePlayerPacket, QosType.Reliable);
			}
		}

		/// <summary>
		/// まとめたパケット送信など、最後に行うべきUpdateループ
		/// </summary>
		public override void OnLastUpdate () {
			if (state == State.Offline) {
				return;
			}

			if (!isFirstUpdateComplete) return;

			//main thread処理
			for (int i = 0; i < networkLinkers.Count; i++) {
				if (networkLinkers[i] != null) {
					if (state == State.Online || state == State.Disconnecting) {
						networkLinkers[i].SendMeasureLatencyPacket ();
					}
				}
			}

			//Unreliableなパケットの送信
			var linkerJobs = new NativeArray<JobHandle> (networkLinkers.Count, Allocator.Temp);
			for (int i = 0; i < networkLinkers.Count; i++) {
				if (networkLinkers[i] != null) {
					linkerJobs[i] = networkLinkers[i].ScheduleSendChunks (default (JobHandle));
				}
			}
			jobHandle = JobHandle.CombineDependencies (linkerJobs);
			//JobHandle.ScheduleBatchedJobs ();

			//driverの更新
			jobHandle = driver.ScheduleUpdate (jobHandle);

			//TODO iJobで実行するとNetworkDriverの処理が並列にできない
			//     できればIJobParallelForでScheduleRecieveを並列化したい
			for (int i = 0; i < networkLinkers.Count; i++) {
				if (networkLinkers[i] != null) {
					//JobスレッドでLinkerのパケット処理開始
					jobHandle = networkLinkers[i].ScheduleRecieve (jobHandle);
				}
			}
			linkerJobs.Dispose ();
			isFirstUpdateComplete = false;

			JobHandle.ScheduleBatchedJobs ();
		}
	}
}
