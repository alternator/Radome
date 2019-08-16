using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Unity.Collections;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.LowLevel.Unsafe;
using Unity.Networking.Transport.Utilities;
using UnityEngine;

namespace ICKX.Radome
{
	public class UDPClientNetworkManager<PlayerInfo> : ClientNetworkManager<UdpNetworkDriver, PlayerInfo> where PlayerInfo : DefaultPlayerInfo, new()
	{
		private NetworkConfigParameter Config;

		public UDPClientNetworkManager(PlayerInfo playerInfo) : base(playerInfo)
		{
			Config = new NetworkConfigParameter()
			{
				connectTimeoutMS = 1000 * 5,
				disconnectTimeoutMS = 1000 * 5,
			};
		}

		public UDPClientNetworkManager(PlayerInfo playerInfo, NetworkConfigParameter config) : base(playerInfo)
		{
			Config = config;
		}

		private IPAddress serverAdress;
		private ushort serverPort;

		/// <summary>
		/// クライアント接続開始
		/// </summary>
		public void Start(IPAddress adress, ushort port)
		{
			Debug.Log($"Start {adress} {port}");
			serverAdress = adress;
			serverPort = port;

			if (!NetworkDriver.IsCreated)
			{
				NetworkDriver = new UdpNetworkDriver(new INetworkParameter[] {
					Config,
					new ReliableUtility.Parameters { WindowSize = 128 },
					new NetworkPipelineParams {initialCapacity = ushort.MaxValue},
                    //new SimulatorUtility.Parameters {MaxPacketSize = 256, MaxPacketCount = 32, PacketDelayMs = 100},
                });
			}

			_QosPipelines[(int)QosType.Empty] = NetworkDriver.CreatePipeline();
			//_QosPipelines[(int)QosType.Reliable] = NetworkDriver.CreatePipeline();
			//_QosPipelines[(int)QosType.Unreliable] = NetworkDriver.CreatePipeline();
			_QosPipelines[(int)QosType.Reliable] = NetworkDriver.CreatePipeline(typeof(ReliableSequencedPipelineStage), typeof(SimulatorPipelineStage));
			_QosPipelines[(int)QosType.Unreliable] = NetworkDriver.CreatePipeline(typeof(SimulatorPipelineStage));

			var endpoint = NetworkEndPoint.Parse(serverAdress.ToString(), serverPort);
			ServerConnId = NetworkDriver.Connect(endpoint).InternalId;

			base.Start();
		}

		protected override void ReconnectMethod()
		{
			Debug.Log("Reconnect" + NetworkState + " : " + serverAdress + ":" + serverPort);

			if (NetworkState != NetworkConnection.State.Connected)
			{
				var endpoint = NetworkEndPoint.Parse(serverAdress.ToString(), serverPort);
				NetworkState = NetworkConnection.State.Connecting;

				ServerConnId = NetworkDriver.Connect(endpoint).InternalId;

				Debug.Log("Reconnect");
			}
		}

		//protected override void DisconnectMethod(int connId)
		//{
		//	NetworkDriver.Disconnect(new NetworkConnection(connId));
		//}
	}

	/// <summary>
	/// サーバー用のNetworkManager
	/// 通信の手順は
	/// 
	/// [Client to Server] c.AddChunk -> c.Send -> c.Pipline -> s.Pop -> Pipline -> Chunk解除 -> Server.Recieve
	/// [Client to Client] c.AddChunk -> c.Send -> c.Pipline -> s.Pop -> Pipline -> s.Relay -> Pipline -> c.Pop -> Chunk解除 -> Server.Recieve
	/// </summary>
	/// <typeparam name="Driver"></typeparam>
	/// <typeparam name="PlayerInfo"></typeparam>
	public abstract class ClientNetworkManager<Driver, PlayerInfo> : ClientNetworkManagerBase<int, PlayerInfo>
			where Driver : struct, INetworkDriver where PlayerInfo : DefaultPlayerInfo, new()
	{
		protected override int EmptyConnId => -1;

		public Driver NetworkDriver;
		protected NativeArray<NetworkPipeline> _QosPipelines;
		
		public bool AutoRecconect { get; set; } = true;

		private bool _IsFirstUpdateComplete = false;

		public ClientNetworkManager(PlayerInfo playerInfo) : base(playerInfo)
		{
			_QosPipelines = new NativeArray<NetworkPipeline>((int)QosType.ChunkEnd, Allocator.Persistent);
		}

		public override void Dispose()
		{
			if (_IsDispose) return;

			if (NetworkState != NetworkConnection.State.Disconnected)
			{
				StopComplete();
			}

			JobHandle.Complete();

			if (NetworkDriver.IsCreated)
			{
				NetworkDriver.Dispose();
			}
			_QosPipelines.Dispose();

			base.Dispose();
		}

		protected new void Start()
		{
			base.Start();
		}


		/// <summary>
		/// クライアント接続停止
		/// </summary>
		public override void Stop()
		{
			base.Stop();
			if (NetworkState == NetworkConnection.State.Disconnected)
			{
				Debug.LogError("Start Failed  currentState = " + NetworkState);
				return;
			}
			JobHandle.Complete();

			Debug.Log("Stop");
		}

		// サーバーから切断されたらLinkerを破棄して停止
		protected override void StopComplete()
		{
			base.StopComplete();
			if (NetworkState == NetworkConnection.State.Disconnected)
			{
				Debug.LogError("CompleteStop Failed  currentState = " + NetworkState);
				return;
			}
			JobHandle.Complete();

			var serverConnection = new NetworkConnection(ServerConnId);
			if (serverConnection.GetState(NetworkDriver) != NetworkConnection.State.Disconnected)
			{
				serverConnection.Disconnect(NetworkDriver);
			}

			NetworkState = NetworkConnection.State.Disconnected;

			_IsFirstUpdateComplete = false;
			NetworkState = NetworkConnection.State.Disconnected;
		}

		protected override void SendToConnIdImmediately(int connId, DataStreamWriter packet, bool reliable)
		{
		}

		/// <summary>
		/// 受信パケットの受け取りなど、最初に行うべきUpdateループ
		/// </summary>
		public override void OnFirstUpdate()
		{
			JobHandle.Complete();

			if (NetworkState == NetworkConnection.State.Disconnected)
			{
				return;
			}

			_SinglePacketBuffer.Clear();
			_BroadcastRudpChunkedPacketManager.Clear();
			_BroadcastUdpChunkedPacketManager.Clear();

			NetworkConnection connId;
			DataStreamReader stream;
			NetworkEvent.Type cmd;

			while ((cmd = NetworkDriver.PopEvent(out connId, out stream)) != NetworkEvent.Type.Empty)
			{
				if (cmd == NetworkEvent.Type.Connect)
				{
					//Serverと接続完了
					ServerConnId = connId.InternalId;
					ConnectServer(connId.InternalId);
				}
				else if (cmd == NetworkEvent.Type.Disconnect)
				{
					DisconnectServer(connId.InternalId);
					return;
				}
				else if (cmd == NetworkEvent.Type.Data)
				{
					//Debug.Log($"driver.PopEvent={cmd} con={connId.InternalId} : {stream.Length}");
					if (!stream.IsCreated)
					{
						continue;
					}

					var ctx = new DataStreamReader.Context();
					byte qos = stream.ReadByte(ref ctx);
					ushort targetPlayerId = stream.ReadUShort(ref ctx);
					ushort senderPlayerId = stream.ReadUShort(ref ctx);

					GetUniqueIdByPlayerId(senderPlayerId, out ulong uniqueId);
					while (true)
					{
						int pos = stream.GetBytesRead(ref ctx);
						if (pos >= stream.Length) break;
						ushort dataLen = stream.ReadUShort(ref ctx);
						if (dataLen == 0 || pos + dataLen >= stream.Length) break;

						var chunk = stream.ReadChunk(ref ctx, dataLen);

						var ctx2 = new DataStreamReader.Context();
						byte type = chunk.ReadByte(ref ctx2);

						//if(type != (byte)BuiltInPacket.Type.MeasureRtt)
						//{
						//    var c = new DataStreamReader.Context();
						//    Debug.Log($"Dump : {string.Join(",", chunk.ReadBytesAsArray(ref c, chunk.Length))}");
						//}

						DeserializePacket(ServerConnId, uniqueId, type, ref chunk, ref ctx2);
					}
				}
			}
			_IsFirstUpdateComplete = true;
		}
		private float _PrevSendTime;

		/// <summary>
		/// まとめたパケット送信など、最後に行うべきUpdateループ
		/// </summary>
		public override void OnLastUpdate()
		{
			if (NetworkState == NetworkConnection.State.Disconnected)
			{
				return;
			}
			if (!_IsFirstUpdateComplete) return;
			
			if (NetworkState == NetworkConnection.State.Connected || NetworkState == NetworkConnection.State.AwaitingResponse)
			{
				if (Time.realtimeSinceStartup - _PrevSendTime > 1.0)
				{
					_PrevSendTime = Time.realtimeSinceStartup;
					SendMeasureLatencyPacket();
				}
			}
			_BroadcastRudpChunkedPacketManager.WriteCurrentBuffer();
			_BroadcastUdpChunkedPacketManager.WriteCurrentBuffer();

			JobHandle = ScheduleSendPacket(default);
			JobHandle = NetworkDriver.ScheduleUpdate(JobHandle);

			JobHandle.ScheduleBatchedJobs();
		}

		protected void SendMeasureLatencyPacket()
		{
			//Debug.Log("SendMeasureLatencyPacket");
			using (var packet = new DataStreamWriter(9, Allocator.Temp))
			{
				packet.Write((byte)BuiltInPacket.Type.MeasureRtt);
				packet.Write(GamePacketManager.CurrentUnixTime);

				//ServerConnection.Send(NetworkDriver, _QosPipelines[(byte)QosType.Reliable], packet);
				Send(ServerPlayerId, packet, QosType.Unreliable);
			}
		}

		protected JobHandle ScheduleSendPacket(JobHandle jobHandle)
		{
			var serverConnection = new NetworkConnection(ServerConnId);
			if (serverConnection.GetState(NetworkDriver) != NetworkConnection.State.Connected)
			{
				return default;
			}

			var sendPacketsJob = new SendPacketaJob()
			{
				driver = NetworkDriver,
				serverConnection = serverConnection,
				singlePacketBuffer = _SinglePacketBuffer,
				rudpPacketBuffer = _BroadcastRudpChunkedPacketManager.ChunkedPacketBuffer,
				udpPacketBuffer = _BroadcastUdpChunkedPacketManager.ChunkedPacketBuffer,
				qosPipelines = _QosPipelines,
				senderPlayerId = MyPlayerId,
			};

			return sendPacketsJob.Schedule(jobHandle);
		}

		struct SendPacketaJob : IJob
		{
			public Driver driver;
			[ReadOnly]
			public NetworkConnection serverConnection;

			[ReadOnly]
			public DataStreamWriter singlePacketBuffer;
			[ReadOnly]
			public DataStreamWriter rudpPacketBuffer;
			[ReadOnly]
			public DataStreamWriter udpPacketBuffer;

			public NativeArray<NetworkPipeline> qosPipelines;
			[ReadOnly]
			public ushort senderPlayerId;

			public unsafe void Execute()
			{
				var multiCastList = new NativeList<ushort>(Allocator.Temp);
				var temp = new DataStreamWriter(NetworkParameterConstants.MTU, Allocator.Temp);

				if (singlePacketBuffer.Length != 0)
				{
					var reader = new DataStreamReader(singlePacketBuffer, 0, singlePacketBuffer.Length);
					var ctx = default(DataStreamReader.Context);
					while (true)
					{
						temp.Clear();

						int pos = reader.GetBytesRead(ref ctx);
						if (pos >= reader.Length) break;

						byte qos = reader.ReadByte(ref ctx);
						ushort targetPlayerId = reader.ReadUShort(ref ctx);

						if (targetPlayerId == NetworkLinkerConstants.MulticastId)
						{
							multiCastList.Clear();
							ushort multiCastCount = reader.ReadUShort(ref ctx);
							for (int i = 0; i < multiCastCount; i++)
							{
								multiCastList.Add(reader.ReadUShort(ref ctx));
							}
						}

						ushort packetDataLen = reader.ReadUShort(ref ctx);
						if (packetDataLen == 0 || pos + packetDataLen >= reader.Length) break;

						var packet = reader.ReadChunk(ref ctx, packetDataLen);
						byte* packetPtr = packet.GetUnsafeReadOnlyPtr();

						temp.Write(qos);
						temp.Write(targetPlayerId);
						temp.Write(senderPlayerId);
						temp.Write(packetDataLen);
						temp.WriteBytes(packetPtr, packetDataLen);

						//Debug.Log("singlePacketBuffer : " + packetDataLen);
						serverConnection.Send(driver, qosPipelines[qos], temp);
						//serverConnection.Send(driver, temp);
					}
				}

				if (udpPacketBuffer.Length != 0)
				{
					var reader = new DataStreamReader(udpPacketBuffer, 0, udpPacketBuffer.Length);
					var ctx = default(DataStreamReader.Context);
					while (true)
					{
						temp.Clear();

						int pos = reader.GetBytesRead(ref ctx);
						if (pos >= reader.Length) break;

						ushort packetDataLen = reader.ReadUShort(ref ctx);
						if (packetDataLen == 0 || pos + packetDataLen >= reader.Length) break;

						var packet = reader.ReadChunk(ref ctx, packetDataLen);
						byte* packetPtr = packet.GetUnsafeReadOnlyPtr();

						//chunkはBroadcast + Unrealiableのみ
						temp.Write((byte)QosType.Unreliable);
						temp.Write(NetworkLinkerConstants.BroadcastId);
						temp.Write(senderPlayerId);
						//temp.Write(packetDataLen);
						temp.WriteBytes(packetPtr, packetDataLen);
						//Debug.Log("chunkedPacketBuffer : " + packetDataLen);
						serverConnection.Send(driver, qosPipelines[(byte)QosType.Unreliable], temp);
						//serverConnection.Send(driver, temp);
					}
				}
				if (rudpPacketBuffer.Length != 0)
				{
					var reader = new DataStreamReader(rudpPacketBuffer, 0, rudpPacketBuffer.Length);
					var ctx = default(DataStreamReader.Context);
					while (true)
					{
						temp.Clear();

						int pos = reader.GetBytesRead(ref ctx);
						if (pos >= reader.Length) break;

						ushort packetDataLen = reader.ReadUShort(ref ctx);
						if (packetDataLen == 0 || pos + packetDataLen >= reader.Length) break;

						var packet = reader.ReadChunk(ref ctx, packetDataLen);
						byte* packetPtr = packet.GetUnsafeReadOnlyPtr();

						//chunkはBroadcast + Unrealiableのみ
						temp.Write((byte)QosType.Reliable);
						temp.Write(NetworkLinkerConstants.BroadcastId);
						temp.Write(senderPlayerId);
						//temp.Write(packetDataLen);
						temp.WriteBytes(packetPtr, packetDataLen);
						//Debug.Log("chunkedPacketBuffer : " + packetDataLen);
						serverConnection.Send(driver, qosPipelines[(byte)QosType.Reliable], temp);
						//serverConnection.Send(driver, temp);
					}
				}
				temp.Dispose();
				multiCastList.Dispose();
			}
		}
	}
	
	public abstract class ClientNetworkManagerBase<ConnIdType, PlayerInfo> : GenericNetworkManagerBase<ConnIdType, PlayerInfo>
			where ConnIdType : struct, System.IEquatable<ConnIdType> where PlayerInfo : DefaultPlayerInfo, new()
	{

		public override bool IsFullMesh => false;

		public ConnIdType ServerConnId { get; protected set; }

		public ClientNetworkManagerBase(PlayerInfo playerInfo) : base(playerInfo)
		{
		}

		public override void Dispose()
		{
			if (_IsDispose) return;
			JobHandle.Complete();

			base.Dispose();
		}

		protected void Start()
		{
			IsLeader = false;
			NetworkState = NetworkConnection.State.Connecting;
		}

		/// <summary>
		/// クライアント接続停止
		/// </summary>
		public override void Stop()
		{
			base.Stop();

			if (NetworkState == NetworkConnection.State.Disconnected)
			{
				Debug.LogError("Stop Failed  currentState = " + NetworkState);
				return;
			}
			JobHandle.Complete();

			//Playerリストから削除するリクエストを送る
			SendUnregisterPlayerPacket();
		}

		// サーバーから切断されたらLinkerを破棄して停止
		protected override void StopComplete()
		{
			base.StopComplete();

			if (NetworkState == NetworkConnection.State.Disconnected)
			{
				Debug.LogError("CompleteStop Failed  currentState = " + NetworkState);
				return;
			}
			JobHandle.Complete();
		}

		protected void ConnectServer(int connId)
		{
			Debug.Log("IsConnected" + connId);

			if (NetworkState == NetworkConnection.State.AwaitingResponse)
			{
				//再接続
				//サーバーに自分のPlayerInfoを教える
				SendReconnectPlayerPacket(ServerPlayerId);
			}
			else
			{
				//サーバーに自分のPlayerInfoを教える
				SendRegisterPlayerPacket(ServerPlayerId);
			}
		}

		protected void DisconnectServer(int connId)
		{
			Debug.Log("IsDisconnected" + connId);
			if (IsStopRequest)
			{
				StopComplete();
				ExecOnDisconnectAll(0);
			}
			else
			{
				if (NetworkState == NetworkConnection.State.Connecting)
				{
					ExecOnConnectFailed(1); //TODO ErrorCodeを取得する方法を探す
					NetworkState = NetworkConnection.State.Disconnected;
				}
				else
				{
					DisconnectPlayer(MyPlayerInfo.UniqueId);
					ExecOnDisconnectAll(1);    //TODO ErrorCodeを取得する方法を探す
					NetworkState = NetworkConnection.State.AwaitingResponse;
				}
				ReconnectMethod();
			}
		}

		protected abstract void ReconnectMethod();

		protected void SendRegisterPlayerPacket(ushort targetPlayerId)
		{
			var addPlayerPacket = MyPlayerInfo.CreateUpdatePlayerInfoPacket((byte)BuiltInPacket.Type.RegisterPlayer);
			Send(targetPlayerId, addPlayerPacket, QosType.Reliable);
			addPlayerPacket.Dispose();
		}

		protected void SendReconnectPlayerPacket(ushort targetPlayerId)
		{
			var recconectPlayerPacket = MyPlayerInfo.CreateUpdatePlayerInfoPacket((byte)BuiltInPacket.Type.ReconnectPlayer);
			Send(targetPlayerId, recconectPlayerPacket, QosType.Reliable);
			recconectPlayerPacket.Dispose();
		}

		protected void SendUpdatePlayerPacket(ushort targetPlayerId)
		{
			var addPlayerPacket = MyPlayerInfo.CreateUpdatePlayerInfoPacket((byte)BuiltInPacket.Type.UpdatePlayerInfo);
			Send(targetPlayerId, addPlayerPacket, QosType.Reliable);
			addPlayerPacket.Dispose();
		}

		protected void SendUpdateAllPlayerPacket(ushort targetPlayerId)
		{
			var packet = new DataStreamWriter(NetworkLinkerConstants.MaxPacketSize, Allocator.Temp);

			packet.Write((byte)BuiltInPacket.Type.UpdatePlayerInfo);
			foreach (var pair in _ConnIdUniqueIdable)
			{
				ConnIdType connId = pair.Key;
				ulong uniqueId = pair.Value;
				GetPlayerIdByUniqueId(uniqueId, out ushort playerId);
				if (IsEquals(connId, ServerConnId)) continue;

				var connInfo = GetConnectionInfoByUniqueId(uniqueId);
				var playerInfo = GetPlayerInfoByUniqueId(uniqueId);
				if (connInfo != null && playerInfo != null && connInfo.State != NetworkConnection.State.Connecting)
				{
					packet.Write((byte)connInfo.State);
					playerInfo.AppendPlayerInfoPacket(ref packet);
				}
				else
				{
					playerInfo = new PlayerInfo() { PlayerId = playerId };
					packet.Write((byte)NetworkConnection.State.Disconnected);
					playerInfo.AppendPlayerInfoPacket(ref packet);
				}

				if (packet.Length > NetworkLinkerConstants.MaxPacketSize - playerInfo.PacketSize - 1)
				{
					Send(targetPlayerId, packet, QosType.Reliable);
					packet.Clear();
					packet.Write((byte)BuiltInPacket.Type.UpdatePlayerInfo);
				}
			}
			if (packet.Length > 1)
			{
				Send(targetPlayerId, packet, QosType.Reliable);
			}
			packet.Dispose();
		}

		protected void BroadcastUpdatePlayerPacket(ushort playerId)
		{
			var connInfo = GetConnectionInfoByPlayerId(playerId);
			var playerInfo = GetPlayerInfoByPlayerId(playerId);
			if (connInfo != null && playerInfo != null && connInfo.State != NetworkConnection.State.Connecting)
			{
				var packet = new DataStreamWriter(2 + playerInfo.PacketSize, Allocator.Temp);
				packet.Write((byte)BuiltInPacket.Type.UpdatePlayerInfo);
				packet.Write((byte)connInfo.State);
				playerInfo.AppendPlayerInfoPacket(ref packet);
				Broadcast(packet, QosType.Reliable);
				packet.Dispose();
			}
			else
			{
				playerInfo = new PlayerInfo { PlayerId = playerId };
				var packet = new DataStreamWriter(2 + playerInfo.PacketSize, Allocator.Temp);
				packet.Write((byte)BuiltInPacket.Type.UpdatePlayerInfo);
				packet.Write((byte)NetworkConnection.State.Disconnected);
				playerInfo.AppendPlayerInfoPacket(ref packet);
				Broadcast(packet, QosType.Reliable);
				packet.Dispose();
			}
		}

		protected void SendRegisterPlayerPacket(ushort id, bool isReconnect)
		{
			var registerPacket = new DataStreamWriter(14 + _ActivePlayerIdList.Count + MyPlayerInfo.PacketSize, Allocator.Temp);
			registerPacket.Write((byte)BuiltInPacket.Type.RegisterPlayer);
			registerPacket.Write(id);
			registerPacket.Write(LeaderStatTime);
			registerPacket.Write((byte)(isReconnect ? 1 : 0));

			registerPacket.Write((byte)_ActivePlayerIdList.Count);
			for (int i = 0; i < _ActivePlayerIdList.Count; i++)
			{
				registerPacket.Write(_ActivePlayerIdList[i]);
			}
			MyPlayerInfo.AppendPlayerInfoPacket(ref registerPacket);

			Debug.Log($"Send Reg {id} : {LeaderStatTime} : {isReconnect} : count={_ActivePlayerIdList.Count} : {registerPacket.Length}");

			Send(id, registerPacket, QosType.Reliable);

			registerPacket.Dispose();
		}

		protected void SendUnregisterPlayerPacket()
		{
			using (var unregisterPlayerPacket = new DataStreamWriter(9, Allocator.Temp))
			{
				unregisterPlayerPacket.Write((byte)BuiltInPacket.Type.UnregisterPlayer);
				unregisterPlayerPacket.Write(MyPlayerInfo.UniqueId);
				Send(ServerPlayerId, unregisterPlayerPacket, QosType.Reliable);
			}
		}

		protected void BroadcastStopNetworkPacket()
		{
			using (var stopNetworkPacket = new DataStreamWriter(2, Allocator.Temp))
			{
				stopNetworkPacket.Write((byte)BuiltInPacket.Type.StopNetwork);
				stopNetworkPacket.Write((byte)0);    //TODO error code

				Broadcast(stopNetworkPacket, QosType.Reliable, true);
			}
		}

		protected override bool DeserializePacket(ConnIdType connId, ulong senderUniqueId, byte type, ref DataStreamReader chunk, ref DataStreamReader.Context ctx2)
		{
			//自分宛パケットの解析
			switch (type)
			{
				case (byte)BuiltInPacket.Type.RegisterPlayer:
					NetworkState = NetworkConnection.State.Connected;
					MyPlayerInfo.PlayerId = chunk.ReadUShort(ref ctx2);

					LeaderStatTime = chunk.ReadLong(ref ctx2);
					bool isRecconnect = chunk.ReadByte(ref ctx2) != 0;

					Debug.Log($"Register ID={MyPlayerId} Time={LeaderStatTime} IsRec={isRecconnect}");

					byte count = chunk.ReadByte(ref ctx2);
					_ActivePlayerIdList.Clear();
					for (int i = 0; i < count; i++)
					{
						_ActivePlayerIdList.Add(chunk.ReadByte(ref ctx2));
					}

					var serverPlayerInfo = new PlayerInfo();
					serverPlayerInfo.Deserialize(ref chunk, ref ctx2);

					//サーバーを登録
					RegisterPlayer(serverPlayerInfo, connId, isRecconnect);
					//自分を登録
					RegisterPlayer(MyPlayerInfo as PlayerInfo, EmptyConnId, isRecconnect);

					ExecOnConnect();
					break;
				case (byte)BuiltInPacket.Type.StopNetwork:
					Stop();
					break;
				case (byte)BuiltInPacket.Type.UpdatePlayerInfo:
					{
						while (ctx2.GetReadByteIndex() + 2 < chunk.Length)
						{
							var state = (NetworkConnection.State)chunk.ReadByte(ref ctx2);
							var updatePlayerInfo = new PlayerInfo();
							updatePlayerInfo.Deserialize(ref chunk, ref ctx2);
							Debug.Log("UpdatePlayerInfo : " + state + " : " + updatePlayerInfo.PlayerId);
							if (updatePlayerInfo.PlayerId != MyPlayerId)
							{
								var connInfo = GetConnectionInfoByPlayerId(updatePlayerInfo.PlayerId);
								switch (state)
								{
									case NetworkConnection.State.Connected:
										if (connInfo != null && connInfo.State == NetworkConnection.State.AwaitingResponse)
										{
											Debug.Log("ReconnectPlayerId : " + state + " : " + updatePlayerInfo.PlayerId);
											RegisterPlayer(updatePlayerInfo, EmptyConnId, true);
										}
										else if (!(connInfo != null && connInfo.State == NetworkConnection.State.Connected))
										{
											Debug.Log("RegisterPlayerId : " + state + " : " + updatePlayerInfo.PlayerId);
											RegisterPlayer(updatePlayerInfo, EmptyConnId, false);
										}
										break;
									case NetworkConnection.State.Disconnected:
										if (connInfo != null && connInfo.State != NetworkConnection.State.Disconnected)
										{
											Debug.Log("UnregisterPlayerId : " + state + " : " + updatePlayerInfo.PlayerId);
											if (GetUniqueIdByPlayerId(updatePlayerInfo.PlayerId, out ulong updateUniqueId))
											{
												UnregisterPlayer(updateUniqueId);
											}
										}
										break;
									case NetworkConnection.State.AwaitingResponse:
										if (connInfo != null && connInfo.State != NetworkConnection.State.AwaitingResponse)
										{
											Debug.Log("DisconnectPlayerId : " + state + " : " + updatePlayerInfo.PlayerId);
											DisconnectPlayer(updatePlayerInfo.UniqueId);
										}
										break;
								}
							}
						}
					}
					break;
				default:
					if (GetPlayerIdByUniqueId(senderUniqueId, out var senderPlayerId))
					{
						RecieveData(senderPlayerId, senderUniqueId, type, chunk, ctx2);
					}
					break;
			}
			return false;
		}
	}
}
