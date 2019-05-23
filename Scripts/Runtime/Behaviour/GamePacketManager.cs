using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Networking.Transport;
using UnityEngine;
using UnityEngine.Experimental.LowLevel;
using static ICKX.Radome.NetworkManagerBase;
using UpdateLoop = UnityEngine.Experimental.PlayerLoop.Update;

namespace ICKX.Radome {

	public class GamePacketManager {

		public struct GamePacketManagerUpdate { }

		[RuntimeInitializeOnLoadMethod]
		static void Initialize () {
			localStartTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ();

			CustomPlayerLoopUtility.InsertLoopFirst(typeof(UpdateLoop), new PlayerLoopSystem() {
				type = typeof(GamePacketManagerUpdate),
				updateDelegate = Update
			});
		}

		/// <summary>
		/// 現在Gameで利用するネットワーク
		/// 基本的なメソッドの呼び出しはGamePacketManagerのstaticメソッドで行うのが望ましい
		/// </summary>
		public static NetworkManagerBase NetworkManager { get; private set; } = null;

        /// <summary>
        /// 接続処理がすべて完了した際に呼ばれるイベント
        /// Serverは起動時にすぐ呼ばれる
        /// </summary>
        public static event OnConnectEvent OnConnect = null;
        /// <summary>
        /// 切断時に呼ばれるイベント error=0なら正常切断
        /// </summary>
        public static event OnDisconnectAllEvent OnDisconnectAll = null;
        /// <summary>
        /// 接続自体が失敗した際に呼ばれるイベント
        /// </summary>
        public static event OnConnectFailedEvent OnConnectFailed = null;

        /// <summary>
        /// 誰かPlayerが再接続した場合に呼ばれるイベント
        /// </summary>
        public static event OnReconnectPlayerEvent OnReconnectPlayer = null;
		/// <summary>
		/// 誰かPlayerが通信状況などで切断した場合に呼ばれるイベント（申告して意図的に退出した場合は呼ばれない）
		/// </summary>
		public static event OnDisconnectPlayerEvent OnDisconnectPlayer = null;
		/// <summary>
		/// 誰かPlayerが接続した場合に呼ばれるイベント
		/// </summary>
		public static event OnRegisterPlayerEvent OnRegisterPlayer = null;
		/// <summary>
		/// 誰かPlayerが退出した場合に呼ばれるイベント（通信状況などによる切断では呼ばれない）
		/// </summary>
		public static event OnUnregisterPlayerEvent OnUnregisterPlayer = null;
		/// <summary>
		/// 送信されたパケットを受け取る
		/// </summary>
		public static event OnRecievePacketEvent OnRecievePacket = null;

		public static bool IsLeader {
			get {
				if (NetworkManager == null) {
					return true;
				} else {
					return NetworkManager.IsLeader;
				}
			}
		}

		public static ushort PlayerId {
			get {
				if(NetworkManager == null) {
					return 0;
				}else {
					return NetworkManager.MyPlayerId;
				}
			}
		}

		private static long localStartTime;

		public static long LeaderStartTime {
			get {
				if (NetworkManager == null) {
					return localStartTime;
				} else {
					return NetworkManager.LeaderStatTime;
				}
			}
		}

		public static long currentUnixTime { get; private set; }

		public static uint progressTimeSinceStartup {
			get { return (uint)(currentUnixTime - LeaderStartTime); }
		}

		private static void Update() {
			currentUnixTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		}

		public static ushort GetPlayerCount () {
			return (NetworkManager == null) ? (ushort)0 : NetworkManager.GetPlayerCount () ;
		}

        public static IReadOnlyList<DefaultPlayerInfo> PlayerInfoList {
            get { return NetworkManager != null ? NetworkManager.PlayerInfoList : null ; }
        }

		public bool IsActivePlayerId (ushort playerId) {
			return NetworkManager.IsActivePlayerId (playerId);
		}

		public static void SetNetworkManager (NetworkManagerBase networkManager)
        {
            RemoveNetworkManager ();
			NetworkManager = networkManager;

            NetworkManager.OnConnect += ExecOnCconnect;
            NetworkManager.OnDisconnectAll += ExecOnDisconnectAll;
            NetworkManager.OnConnectFailed += ExecOnCconnectFailed;
            NetworkManager.OnReconnectPlayer += ExecOnReconnectPlayer;
            NetworkManager.OnDisconnectPlayer += ExecOnDisconnectPlayer;
			NetworkManager.OnRegisterPlayer += ExecOnRegisterPlayer;
			NetworkManager.OnUnregisterPlayer += ExecOnUnregisterPlayer;
			NetworkManager.OnRecievePacket += ExecOnRecievePacket;
		}

		public static void RemoveNetworkManager ()
        {
            if (NetworkManager != null) {
                NetworkManager.OnConnect -= ExecOnCconnect;
                NetworkManager.OnDisconnectAll -= ExecOnDisconnectAll;
                NetworkManager.OnConnectFailed -= ExecOnCconnectFailed;
                NetworkManager.OnReconnectPlayer -= ExecOnReconnectPlayer;
				NetworkManager.OnDisconnectPlayer -= ExecOnDisconnectPlayer;
				NetworkManager.OnRegisterPlayer -= ExecOnRegisterPlayer;
				NetworkManager.OnUnregisterPlayer -= ExecOnUnregisterPlayer;
				NetworkManager.OnRecievePacket -= ExecOnRecievePacket;
				NetworkManager = null;
			}
		}

        private static void ExecOnCconnect() { OnConnect?.Invoke(); }

        private static void ExecOnDisconnectAll(byte errorCode) { OnDisconnectAll?.Invoke(errorCode); }

        private static void ExecOnCconnectFailed(byte errorCode) { OnConnectFailed?.Invoke(errorCode); }

        private static void ExecOnReconnectPlayer (ushort playerId, ulong uniqueId) { OnReconnectPlayer?.Invoke (playerId, uniqueId); }

		private static void ExecOnDisconnectPlayer (ushort playerId, ulong uniqueId) { OnDisconnectPlayer?.Invoke (playerId, uniqueId); }

		private static void ExecOnRegisterPlayer (ushort playerId, ulong uniqueId) { OnRegisterPlayer?.Invoke (playerId, uniqueId); }

		private static void ExecOnUnregisterPlayer (ushort playerId, ulong uniqueId) { OnUnregisterPlayer?.Invoke (playerId, uniqueId); }

		private static void ExecOnRecievePacket (ushort senderPlayerId, ulong senderUniqueId, byte type, DataStreamReader stream, DataStreamReader.Context ctx) {
			OnRecievePacket?.Invoke (senderPlayerId, senderUniqueId, type, stream, ctx);
		}

		public static void Send (ushort playerId, DataStreamWriter data, QosType qos) {
			if (NetworkManager == null || NetworkManager.NetwrokState == NetworkConnection.State.Disconnected) return;
			NetworkManager.Send (playerId, data, qos);
		}

		public static void Multicast (NativeList<ushort> playerIdList, DataStreamWriter data, QosType qos) {
			if (NetworkManager == null || NetworkManager.NetwrokState == NetworkConnection.State.Disconnected) return;
			NetworkManager.Multicast (playerIdList, data, qos);
		}

		public static void Brodcast (DataStreamWriter data, QosType qos, bool noChunk = false) {
			if (NetworkManager == null || NetworkManager.NetwrokState == NetworkConnection.State.Disconnected) return;
			NetworkManager.Broadcast (data, qos, noChunk);
		}

	}
}
