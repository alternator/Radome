using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Profiling;

namespace ICKX.Radome
{

	public class UDPServerNetworkManager<PlayerInfo> : ServerNetworkManager<UDPNetworkInterface, PlayerInfo> where PlayerInfo : DefaultPlayerInfo, new()
	{
		private NetworkConfigParameter Config;

		public UDPServerNetworkManager(PlayerInfo playerInfo) : base(playerInfo)
		{
			Config = new NetworkConfigParameter()
			{
				connectTimeoutMS = 1000 * 5,
				disconnectTimeoutMS = 1000 * 5,
			};
		}

		public UDPServerNetworkManager(PlayerInfo playerInfo, NetworkConfigParameter config) : base(playerInfo)
		{
			Config = config;
		}

		/// <summary>
		/// サーバー起動
		/// </summary>
		public void Start(int port)
		{
			if (NetworkState != NetworkConnection.State.Disconnected)
			{
				Debug.LogError("Start Failed  currentState = " + NetworkState);
				return;
			}

			if (!NetworkDriver.IsCreated)
			{
				NetworkDriver = new NetworkDriver(new UDPNetworkInterface(), new INetworkParameter[] {
					Config,
					new ReliableUtility.Parameters { WindowSize = 32 },
					new NetworkPipelineParams {initialCapacity = ushort.MaxValue},
                    //new SimulatorUtility.Parameters {MaxPacketSize = 256, MaxPacketCount = 32, PacketDelayMs = 100},
                });
			}

			_QosPipelines[(int)QosType.Empty] = NetworkDriver.CreatePipeline();
			//_QosPipelines[(int)QosType.Reliable] = NetworkDriver.CreatePipeline();
			//_QosPipelines[(int)QosType.Unreliable] = NetworkDriver.CreatePipeline();

			_QosPipelines[(int)QosType.Reliable] = NetworkDriver.CreatePipeline(typeof(ReliableSequencedPipelineStage));
			_QosPipelines[(int)QosType.Unreliable] = NetworkDriver.CreatePipeline();
			//_QosPipelines[(int)QosType.Reliable] = NetworkDriver.CreatePipeline(typeof(SimulatorPipelineStage));
			//_QosPipelines[(int)QosType.Unreliable] = NetworkDriver.CreatePipeline(typeof(SimulatorPipelineStage));

			var endPoint = NetworkEndPoint.AnyIpv4;
			endPoint.Port = (ushort)port;
			if (NetworkDriver.Bind(endPoint) != 0)
			{
				Debug.Log("Failed to bind to port");
				ExecOnConnectFailed(1);
			}
			else
			{
				NetworkDriver.Listen();
				ExecOnConnect();
				Debug.Log("Listen");
			}

			Start();
		}

		protected override void DisconnectMethod(int connId)
		{
			NetworkDriver.Disconnect(_NetworkConnections[connId]);
		}
	}

	/// <summary>
	/// サーバー用のNetworkManager
	/// 通信の手順は
	/// 
	/// Server.Send  -> Chunk化 -> Pipline -> con.Send -> Pop -> Pipline -> Chunk解除 -> Recieve
	/// </summary>
	/// <typeparam name="Driver"></typeparam>
	/// <typeparam name="PlayerInfo"></typeparam>
	public abstract class ServerNetworkManager<NetInterface, PlayerInfo> : ServerNetworkManagerBase<int, PlayerInfo>
			where NetInterface : struct, INetworkInterface where PlayerInfo : DefaultPlayerInfo, new()
	{

		protected override int EmptyConnId => -1;

		public float registrationTimeOut { get; set; } = 60.0f;

		public NetworkDriver NetworkDriver;
		protected NativeArray<NetworkPipeline> _QosPipelines;

		protected NativeList<NetworkConnection> _ConnectConnIdList;
		protected NativeList<NetworkConnection> _DisconnectConnIdList;

		protected NativeArray<byte> _RecieveBuffer;
		protected NativeArray<byte> _RelayBuffer;
		protected NativeStreamWriter _RecieveBufferWriter;
		protected NativeList<DataPacket> _RecieveDataStream;

		protected NativeList<NetworkConnection> _NetworkConnections;

		private bool _IsFirstUpdateComplete = false;

		public ServerNetworkManager(PlayerInfo playerInfo) : base(playerInfo)
		{
			_NetworkConnections = new NativeList<NetworkConnection>(4, Allocator.Persistent);
			_ConnectConnIdList = new NativeList<NetworkConnection>(4, Allocator.Persistent);
			_DisconnectConnIdList = new NativeList<NetworkConnection>(4, Allocator.Persistent);
			_RecieveBuffer = new NativeArray<byte>(4096, Allocator.Persistent);
			_RelayBuffer = new NativeArray<byte>(NetworkParameterConstants.MTU, Allocator.Persistent);
			_RecieveBufferWriter = new NativeStreamWriter(_RecieveBuffer);
			//_RecieveDataStream = new NativeMultiHashMap<int, NativeStreamReader>(32, Allocator.Persistent);
			_RecieveDataStream = new NativeList<DataPacket>(32, Allocator.Persistent);

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

			_NetworkConnections.Dispose();
			_ConnectConnIdList.Dispose();
			_DisconnectConnIdList.Dispose();
			_RecieveBuffer.Dispose();
			_RelayBuffer.Dispose();
			_RecieveDataStream.Dispose();

			if (NetworkDriver.IsCreated)
			{
				NetworkDriver.Dispose();
			}
			_QosPipelines.Dispose();

			base.Dispose();
		}

		/// <summary>
		/// サーバー停止
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

			//すべてのPlayerに停止を伝えてからサーバーも停止
			if (GetPlayerCount() == 1)
			{
				StopComplete();
			}
			else
			{
				BroadcastStopNetworkPacket();
				Debug.Log("Stop");
			}
		}

		// すべてのClientが切断したら呼ぶ
		protected override void StopComplete()
		{
			base.StopComplete();
			if (NetworkState == NetworkConnection.State.Disconnected)
			{
				Debug.LogError("CompleteStop Failed  currentState = " + NetworkState);
				return;
			}
			JobHandle.Complete();

			_NetworkConnections.Clear();
			_ConnectConnIdList.Clear();
			_DisconnectConnIdList.Clear();
			_RecieveBufferWriter.Clear();
			_RecieveDataStream.Clear();

			NetworkState = NetworkConnection.State.Disconnected;

			_IsFirstUpdateComplete = false;
			NetworkState = NetworkConnection.State.Disconnected;
		}

		protected override void SendToConnIdImmediately(int connId, NativeStreamWriter packet, bool reliable)
		{
		}

		/// <summary>
		/// 受信パケットの受け取りなど、最初に行うべきUpdateループ
		/// </summary>
		public override void OnFirstUpdate()
		{
			if (NetworkState == NetworkConnection.State.Disconnected)
			{
				return;
			}

			//job完了待ち
			JobHandle.Complete();

			_SinglePacketBufferWriter.Clear();
			_BroadcastRudpChunkedPacketManager.Clear();
			_BroadcastUdpChunkedPacketManager.Clear();

			//接続確認
			NetworkConnection connection;
			while ((connection = NetworkDriver.Accept()) != default)
			{
				while (connection.InternalId >= _NetworkConnections.Length) _NetworkConnections.Add(default);
				_NetworkConnections[connection.InternalId] = connection;
				Debug.Log("Accepted a connection =" + connection.InternalId + " : " + connection.GetHashCode());
			}
			CheckTimeOut(registrationTimeOut);

			for (ushort i = 0; i < _DisconnectConnIdList.Length; i++)
			{
				OnDisconnectMethod(_DisconnectConnIdList[i].InternalId);
			}

			for (int i = 0; i < _RecieveDataStream.Length; i++)
			{
				connection = _RecieveDataStream[i].Connection;
				var chunk = _RecieveDataStream[i].Chunk;
				byte type = chunk.ReadByte();
				//Debug.Log($"TYPE{type}, chunk= {chunk.Length} con {connection.InternalId}");
				GetUniqueIdByConnId(connection.InternalId, out ulong uniqueId);
				DeserializePacket(connection.InternalId, uniqueId, type, chunk);
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

			CollectConnIdTable();

			//main thread処理
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
			_RecieveDataStream.Clear();

			JobHandle = ScheduleSendPacket(default);
			JobHandle = NetworkDriver.ScheduleUpdate(JobHandle);
			JobHandle = ScheduleRecieve(JobHandle);

			JobHandle.ScheduleBatchedJobs();
		}

		protected void SendMeasureLatencyPacket()
		{
			//Debug.Log("SendMeasureLatencyPacket");
			using (var array = new NativeArray<byte>(9, Allocator.Temp))
			{
				var packet = new NativeStreamWriter(array);
				packet.WriteByte((byte)BuiltInPacket.Type.MeasureRtt);
				packet.WriteLong(GamePacketManager.CurrentUnixTime);

				Broadcast(packet, QosType.Unreliable, true);
			}
		}

		protected JobHandle ScheduleSendPacket(JobHandle jobHandle)
		{
			var sendPacketsJob = new SendPacketaJob()
			{
				driver = NetworkDriver,
				connections = _ConnectionIdList,
				networkConnections = _NetworkConnections,
				singlePacketBuffer = _SinglePacketBufferWriter,
				rudpPacketBuffer = _BroadcastRudpChunkedPacketManager.ChunkedPacketBufferWriter,
				udpPacketBuffer = _BroadcastUdpChunkedPacketManager.ChunkedPacketBufferWriter,
				qosPipelines = _QosPipelines,
				serverPlayerId = ServerPlayerId,
			};

			return sendPacketsJob.Schedule(jobHandle);
		}

		protected JobHandle ScheduleRecieve(JobHandle jobHandle)
		{
			var recievePacketJob = new RecievePacketJob()
			{
				driver = NetworkDriver,
				connections = _ConnectionIdList,
				networkConnections = _NetworkConnections,
				qosPipelines = _QosPipelines,
				connectConnIdList = _ConnectConnIdList,
				disconnectConnIdList = _DisconnectConnIdList,
				recieveBuffer = _RecieveBufferWriter,
				relayBuffer = _RelayBuffer,
				recieveDataStream = _RecieveDataStream,
				serverPlayerId = ServerPlayerId,
			};
			return recievePacketJob.Schedule(jobHandle);
		}
		
		struct SendPacketaJob : IJob
		{
			public NetworkDriver driver;
			[ReadOnly]
			public NativeList<int> connections;		//Key : PlayerId
			[ReadOnly]
			public NativeList<NetworkConnection> networkConnections;	//Key : connectionId

			[ReadOnly]
			[NativeDisableUnsafePtrRestriction]
			public NativeStreamWriter singlePacketBuffer;
			[ReadOnly]
			[NativeDisableUnsafePtrRestriction]
			public NativeStreamWriter rudpPacketBuffer;
			[ReadOnly]
			[NativeDisableUnsafePtrRestriction]
			public NativeStreamWriter udpPacketBuffer;

			[ReadOnly]
			public NativeArray<NetworkPipeline> qosPipelines;

			[ReadOnly]
			public ushort serverPlayerId;

			bool IsConnected (ushort playerId)
			{
				if (playerId == serverPlayerId) return false;
				if (playerId >= connections.Length) return false;
				if (connections[playerId] == -1 || connections[playerId] >= networkConnections.Length) return false;
				return true;
			}

			unsafe void SendPacket (ushort playerId, byte qos, ref NativeStreamWriter writer)
			{
				var connection = networkConnections[connections[playerId]];
				var sendWriter = driver.BeginSend(qosPipelines[qos], connection);
				sendWriter.WriteBytes(writer.GetUnsafePtr(), writer.Length);
				driver.EndSend(sendWriter);
				//Debug.Log($"playerId{playerId} : {connection.InternalId} : qos{qos} : Len{writer.Length}");
			}

			public unsafe void Execute()
			{
				var multiCastList = new NativeList<ushort>(Allocator.Temp);
				var tempArray = new NativeArray<byte>(NetworkParameterConstants.MTU, Allocator.Temp);
				var tempWriter = new NativeStreamWriter(tempArray);

				if (singlePacketBuffer.Length != 0)
				{
					var reader = new NativeStreamReader(singlePacketBuffer, 0, singlePacketBuffer.Length);
					while(true)
					{
						if (!CreateSingleSendPacket(ref tempWriter, out ushort targetPlayerId, out byte qos, ref reader, multiCastList, serverPlayerId)) break;

						//Debug.Log($"conLen{ connections.Length} ; tempWriter{tempWriter.Length} : targetPlayerId{targetPlayerId} : qos{qos} : Len{reader.GetBytesRead()}");
						if (targetPlayerId == NetworkLinkerConstants.BroadcastId)
						{
							for (ushort i = 0; i < connections.Length; i++)
							{
								if (IsConnected(i)) SendPacket(i, qos, ref tempWriter);
							}
						}
						else if (targetPlayerId == NetworkLinkerConstants.MulticastId)
						{
							for (ushort i = 0; i < multiCastList.Length; i++)
							{
								if (IsConnected(multiCastList[i])) SendPacket(multiCastList[i], qos, ref tempWriter);
							}
						}
						else
						{
							if (IsConnected(targetPlayerId)) SendPacket(targetPlayerId, qos, ref tempWriter);
						}
					}
				}

				if (udpPacketBuffer.Length > 2)
				{
					var reader = new NativeStreamReader(udpPacketBuffer, 0, udpPacketBuffer.Length);
					while (true)
					{
						if (!CreateChunkSendPacket(ref tempWriter, ref reader, multiCastList, serverPlayerId, (byte)QosType.Unreliable)) break;
						//Debug.Log($"udpPacketBuffer conLen{ connections.Length} ; tempWriter{tempWriter.Length} :  Len{reader.GetBytesRead()}");

						for (ushort i = 0; i < connections.Length; i++)
						{
							if (IsConnected(i)) SendPacket(i, (byte)QosType.Unreliable, ref tempWriter);
						}
					}
				}
				if (rudpPacketBuffer.Length > 2)
				{
					var reader = new NativeStreamReader(rudpPacketBuffer, 0, rudpPacketBuffer.Length);
					while (true)
					{
						if (!CreateChunkSendPacket(ref tempWriter, ref reader, multiCastList, serverPlayerId, (byte)QosType.Reliable)) break;
						//Debug.Log($"rudpPacketBuffer conLen{ connections.Length} ; tempWriter{tempWriter.Length} :  Len{reader.GetBytesRead()}");

						for (ushort i = 0; i < connections.Length; i++)
						{
							if (IsConnected(i)) SendPacket(i, (byte)QosType.Reliable, ref tempWriter);
						}
					}
				}
				tempArray.Dispose();
				multiCastList.Dispose();
			}
		}

		struct RecievePacketJob : IJob
		{
			public NetworkDriver driver;
			[ReadOnly]
			public NativeList<int> connections;
			[ReadOnly]
			public NativeList<NetworkConnection> networkConnections;
			[ReadOnly]
			public NativeArray<NetworkPipeline> qosPipelines;

			public NativeList<NetworkConnection> connectConnIdList;
			public NativeList<NetworkConnection> disconnectConnIdList;

			public NativeStreamWriter recieveBuffer;

			public NativeArray<byte> relayBuffer;
			//public NativeMultiHashMap<int, NativeStreamReader> dataStream;
			public NativeList<DataPacket> recieveDataStream;
			[ReadOnly]
			public ushort serverPlayerId;

			public unsafe void Execute()
			{
				var multiCastList = new NativeList<ushort>(Allocator.Temp);
				connectConnIdList.Clear();
				disconnectConnIdList.Clear();

				NetworkConnection con;
				//NativeStreamReader stream;
				NetworkEvent.Type cmd;

				while ((cmd = driver.PopEvent(out con, out var dataStream)) != NetworkEvent.Type.Empty)
				{
					if (cmd == NetworkEvent.Type.Connect)
					{
						Debug.Log($"NetworkEvent.Type.Connect con={con.InternalId}");
						connectConnIdList.Add(con);
					}
					else if (cmd == NetworkEvent.Type.Disconnect)
					{
						Debug.Log($"NetworkEvent.Type.Disconnect con={con.InternalId}");
						disconnectConnIdList.Add(con);
					}
					else if (cmd == NetworkEvent.Type.Data)
					{
						if (!dataStream.IsCreated) continue;
						//Debug.Log($"driver.PopEvent={cmd} con={con.InternalId} : {dataStream.Length}");

						byte* buffer = (byte*)relayBuffer.GetUnsafePtr();
						dataStream.ReadBytes(buffer, dataStream.Length);
						//recieveBuffer.WriteBytes(buffer, dataStream.Length);
						var stream = new NativeStreamReader(relayBuffer, 0, dataStream.Length);

						RecieveClientStream(SendMethod, con, stream, multiCastList, serverPlayerId, connections, ref recieveBuffer, ref recieveDataStream);
					}
				}
				multiCastList.Dispose();
			}
			
			private unsafe void SendMethod(byte qos, ushort targetPlayerId, NativeStreamReader packet)
			{
				var connection = networkConnections[connections[targetPlayerId]];

				var sendWriter = driver.BeginSend(qosPipelines[qos], connection);
				sendWriter.WriteBytes(packet.GetUnsafePtr(), packet.Length);
				driver.EndSend(sendWriter);
			}
		}
	}


	public abstract class ServerNetworkManagerBase<ConnIdType, PlayerInfo> : GenericNetworkManagerBase<ConnIdType, PlayerInfo>
			where ConnIdType : struct, System.IEquatable<ConnIdType> where PlayerInfo : DefaultPlayerInfo, new()
	{
		public struct DataPacket
		{
			public ushort SenderPlayerId;
			public NetworkConnection Connection;
			public NativeStreamReader Chunk;
		}

		public override bool IsFullMesh => false;

		//public ConnIdType HostConnId { get; protected set; }

		protected NativeList<ConnIdType> _ConnectionIdList;
		protected NativeHashMap<ConnIdType, ushort> _ConnectionIdPlayerIdTable;

		public ServerNetworkManagerBase(PlayerInfo playerInfo) : base(playerInfo)
		{
			_ConnectionIdList = new NativeList<ConnIdType>(16, Allocator.Persistent);
			_ConnectionIdPlayerIdTable = new NativeHashMap<ConnIdType, ushort>(16, Allocator.Persistent);
		}

		public override void Dispose()
		{
			if (_IsDispose) return;
			JobHandle.Complete();

			_ConnectionIdList.Dispose();
			_ConnectionIdPlayerIdTable.Dispose();

			base.Dispose();
		}

		protected void Start()
		{
			IsLeader = true;
			Debug.Log("start " + SystemInfo.deviceUniqueIdentifier);
			LeaderStatTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			NetworkState = NetworkConnection.State.Connected;
			MyPlayerInfo.PlayerId = ServerPlayerId;
			RegisterPlayer(MyPlayerInfo as PlayerInfo, EmptyConnId, false);
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

			//すべてのPlayerに停止を伝えてからサーバーも停止
			if (GetPlayerCount() == 1)
			{
				StopComplete();
			}
			else
			{
				BroadcastStopNetworkPacket();
				Debug.Log("Stop");
			}
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

			_ConnectionIdList.Clear();
			_ConnectionIdPlayerIdTable.Clear();
		}

		/// <summary>
		/// 他のClientと切断時に実行する
		/// </summary>
		/// <param name="connId"></param>
		protected void OnDisconnectMethod(ConnIdType connId)
		{
			if (GetPlayerIdByConnId(connId, out ushort playerId))
			{
				if (GetUniqueIdByPlayerId(playerId, out ulong uniqueId))
				{
					var connInfo = GetConnectionInfoByUniqueId(uniqueId);
					if (connInfo != null && connInfo.State != NetworkConnection.State.AwaitingResponse)
					{
						DisconnectPlayer(uniqueId);
						BroadcastUpdatePlayerPacket(playerId);

						if (GetPlayerCount() >= 2)
						{
							bool isAllDisconnect = _ActiveConnectionInfoTable.Values
								.Where(info => info != null && !IsEquals(info.ConnId, EmptyConnId))
								.All(info => info.State == NetworkConnection.State.AwaitingResponse);

							if (isAllDisconnect)
							{
								ExecOnDisconnectAll(1);
							}
						}
					}
				}
			}
		}

		protected void OnStartHostMethod()
		{
			if (IsLeader)
			{
				NetworkState = NetworkConnection.State.Connected;
				RegisterPlayer(MyPlayerInfo as PlayerInfo, EmptyConnId, false);
				ExecOnConnect();
			}
		}

		protected void SendRegisterPlayerPacket(ushort targetPlayerId)
		{
			var addPlayerPacket = MyPlayerInfo.CreateUpdatePlayerInfoPacket((byte)BuiltInPacket.Type.RegisterPlayer);
			Send(targetPlayerId, addPlayerPacket, QosType.Reliable);
		}

		protected void SendReconnectPlayerPacket(ushort targetPlayerId)
		{
			var recconectPlayerPacket = MyPlayerInfo.CreateUpdatePlayerInfoPacket((byte)BuiltInPacket.Type.ReconnectPlayer);
			Send(targetPlayerId, recconectPlayerPacket, QosType.Reliable);
		}

		protected void SendUpdatePlayerPacket(ushort targetPlayerId)
		{
			var addPlayerPacket = MyPlayerInfo.CreateUpdatePlayerInfoPacket((byte)BuiltInPacket.Type.UpdatePlayerInfo);
			Send(targetPlayerId, addPlayerPacket, QosType.Reliable);
		}

		protected void SendUpdateAllPlayerPacket(ushort targetPlayerId)
		{
			var packet = new NativeStreamWriter(NetworkLinkerConstants.MaxPacketSize, Allocator.Temp);

			packet.WriteByte((byte)BuiltInPacket.Type.UpdatePlayerInfo);
			foreach (var pair in _ConnIdUniqueIdable)
			{
				ConnIdType connId = pair.Key;
				ulong uniqueId = pair.Value;
				GetPlayerIdByUniqueId(uniqueId, out ushort playerId);
				if (IsEquals(connId, EmptyConnId)) continue;

				var connInfo = GetConnectionInfoByUniqueId(uniqueId);
				var playerInfo = GetPlayerInfoByUniqueId(uniqueId);
				if (connInfo != null && playerInfo != null && connInfo.State != NetworkConnection.State.Connecting)
				{
					packet.WriteByte((byte)connInfo.State);
					playerInfo.AppendPlayerInfoPacket(ref packet);
				}
				else
				{
					playerInfo = new PlayerInfo() { PlayerId = playerId };
					packet.WriteByte((byte)NetworkConnection.State.Disconnected);
					playerInfo.AppendPlayerInfoPacket(ref packet);
				}

				if (packet.Length > NetworkLinkerConstants.MaxPacketSize - playerInfo.PacketSize - 1)
				{
					Send(targetPlayerId, packet, QosType.Reliable);
					packet.Clear();
					packet.WriteByte((byte)BuiltInPacket.Type.UpdatePlayerInfo);
				}
			}
			if (packet.Length > 1)
			{
				Send(targetPlayerId, packet, QosType.Reliable);
			}
		}

		protected void BroadcastUpdatePlayerPacket(ushort playerId)
		{
			var connInfo = GetConnectionInfoByPlayerId(playerId);
			var playerInfo = GetPlayerInfoByPlayerId(playerId);
			if (connInfo != null && playerInfo != null && connInfo.State != NetworkConnection.State.Connecting)
			{
				var packet = new NativeStreamWriter(2 + playerInfo.PacketSize, Allocator.Temp);
				packet.WriteByte((byte)BuiltInPacket.Type.UpdatePlayerInfo);
				packet.WriteByte((byte)connInfo.State);
				playerInfo.AppendPlayerInfoPacket(ref packet);
				Broadcast(packet, QosType.Reliable, true);
			}
			else
			{
				playerInfo = new PlayerInfo { PlayerId = playerId };
				var packet = new NativeStreamWriter(2 + playerInfo.PacketSize, Allocator.Temp);
				packet.WriteByte((byte)BuiltInPacket.Type.UpdatePlayerInfo);
				packet.WriteByte((byte)NetworkConnection.State.Disconnected);
				playerInfo.AppendPlayerInfoPacket(ref packet);
				Broadcast(packet, QosType.Reliable, true);
			}
		}

		protected void SendRegisterPlayerPacket(ushort id, bool isReconnect)
		{
			var registerPacket = new NativeStreamWriter(14 + _ActivePlayerIdList.Count + MyPlayerInfo.PacketSize, Allocator.Temp);
			registerPacket.WriteByte((byte)BuiltInPacket.Type.RegisterPlayer);
			registerPacket.WriteUShort(id);
			registerPacket.WriteLong(LeaderStatTime);
			registerPacket.WriteByte((byte)(isReconnect ? 1 : 0));

			registerPacket.WriteByte((byte)_ActivePlayerIdList.Count);
			for (int i = 0; i < _ActivePlayerIdList.Count; i++)
			{
				registerPacket.WriteByte(_ActivePlayerIdList[i]);
			}
			MyPlayerInfo.AppendPlayerInfoPacket(ref registerPacket);

			//Debug.Log($"Send Reg {id} : {LeaderStatTime} : {isReconnect} : count={_ActivePlayerIdList.Count} : {registerPacket.Length}");

			Send(id, registerPacket, QosType.Reliable);
		}

		protected void SendUnregisterPlayerPacket()
		{
			using (var array = new NativeArray<byte>(9, Allocator.Temp))
			{
				var unregisterPlayerPacket = new NativeStreamWriter(array);
				unregisterPlayerPacket.WriteByte((byte)BuiltInPacket.Type.UnregisterPlayer);
				unregisterPlayerPacket.WriteULong(MyPlayerInfo.UniqueId);
				Send(ServerPlayerId, unregisterPlayerPacket, QosType.Reliable);
			}
		}

		protected void BroadcastStopNetworkPacket()
		{
			using (var array = new NativeArray<byte>(2, Allocator.Temp))
			{
				var stopNetworkPacket = new NativeStreamWriter(array);
				stopNetworkPacket.WriteByte((byte)BuiltInPacket.Type.StopNetwork);
				stopNetworkPacket.WriteByte((byte)0);    //TODO error code

				Broadcast(stopNetworkPacket, QosType.Reliable, true);
			}
		}

		/// <summary>
		/// ConnIdをNativeListに集める
		/// </summary>
		protected void CollectConnIdTable()
		{
			_ConnectionIdList.Clear();
			_ConnectionIdPlayerIdTable.Clear();

			foreach (var connId in _ConnIdUniqueIdable.Keys)
			{
				if (GetPlayerIdByConnId(connId, out ushort playerId))
				{
					while (playerId >= _ConnectionIdList.Length) _ConnectionIdList.Add(EmptyConnId);
					_ConnectionIdList[playerId] = connId;
					_ConnectionIdPlayerIdTable.TryAdd(connId, playerId);
				}
			}
		}


		protected static unsafe bool CreateSingleSendPacket(ref NativeStreamWriter writer, out ushort targetPlayerId, out byte qos, ref NativeStreamReader reader, NativeList<ushort> multiCastList, ushort serverPlayerId)
		{
			targetPlayerId = 0;
			qos = 0;

			writer.Clear();

			int pos = reader.GetBytesRead();
			if (pos >= reader.Length) return false;

			qos = reader.ReadByte();
			targetPlayerId = reader.ReadUShort();

			//Debug.Log($"CreateSingleSendPacket {(QosType)qos} {targetPlayerId} writer {writer.Length}");
			if (targetPlayerId == NetworkLinkerConstants.MulticastId)
			{
				multiCastList.Clear();
				ushort multiCastCount = reader.ReadUShort();
				for (int i = 0; i < multiCastCount; i++)
				{
					multiCastList.Add(reader.ReadUShort());
				}
				//Debug.Log($"{(QosType)qos} {string.Join(",", multiCastList.ToArray())}");
			}

			ushort packetDataLen = reader.ReadUShort();
			if (packetDataLen == 0 || pos + packetDataLen >= reader.Length) return false;

			var packet = reader.ReadChunk(packetDataLen);
			byte* packetPtr = packet.GetUnsafePtr();

			writer.WriteByte(qos);
			writer.WriteUShort(targetPlayerId);

			if (targetPlayerId == NetworkLinkerConstants.MulticastId)
			{
				writer.WriteUShort((ushort)multiCastList.Length);
				for (ushort i = 0; i < multiCastList.Length; i++)
				{
					writer.WriteUShort((ushort)multiCastList[i]);
				}
			}

			writer.WriteUShort(serverPlayerId);
			writer.WriteUShort(packetDataLen);
			writer.WriteBytes(packetPtr, packetDataLen);
			return true;
		}
		
		protected static unsafe bool CreateChunkSendPacket(ref NativeStreamWriter writer, ref NativeStreamReader reader, NativeList<ushort> multiCastList, ushort serverPlayerId, byte qos)
		{
			writer.Clear();

			int pos = reader.GetBytesRead();
			if (pos >= reader.Length) return false;

			ushort packetDataLen = reader.ReadUShort();
			if (packetDataLen == 0 || pos + packetDataLen >= reader.Length) return false;

			var packet = reader.ReadChunk(packetDataLen);
			byte* packetPtr = packet.GetUnsafePtr();

			//chunkはBroadcast + Unrealiableのみ
			writer.WriteByte(qos);
			writer.WriteUShort(NetworkLinkerConstants.BroadcastId);
			writer.WriteUShort(serverPlayerId);
			//temp.Write(packetDataLen);
			writer.WriteBytes(packetPtr, packetDataLen);
			return true;
		}

		protected static unsafe bool RecieveClientStream(System.Action<byte, ushort, NativeStreamReader> sendMethod, NetworkConnection con, NativeStreamReader stream
		, NativeList<ushort> multiCastList, ushort serverPlayerId, NativeList<int> connections, ref NativeStreamWriter recieveBuffer, ref NativeList<DataPacket> dataStream)
		{
			var replayPackat = stream;
			byte qos = stream.ReadByte();
			ushort targetPlayerId = stream.ReadUShort();

			if (targetPlayerId == NetworkLinkerConstants.MulticastId)
			{
				multiCastList.Clear();
				ushort multiCastCount = stream.ReadUShort();
				for (int i = 0; i < multiCastCount; i++)
				{
					multiCastList.Add(stream.ReadUShort());
				}
			}

			ushort senderPlayerId = stream.ReadUShort();

			//Debug.Log($"qos={qos} : targetPlayerId{targetPlayerId} senderPlayerId{senderPlayerId}");

			if (targetPlayerId == NetworkLinkerConstants.BroadcastId)
			{
				for (ushort i = 0; i < connections.Length; i++)
				{
					if (i == serverPlayerId) continue;
					if (connections[i] != -1 && senderPlayerId != i)
					{
						replayPackat.SetPosition(0);
						sendMethod(qos, targetPlayerId, replayPackat);
					}
				}
				PurgeChunk(senderPlayerId, con, ref stream, ref recieveBuffer, ref dataStream);
			}
			else if (targetPlayerId == NetworkLinkerConstants.MulticastId)
			{
				for (int i = 0; i < multiCastList.Length; i++)
				{
					if (multiCastList[i] == serverPlayerId)
					{
						//Debug.Log("recieve multiCast Server : " + multiCastList[i] + " / " + senderPlayerId);
						PurgeChunk(senderPlayerId, con, ref stream, ref recieveBuffer, ref dataStream);
					}
					else
					{
						//Debug.Log("recieve multiCastList : " + multiCastList[i] + " / " + connections[multiCastList[i]]);
						if (senderPlayerId != multiCastList[i])
						{
							replayPackat.SetPosition(0);
							sendMethod(qos, targetPlayerId, replayPackat);
						}
					}
				}
			}
			else
			{
				if (targetPlayerId == serverPlayerId)
				{
					PurgeChunk(senderPlayerId, con, ref stream, ref recieveBuffer, ref dataStream);
				}
				else
				{
					replayPackat.SetPosition(0);
					sendMethod(qos, targetPlayerId, replayPackat);
				}
			}
			return true;
		}

		private static unsafe void PurgeChunk(ushort senderPlayerId, NetworkConnection con
			, ref NativeStreamReader stream, ref NativeStreamWriter recieveBuffer, ref NativeList<DataPacket> dataStream)
		{
			while (true)
			{
				int pos = stream.GetBytesRead();
				if (pos >= stream.Length) break;
				ushort dataLen = stream.ReadUShort();
				
				if (dataLen == 0 || pos + dataLen >= stream.Length) break;
				
				//recieveBufferに1フレーム分のパケットをバッファリングしていく
				int startIndex = recieveBuffer.Length;
				if (recieveBuffer.Length + dataLen > recieveBuffer.Capacity)
				{
					Debug.LogError("RecieveBuffer Capacity over !! Capacity = " + recieveBuffer.Capacity);
					break;
				}
				var tempChunk = stream.ReadChunk(dataLen);
				recieveBuffer.WriteBytes(tempChunk.GetUnsafePtr(), dataLen);
				var chunk = new NativeStreamReader(recieveBuffer, startIndex, dataLen);

				dataStream.Add(new DataPacket() { SenderPlayerId = senderPlayerId, Connection = con, Chunk = chunk });
			}
		}

		protected abstract void DisconnectMethod(ConnIdType connId);
		
		protected override bool DeserializePacket(ConnIdType connId, ulong uniqueId, byte type, NativeStreamReader chunk)
		{
			//Debug.Log($"DeserializePacket : {uniqueId} : {(BuiltInPacket.Type)type} {chunk.Length}");
			switch (type)
			{
				case (byte)BuiltInPacket.Type.MeasureRtt:
					break;
				case (byte)BuiltInPacket.Type.RegisterPlayer:
					{
						var addPlayerInfo = new PlayerInfo();
						addPlayerInfo.Deserialize(ref chunk);

						var connInfo = GetConnectionInfoByUniqueId(addPlayerInfo.UniqueId);
						if (connInfo == null || connInfo.State != NetworkConnection.State.Connected)
						{
							bool isReconnect = GetPlayerIdByUniqueId(addPlayerInfo.UniqueId, out ushort newPlayerId);

							if (!isReconnect)
							{
								newPlayerId = GetDeactivePlayerId();
							}

							addPlayerInfo.PlayerId = newPlayerId;

							Debug.Log($"Register newID={newPlayerId} UniqueId={addPlayerInfo.UniqueId} IsRec={isReconnect}");

							if (isReconnect)
							{
								RegisterPlayer(addPlayerInfo, connId, true);
							}
							else
							{
								RegisterPlayer(addPlayerInfo, connId, false);
							}
							SendRegisterPlayerPacket(newPlayerId, isReconnect);
							BroadcastUpdatePlayerPacket(addPlayerInfo.PlayerId);

							SendUpdateAllPlayerPacket(newPlayerId);

							NetworkState = NetworkConnection.State.Connected;
						}
					}
					break;
				case (byte)BuiltInPacket.Type.ReconnectPlayer:
					{
						var reconnectPlayerInfo = new PlayerInfo();
						reconnectPlayerInfo.Deserialize(ref chunk);
						if (GetPlayerIdByUniqueId(reconnectPlayerInfo.UniqueId, out ushort playerId) && playerId == reconnectPlayerInfo.PlayerId)
						{
							var connInfo = GetConnectionInfoByUniqueId(reconnectPlayerInfo.UniqueId);
							if (connInfo == null || connInfo.State != NetworkConnection.State.Connected)
							{
								RegisterPlayer(reconnectPlayerInfo, connId, true);
								BroadcastUpdatePlayerPacket(reconnectPlayerInfo.PlayerId);
								SendUpdateAllPlayerPacket(playerId);
							}
						}
					}
					break;
				case (byte)BuiltInPacket.Type.UnregisterPlayer:
					//登録解除リクエスト
					{
						ulong unregisterUniqueId = chunk.ReadULong();
						if (GetPlayerIdByUniqueId(unregisterUniqueId, out ushort playerId))
						{
							var connInfo = GetConnectionInfoByUniqueId(unregisterUniqueId);
							if (connInfo != null && connInfo.State != NetworkConnection.State.Disconnected)
							{
								UnregisterPlayer(unregisterUniqueId);
								BroadcastUpdatePlayerPacket(playerId);
								DisconnectMethod(connId);

								if (IsStopRequest && GetPlayerCount() == 1)
								{
									StopComplete();
									ExecOnDisconnectAll(0);
								}
							}
						}
					}
					return true;
				case (byte)BuiltInPacket.Type.UpdatePlayerInfo:
					{
						//Serverは追えているのでPlayerInfoの更新のみ
						var state = (NetworkConnection.State)chunk.ReadByte();
						var playerId = chunk.ReadUShort();

						GetPlayerInfoByPlayerId(playerId).Deserialize(ref chunk);
						BroadcastUpdatePlayerPacket(playerId);
					}
					break;
				default:
					{
						//自分宛パケットの解析
						var playerInfo = GetPlayerInfoByConnId(connId);
						if (playerInfo != null)
						{
							RecieveData(playerInfo.PlayerId, playerInfo.UniqueId, type, chunk);
						}
					}
					break;
			}
			return false;
		}
	}

}
