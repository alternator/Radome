﻿using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Networking.Transport;
using UnityEngine;
using static ICKX.Radome.NetworkManagerBase;

namespace ICKX.Radome {

    public class GamePacketManager {

        [RuntimeInitializeOnLoadMethod]
        static void Initialize () {
            localStartTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds ();
        }

		/// <summary>
		/// 現在Gameで利用するネットワーク
		/// 基本的なメソッドの呼び出しはGamePacketManagerのstaticメソッドで行うのが望ましい
		/// </summary>
		public static NetworkManagerBase NetworkManager { get; private set; } = null;

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
                    return NetworkManager.isLeader;
                }
            }
        }

        public static ushort PlayerId {
            get {
                if(NetworkManager == null) {
                    return 0;
                }else {
                    return NetworkManager.playerId;
                }
            }
        }

        private static long localStartTime;

        public static long LeaderStartTime {
            get {
                if (NetworkManager == null) {
                    return localStartTime;
                } else {
                    return NetworkManager.leaderStatTime;
                }
            }
        }

		public static ushort GetPlayerCount () {
			return NetworkManager.GetPlayerCount () ;
		}

		public bool IsActivePlayerId (ushort playerId) {
			return NetworkManager.IsActivePlayerId (playerId);
		}

		public static void SetNetworkManager (NetworkManagerBase networkManager) {
			RemoveNetworkManager ();
			NetworkManager = networkManager;

			NetworkManager.OnReconnectPlayer += ExecOnReconnectPlayer;
			NetworkManager.OnDisconnectPlayer += ExecOnDisconnectPlayer;
			NetworkManager.OnRegisterPlayer += ExecOnRegisterPlayer;
            NetworkManager.OnUnregisterPlayer += ExecOnUnregisterPlayer;
            NetworkManager.OnRecievePacket += ExecOnRecievePacket;
        }

		public static void RemoveNetworkManager () {
			if (NetworkManager != null) {
				NetworkManager.OnReconnectPlayer -= ExecOnReconnectPlayer;
				NetworkManager.OnDisconnectPlayer -= ExecOnDisconnectPlayer;
				NetworkManager.OnRegisterPlayer -= ExecOnRegisterPlayer;
				NetworkManager.OnUnregisterPlayer -= ExecOnUnregisterPlayer;
				NetworkManager.OnRecievePacket -= ExecOnRecievePacket;
				NetworkManager = null;
			}
		}

		private static void ExecOnReconnectPlayer (ushort id) { OnReconnectPlayer?.Invoke (id); }

		private static void ExecOnDisconnectPlayer (ushort id) { OnDisconnectPlayer?.Invoke (id); }

		private static void ExecOnRegisterPlayer (ushort id) { OnRegisterPlayer?.Invoke (id); }

		private static void ExecOnUnregisterPlayer (ushort id) { OnUnregisterPlayer?.Invoke (id); }

		private static void ExecOnRecievePacket (ushort senderPlayerId, byte type, DataStreamReader stream, DataStreamReader.Context ctx) {
			OnRecievePacket?.Invoke (senderPlayerId, type, stream, ctx);
		}

		public static void Send (ushort playerId, DataStreamWriter data, QosType qos) {
			if (NetworkManager == null || NetworkManager.state == State.Offline) return;
			NetworkManager.Send (playerId, data, qos);
		}

		public static void Multicast (NativeList<ushort> playerIdList, DataStreamWriter data, QosType qos) {
			if (NetworkManager == null || NetworkManager.state == State.Offline) return;
			NetworkManager.Multicast (playerIdList, data, qos);
		}

		public static void Brodcast (DataStreamWriter data, QosType qos, bool noChunk = false) {
            if (NetworkManager == null || NetworkManager.state == State.Offline) return;
            NetworkManager.Brodcast (data, qos, noChunk);
        }

    }
}
