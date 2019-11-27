using System;
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
using UnityEngine.Profiling;

namespace ICKX.Radome
{

	public class UDPServerNetworkManager<PlayerInfo> : ServerNetworkManager<UdpNetworkDriver, PlayerInfo> where PlayerInfo : DefaultPlayerInfo, new()
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
	public abstract class ServerNetworkManager<Driver, PlayerInfo> : ServerNetworkManagerBase<int, PlayerInfo>
			where Driver : struct, INetworkDriver where PlayerInfo : DefaultPlayerInfo, new()
	{

		protected override int EmptyConnId => -1;

		public float registrationTimeOut { get; set; } = 60.0f;

		public Driver NetworkDriver;
		protected NativeArray<NetworkPipeline> _QosPipelines;

		protected NativeList<NetworkConnection> _ConnectConnIdList;
		protected NativeList<NetworkConnection> _DisconnectConnIdList;
		protected DataStreamWriter _RelayWriter;
		protected NativeList<DataPacket> _RecieveDataStream;

		protected NativeList<NetworkConnection> _NetworkConnections;

		private bool _IsFirstUpdateComplete = false;

		public struct DataPacket
		{
			public NetworkConnection Connection;
			public DataStreamReader Chunk;
		}

		public ServerNetworkManager(PlayerInfo playerInfo) : base(playerInfo)
		{
			_NetworkConnections = new NativeList<NetworkConnection>(4, Allocator.Persistent);
			_ConnectConnIdList = new NativeList<NetworkConnection>(4, Allocator.Persistent);
			_DisconnectConnIdList = new NativeList<NetworkConnection>(4, Allocator.Persistent);
			_RelayWriter = new DataStreamWriter(NetworkParameterConstants.MTU, Allocator.Persistent);
			//_RecieveDataStream = new NativeMultiHashMap<int, DataStreamReader>(32, Allocator.Persistent);
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
			_RelayWriter.Dispose();
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
			_RelayWriter.Clear();
			_RecieveDataStream.Clear();

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
			if (NetworkState == NetworkConnection.State.Disconnected)
			{
				return;
			}

			//job完了待ち
			JobHandle.Complete();

			_SinglePacketBuffer.Clear();
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
				var ctx = default(DataStreamReader.Context);
				byte type = chunk.ReadByte(ref ctx);
				GetUniqueIdByConnId(connection.InternalId, out ulong uniqueId);
				DeserializePacket(connection.InternalId, uniqueId, type, ref chunk, ref ctx);
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
			using (var packet = new DataStreamWriter(9, Allocator.Temp))
			{
				packet.Write((byte)BuiltInPacket.Type.MeasureRtt);
				packet.Write(GamePacketManager.CurrentUnixTime);
				
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
				singlePacketBuffer = _SinglePacketBuffer,
				rudpPacketBuffer = _BroadcastRudpChunkedPacketManager.ChunkedPacketBuffer,
				udpPacketBuffer = _BroadcastUdpChunkedPacketManager.ChunkedPacketBuffer,
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
				relayWriter = _RelayWriter,
				dataStream = _RecieveDataStream,
				serverPlayerId = ServerPlayerId,
			};
			return recievePacketJob.Schedule(jobHandle);
		}

		struct SendPacketaJob : IJob
		{
			public Driver driver;
			[ReadOnly]
			public NativeList<int> connections;
			[ReadOnly]
			public NativeList<NetworkConnection> networkConnections;

			[ReadOnly]
			public DataStreamWriter singlePacketBuffer;
			[ReadOnly]
			public DataStreamWriter rudpPacketBuffer;
			[ReadOnly]
			public DataStreamWriter udpPacketBuffer;

			[ReadOnly]
			public NativeArray<NetworkPipeline> qosPipelines;

			[ReadOnly]
			public ushort serverPlayerId;

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
						temp.Write(serverPlayerId);
						temp.Write(packetDataLen);
						temp.WriteBytes(packetPtr, packetDataLen);

						if (targetPlayerId == NetworkLinkerConstants.BroadcastId)
						{
							for (ushort i = 0; i < connections.Length; i++)
							{
								if (i == serverPlayerId) continue;
								if (i >= connections.Length) continue;
								if (connections[i] >= networkConnections.Length) continue;
								var connection = networkConnections[connections[i]];
								connection.Send(driver, qosPipelines[qos], temp);
								//Debug.Log($"{i} : {connection.InternalId} : qos{qos} : Len{packetDataLen}");
							}
						}
						else if (targetPlayerId == NetworkLinkerConstants.MulticastId)
						{
							for (ushort i = 0; i < multiCastList.Length; i++)
							{
								if (multiCastList[i] < connections.Length)
								{
									if (multiCastList[i] >= connections.Length) continue;
									if (connections[multiCastList[i]] >= networkConnections.Length) continue;
									var connection = networkConnections[connections[multiCastList[i]]];
									connection.Send(driver, qosPipelines[qos], temp);
									//Debug.Log($"{multiCastList[i]} : {connection.InternalId} : qos{qos} : Len{packetDataLen}");
								}
							}
						}
						else
						{
							if (connections[targetPlayerId] >= networkConnections.Length) continue;
							var connection = networkConnections[connections[targetPlayerId]];
							connection.Send(driver, qosPipelines[qos], temp);
							//Debug.Log($"{targetPlayerId} : {connection.InternalId} : qos{qos} : Len{packetDataLen}");
						}
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
						temp.Write(serverPlayerId);
						//temp.Write(packetDataLen);
						temp.WriteBytes(packetPtr, packetDataLen);

						for (ushort i = 0; i < connections.Length; i++)
						{
							if (i == serverPlayerId) continue;

							if (connections[i] != -1)
							{
								var connection = networkConnections[connections[i]];
								connection.Send(driver, qosPipelines[(byte)QosType.Unreliable], temp);
							}
						}
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
						temp.Write(serverPlayerId);
						//temp.Write(packetDataLen);
						temp.WriteBytes(packetPtr, packetDataLen);

						for (ushort i = 0; i < connections.Length; i++)
						{
							if (i == serverPlayerId) continue;

							if (connections[i] != -1)
							{
								var connection = networkConnections[connections[i]];
								connection.Send(driver, qosPipelines[(byte)QosType.Reliable], temp);
								//connections[i].Send(driver, temp);
							}
						}
						//Debug.Log("chunkedPacketBuffer : " + packetDataLen);
					}
				}
				temp.Dispose();
				multiCastList.Dispose();
			}
		}

		struct RecievePacketJob : IJob
		{
			public Driver driver;
			[ReadOnly]
			public NativeList<int> connections;
			[ReadOnly]
			public NativeList<NetworkConnection> networkConnections;
			[ReadOnly]
			public NativeArray<NetworkPipeline> qosPipelines;

			public NativeList<NetworkConnection> connectConnIdList;
			public NativeList<NetworkConnection> disconnectConnIdList;
			public DataStreamWriter relayWriter;
			//public NativeMultiHashMap<int, DataStreamReader> dataStream;
			public NativeList<DataPacket> dataStream;
			[ReadOnly]
			public ushort serverPlayerId;

			public unsafe void Execute()
			{
				var multiCastList = new NativeList<ushort>(Allocator.Temp);
				connectConnIdList.Clear();
				disconnectConnIdList.Clear();

				NetworkConnection con;
				DataStreamReader stream;
				NetworkEvent.Type cmd;

				while ((cmd = driver.PopEvent(out con, out stream)) != NetworkEvent.Type.Empty)
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
						if (!stream.IsCreated)
						{
							continue;
						}
						//Debug.Log($"driver.PopEvent={cmd} con={con.InternalId} : {stream.Length}");

						//var c = new DataStreamReader.Context();
						//Debug.Log($"Dump : {string.Join(",", stream.ReadBytesAsArray(ref c, stream.Length))}");

						var ctx = new DataStreamReader.Context();
						byte qos = stream.ReadByte(ref ctx);
						ushort targetPlayerId = stream.ReadUShort(ref ctx);
						ushort senderPlayerId = stream.ReadUShort(ref ctx);

						var ctx2 = ctx;
						byte type = stream.ReadByte(ref ctx2);

						//if (type == (byte)BuiltInPacket.Type.MeasureRtt) continue;

						if (targetPlayerId == NetworkLinkerConstants.MulticastId)
						{
							multiCastList.Clear();
							ushort multiCastCount = stream.ReadUShort(ref ctx);
							for (int i = 0; i < multiCastCount; i++)
							{
								multiCastList.Add(stream.ReadUShort(ref ctx));
							}
						}

						if (targetPlayerId == NetworkLinkerConstants.BroadcastId)
						{
							for (ushort i = 0; i < connections.Length; i++)
							{
								if (i == serverPlayerId) continue;
								if (connections[i] != -1 && senderPlayerId != i)
								{
									RelayPacket(i, stream, qos);
								}
							}
							PurgeChunk(senderPlayerId, con, ref stream, ref ctx);
						}
						else if (targetPlayerId == NetworkLinkerConstants.MulticastId)
						{
							for (int i = 0; i < multiCastList.Length; i++)
							{
								if (multiCastList[i] == serverPlayerId)
								{
									PurgeChunk(senderPlayerId, con, ref stream, ref ctx);
								}
								else
								{
									if (senderPlayerId != multiCastList[i])
									{
										RelayPacket(multiCastList[i], stream, qos);
									}
								}
							}
						}
						else
						{
							if (targetPlayerId == serverPlayerId)
							{
								PurgeChunk(senderPlayerId, con, ref stream, ref ctx);
							}
							else
							{
								RelayPacket(targetPlayerId, stream, qos);
							}
						}
					}
				}
				multiCastList.Dispose();
			}

			private void PurgeChunk(ushort senderPlayerId, NetworkConnection con, ref DataStreamReader stream, ref DataStreamReader.Context ctx)
			{
				while (true)
				{
					int pos = stream.GetBytesRead(ref ctx);
					if (pos >= stream.Length) break;
					ushort dataLen = stream.ReadUShort(ref ctx);
					if (dataLen == 0 || pos + dataLen >= stream.Length) break;

					var chunk = stream.ReadChunk(ref ctx, dataLen);
					var ctx2 = new DataStreamReader.Context();
					byte type = chunk.ReadByte(ref ctx2);

					//if (type != (byte)BuiltInPacket.Type.MeasureRtt)
					//{
					//    var c = new DataStreamReader.Context();
					//    Debug.Log($"Dump : {string.Join(",", chunk.ReadBytesAsArray(ref c, chunk.Length))}");
					//}

					dataStream.Add(new DataPacket() { Connection = con, Chunk = chunk });
				}
			}

			private unsafe void RelayPacket(ushort targetPlayerId, DataStreamReader stream, byte qos)
			{
				relayWriter.Clear();
				relayWriter.WriteBytes(stream.GetUnsafeReadOnlyPtr(), stream.Length);
				var connection = networkConnections[connections[targetPlayerId]];
				connection.Send(driver, qosPipelines[qos], relayWriter);
				//connections[targetPlayerId].Send(driver, relayWriter);
			}
		}
	}


	public abstract class ServerNetworkManagerBase<ConnIdType, PlayerInfo> : GenericNetworkManagerBase<ConnIdType, PlayerInfo>
			where ConnIdType : struct, System.IEquatable<ConnIdType> where PlayerInfo : DefaultPlayerInfo, new()
	{
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
								.Where(info => info != null && !IsEquals( info.ConnId, EmptyConnId))
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
				if (IsEquals(connId, EmptyConnId)) continue;

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
				Broadcast(packet, QosType.Reliable, true);
				packet.Dispose();
			}
			else
			{
				playerInfo = new PlayerInfo { PlayerId = playerId };
				var packet = new DataStreamWriter(2 + playerInfo.PacketSize, Allocator.Temp);
				packet.Write((byte)BuiltInPacket.Type.UpdatePlayerInfo);
				packet.Write((byte)NetworkConnection.State.Disconnected);
				playerInfo.AppendPlayerInfoPacket(ref packet);
				Broadcast(packet, QosType.Reliable, true);
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

			//Debug.Log($"Send Reg {id} : {LeaderStatTime} : {isReconnect} : count={_ActivePlayerIdList.Count} : {registerPacket.Length}");

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

		protected abstract void DisconnectMethod(ConnIdType connId);

		protected override bool DeserializePacket(ConnIdType connId, ulong uniqueId, byte type, ref DataStreamReader chunk, ref DataStreamReader.Context ctx2)
		{
			//Debug.Log($"DeserializePacket : {uniqueId} : {(BuiltInPacket.Type)type} {chunk.Length}");
			switch (type)
			{
				case (byte)BuiltInPacket.Type.MeasureRtt:
					break;
				case (byte)BuiltInPacket.Type.RegisterPlayer:
					{
						var addPlayerInfo = new PlayerInfo();
						addPlayerInfo.Deserialize(ref chunk, ref ctx2);

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
						reconnectPlayerInfo.Deserialize(ref chunk, ref ctx2);
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
						ulong unregisterUniqueId = chunk.ReadULong(ref ctx2);
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
						var state = (NetworkConnection.State)chunk.ReadByte(ref ctx2);
						var ctx3 = ctx2;
						var playerId = chunk.ReadUShort(ref ctx3);

						GetPlayerInfoByPlayerId(playerId).Deserialize(ref chunk, ref ctx2);
						BroadcastUpdatePlayerPacket(playerId);
					}
					break;
				default:
					{
						//自分宛パケットの解析
						var playerInfo = GetPlayerInfoByConnId(connId);
						if(playerInfo != null)
						{
							RecieveData(playerInfo.PlayerId, playerInfo.UniqueId, type, chunk, ctx2);
						}
					}
					break;
			}
			return false;
		}
	}

}
