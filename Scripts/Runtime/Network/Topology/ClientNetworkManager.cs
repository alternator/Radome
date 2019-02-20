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

    public class ClientNetworkManager : NetworkManagerBase {

		public struct PlayerInfo {
			internal byte isCreated;
			public State state;
			public float disconnectTime;

			public bool IsCreated { get { return isCreated != 0; } }
		}

		public override bool isFullMesh => false;

        public NetworkLinkerHandle networkLinkerHandle;

        private IPAddress serverAdress;
        private int serverPort;

		public NativeList<PlayerInfo> activePlayerInfoList;

        public ClientNetworkManager () : base () {
			activePlayerInfoList = new NativeList<PlayerInfo> (8, Allocator.Persistent);
        }

        public override void Dispose () {
            if (state != State.Offline) {
                StopComplete ();
            }
			activePlayerInfoList.Dispose ();
            driver.Dispose ();
            base.Dispose ();
        }

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
            state = State.Connecting;

            networkLinkerHandle = NetworkLinkerPool.CreateLinkerHandle (driver, driver.Connect (endpoint));

            Debug.Log ("StartClient");
        }

        //再接続
        private void Reconnect () {
            var endpoint = new IPEndPoint (serverAdress, serverPort);
            state = State.Connecting;

            var linker = NetworkLinkerPool.GetLinker (networkLinkerHandle);
            linker.Reconnect (driver.Connect (endpoint));
            Debug.Log ("Reconnect");
        }

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
                Send (ServerPlayerId, unregisterPlayerPacket, QosType.Reliable, true);
            }
            Debug.Log ("Stop");
        }

        // サーバーから切断されたらLinkerを破棄して停止
        public override void StopComplete () {
			UnregisterPlayerId (playerId);

			playerId = 0;
            state = State.Offline;
            jobHandle.Complete ();

			//var linker = NetworkLinkerPool.GetLinker (networkLinkerHandle);
			//driver.Disconnect (linker.connection);    //ここはserverからDisconnectされたら行う処理

			NetworkLinkerPool.ReleaseLinker (networkLinkerHandle);
            networkLinkerHandle = default;
            Debug.Log ("StopComplete");
        }

		//新しいPlayerを登録する処理
		protected new void RegisterPlayerId (ushort id) {
			base.RegisterPlayerId (id);

			var playerInfo = new PlayerInfo () {
				isCreated = 1,
				state = State.Online
			};

			while (id >= activePlayerInfoList.Length) {
				activePlayerInfoList.Add (default);
			}
			activePlayerInfoList[id] = playerInfo;

			ExecOnRegisterPlayer (id);
		}

		//Playerを登録解除する処理
		protected new void UnregisterPlayerId (ushort id) {
			base.UnregisterPlayerId (id);

			if (id < activePlayerInfoList.Length) {
				activePlayerInfoList[id] = default;
				ExecOnUnregisterPlayer (id);
			}
		}

		//Playerを再接続させる処理
		protected void ReconnectPlayerId (ushort id) {

			if (id < activePlayerInfoList.Length) {
				var info = activePlayerInfoList[id];
				if (info.IsCreated) {
					info.state = State.Online;
					activePlayerInfoList[id] = info;
					ExecOnReconnectPlayer (id);
				} else {
					Debug.LogError ($"ReconnectPlayerId Error. ID={id}は未登録");
				}
			}else {
				Debug.LogError ($"ReconnectPlayerId Error. ID={id}は未登録");
			}
		}

		//Playerを一旦切断状態にする処理
		protected void DisconnectPlayerId (ushort id) {

			if (id < activePlayerInfoList.Length) {
				var info = activePlayerInfoList[id];
				if (info.IsCreated) {
					info.state = State.Offline;
					info.disconnectTime = Time.realtimeSinceStartup;
					activePlayerInfoList[id] = info;
					ExecOnDisconnectPlayer (id);
				}else {
					Debug.LogError ($"DisconnectPlayerId Error. ID={id}は未登録");
				}
			}else {
				Debug.LogError ($"DisconnectPlayerId Error. ID={id}は未登録");
			}
		}


		/// <summary>
		/// Player1人にパケットを送信
		/// </summary>
		public override ushort Send (ushort targetPlayerId, DataStreamWriter data, QosType qos, bool noChunk = false) {
            if (state == State.Offline) {
                Debug.LogError ("Send Failed : State.Offline");
                return 0;
            }
            ushort seqNum = 0;
            using (var writer = CreateSendPacket (data, qos, targetPlayerId, playerId)) {
                if (networkLinkerHandle.IsCreated) {
                    NetworkLinker linker = NetworkLinkerPool.GetLinker (networkLinkerHandle);
                    if (linker != null) {
                         seqNum = linker.Send (writer, qos, noChunk);
                    }
                } else {
                    Debug.LogError ("Send Failed : is not create networkLinker");
                }
            }
            return seqNum;
        }

		public override ushort Send (NativeList<ushort> playerIdList, DataStreamWriter data, QosType qos, bool noChunk = false) {
			if (state == State.Offline) {
				Debug.LogError ("Send Failed : State.Offline");
				return 0;
			}
			ushort seqNum = 0;
			using (var writer = CreateSendPacket (data, qos, playerIdList, playerId)) {
				if (networkLinkerHandle.IsCreated) {
					NetworkLinker linker = NetworkLinkerPool.GetLinker (networkLinkerHandle);
					if (linker != null) {
						seqNum = linker.Send (writer, qos, noChunk);
					}
				} else {
					Debug.LogError ("Send Failed : is not create networkLinker");
				}
			}
			return seqNum;
		}

		protected DataStreamWriter CreateSendPacket (DataStreamWriter data, QosType qos, NativeList<ushort> targetIdList, ushort senderId) {
			unsafe {
				byte* dataPtr = DataStreamUnsafeUtility.GetUnsafeReadOnlyPtr (data);
				ushort dataLength = (ushort)data.Length;
				var writer = new DataStreamWriter (data.Length + 6 + targetIdList.Length * 2, Allocator.Temp);
				writer.Write (ushort.MaxValue - 1);
				writer.Write (senderId);
				writer.Write ((ushort)targetIdList.Length);
				for (int i = 0; i < targetIdList.Length; i++) {
					writer.Write (targetIdList[i]);
				}
				writer.WriteBytes (dataPtr, data.Length);
				return writer;
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
            Send (ushort.MaxValue, data, qos, noChunk);
        }

        /// <summary>
        /// Player1人にパケットを送信 受け取り確認可能
        /// </summary>
        public override void SendReliable (ushort playerId, DataStreamWriter data, QosType qos, System.Action<ushort> onComplete, bool noChunk = false) {
            throw new System.NotImplementedException ();
        }

        /// <summary>
        /// 全Playerにパケットを送信 受け取り確認可能
        /// </summary>
        public override void BrodcastReliable (DataStreamWriter data, QosType qos, System.Action<ushort> onComplete, bool noChunk = false) {
            throw new System.NotImplementedException ();
            //サーバーに到達したら確定とする
        }

        /// <summary>
        /// 受信パケットの受け取りなど、最初に行うべきUpdateループ
        /// </summary>
        public override void OnFirstUpdate () {
            jobHandle.Complete ();
            if (!networkLinkerHandle.IsCreated) {
                return;
            }

            var linker = NetworkLinkerPool.GetLinker (networkLinkerHandle);
            linker.Complete ();

            if (state == State.Offline) {
                return;
            }

            //受け取ったパケットを処理に投げる.
            if (!networkLinkerHandle.IsCreated) return;

            if (linker.IsConnected) {
                //Debug.Log ("IsConnected : dataLen=" + linker.dataStreams.Length);
            }
            if (linker.IsDisconnected) {
                //Debug.Log ("IsDisconnected");
                if(state == State.Disconnecting) {
                    StopComplete ();
                }else {
					DisconnectPlayerId (playerId);
                    Reconnect ();
                }
                return;
            }

            for (int j = 0; j < linker.dataStreams.Length; j++) {
				var stream = linker.dataStreams[j];
				var ctx = default (DataStreamReader.Context);
				if (!ReadQosHeader (stream, ref ctx, out var qosType, out var seqNum, out var ackNum)) {
					continue;
				}
				//chunkをバラして解析
				while (true) {
					if (!ReadChunkHeader (stream, ref ctx, out var chunk, out var ctx2, out ushort targetPlayerId, out ushort senderPlayerId)) {
						break;
					}
					//Debug.Log ("Linker streamLen=" + stream.Length + ", Pos=" + pos + ", chunkLen=" + chunk.Length + ",type=" + type + ",target=" + targetPlayerId + ",sender=" + senderPlayerId);
					byte type = chunk.ReadByte (ref ctx2);

					//自分宛パケットの解析
					switch (type) {
                        case (byte)BuiltInPacket.Type.RegisterPlayer:
                            state = State.Online;
                            playerId = chunk.ReadUShort (ref ctx2);
                            leaderStatTime = chunk.ReadLong (ref ctx2);

                            ushort syncSelfSeqNum = chunk.ReadUShort (ref ctx2);
                            linker.SyncSeqNum (syncSelfSeqNum);

							byte count = chunk.ReadByte (ref ctx2);
							activePlayerIdList.Clear ();
							for (int i = 0; i < count; i++) {
                                activePlayerIdList.Add( chunk.ReadByte(ref ctx2));
                            }

							for (byte i = 0; i < activePlayerIdList.Length; i++) {
								Debug.Log ("activePlayerIdList : " + i + " : " + activePlayerIdList[i]);
							}

							for (byte i = 0; i < 255; i++) {
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
                            }else {
                                throw　new System.Exception ("接続完了前にNotifyAddPlayerが来ている");
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
                        default:
                            RecieveData (senderPlayerId, type, chunk, ctx2);
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// まとめたパケット送信など、最後に行うべきUpdateループ
        /// </summary>
        public override void OnLastUpdate () {
            if (state == State.Offline) {
                return;
            }

            if (!networkLinkerHandle.IsCreated) {
                return;
            }
            var linker = NetworkLinkerPool.GetLinker (networkLinkerHandle);

            if (state == State.Online || state == State.Disconnecting) {
                linker.SendMeasureLatencyPacket ();
                linker.SendReliableChunks ();
            }

            jobHandle = linker.ScheduleSendUnreliableChunks (default (JobHandle));

            jobHandle = driver.ScheduleUpdate (jobHandle);

            jobHandle = linker.ScheduleRecieve (jobHandle);
        }
    }
}
