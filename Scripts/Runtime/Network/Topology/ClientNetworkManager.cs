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

namespace ICKX.Radome
{
	public class UDPClientNetworkManager<PlayerInfo> : ClientNetworkManager<UDPNetworkInterface, PlayerInfo> where PlayerInfo : DefaultPlayerInfo, new()
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
				NetworkDriver = NetworkDriver.Create(new INetworkParameter[] {
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

			var endpoint = NetworkEndPoint.Parse(serverAdress.ToString(), serverPort);
			ServerConnection = NetworkDriver.Connect(endpoint);
			ServerConnId = ServerConnection.InternalId;

			base.Start();
		}

		protected override void ReconnectMethod()
		{
			JobHandle.Complete();

			Debug.Log("ReconnectMethod" + NetworkState + " : " + serverAdress + ":" + serverPort);

			if (NetworkState != NetworkConnection.State.Connected)
			{
				var endpoint = NetworkEndPoint.Parse(serverAdress.ToString(), serverPort);
				NetworkState = NetworkConnection.State.Connecting;

				if (NetworkDriver.IsCreated)
				{
					NetworkDriver.Dispose();
				}
				NetworkDriver = NetworkDriver.Create(new INetworkParameter[] {
					Config,
					new ReliableUtility.Parameters { WindowSize = 32 },
					new NetworkPipelineParams {initialCapacity = ushort.MaxValue},
					//new SimulatorUtility.Parameters {MaxPacketSize = 256, MaxPacketCount = 32, PacketDelayMs = 100},
				});
				_QosPipelines[(int)QosType.Empty] = NetworkDriver.CreatePipeline();
				//_QosPipelines[(int)QosType.Reliable] = NetworkDriver.CreatePipeline();
				//_QosPipelines[(int)QosType.Unreliable] = NetworkDriver.CreatePipeline();
				_QosPipelines[(int)QosType.Reliable] = NetworkDriver.CreatePipeline(typeof(ReliableSequencedPipelineStage), typeof(SimulatorPipelineStage));
				_QosPipelines[(int)QosType.Unreliable] = NetworkDriver.CreatePipeline(typeof(SimulatorPipelineStage));

				ServerConnection = NetworkDriver.Connect(endpoint);
				ServerConnId = ServerConnection.InternalId;

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
	public abstract class ClientNetworkManager<NetInterface, PlayerInfo> : ClientNetworkManagerBase<int, PlayerInfo>
			where NetInterface : struct, INetworkInterface where PlayerInfo : DefaultPlayerInfo, new()
	{
		protected override int EmptyConnId => -1;

		public NetworkDriver NetworkDriver;
		protected NativeArray<NetworkPipeline> _QosPipelines;

		public bool AutoRecconect { get; set; } = true;

		private bool _IsFirstUpdateComplete = false;

		public NetworkConnection ServerConnection { get; protected set; }

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

		public override void OnApplicationPause(bool pause)
		{
			JobHandle.Complete();

			base.OnApplicationPause(pause);
			if (pause)
			{
				NetworkDriver.Disconnect(ServerConnection);
			}
			else
			{
				DisconnectServer(ServerConnId);
			}
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

			if (ServerConnection.GetState(NetworkDriver) != NetworkConnection.State.Disconnected)
			{
				ServerConnection.Disconnect(NetworkDriver);
			}

			NetworkState = NetworkConnection.State.Disconnected;

			_IsFirstUpdateComplete = false;
		}

		protected override void SendToConnIdImmediately(int connId, NativeStreamWriter packet, bool reliable)
		{
		}

		/// <summary>
		/// 受信パケットの受け取りなど、最初に行うべきUpdateループ
		/// </summary>
		public override unsafe void OnFirstUpdate()
		{
			JobHandle.Complete();

			if (NetworkState == NetworkConnection.State.Disconnected)
			{
				return;
			}

			_SinglePacketBufferWriter.Clear();
			_BroadcastRudpChunkedPacketManager.Clear();
			_BroadcastUdpChunkedPacketManager.Clear();

			NetworkConnection connId;
			NetworkEvent.Type cmd;

			NativeArray<byte> buffer = new NativeArray<byte>(NetworkParameterConstants.MTU, Allocator.Temp);

			while ((cmd = NetworkDriver.PopEvent(out connId, out var dataStream)) != NetworkEvent.Type.Empty)
			{
				//Debug.Log(connId.InternalId + " : " + dataStream.IsCreated + " " + dataStream.HasFailedReads);
				//if (!dataStream.IsCreated) continue;
				if (cmd == NetworkEvent.Type.Connect)
				{
					//Serverと接続完了
					ServerConnection = connId;
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
					if (!dataStream.IsCreated)
					{
						continue;
					}
					dataStream.ReadBytes((byte*)buffer.GetUnsafePtr(), dataStream.Length);
					var stream = new NativeStreamReader(buffer, 0, dataStream.Length);

					RecieveClientStream(ref stream);
				}
			}
			buffer.Dispose();

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
			using (var array = new NativeArray<byte>(9, Allocator.Temp))
			{
				var packet = new NativeStreamWriter(array);
				packet.WriteByte((byte)BuiltInPacket.Type.MeasureRtt);
				packet.WriteLong(GamePacketManager.CurrentUnixTime);

				//ServerConnection.Send(NetworkDriver, _QosPipelines[(byte)QosType.Reliable], packet);
				Send(ServerPlayerId, packet, QosType.Unreliable);
			}
		}

		protected JobHandle ScheduleSendPacket(JobHandle jobHandle)
		{
			if (ServerConnection.GetState(NetworkDriver) != NetworkConnection.State.Connected)
			{
				return default;
			}

			var sendPacketsJob = new SendPacketaJob()
			{
				driver = NetworkDriver,
				serverConnection = ServerConnection,
				singlePacketBuffer = _SinglePacketBufferWriter,
				rudpPacketBuffer = _BroadcastRudpChunkedPacketManager.ChunkedPacketBufferWriter,
				udpPacketBuffer = _BroadcastUdpChunkedPacketManager.ChunkedPacketBufferWriter,
				qosPipelines = _QosPipelines,
				senderPlayerId = MyPlayerId,
			};

			return sendPacketsJob.Schedule(jobHandle);
		}


		struct SendPacketaJob : IJob
		{
			public NetworkDriver driver;
			[ReadOnly]
			public NetworkConnection serverConnection;

			[ReadOnly]
			public NativeStreamWriter singlePacketBuffer;
			[ReadOnly]
			public NativeStreamWriter rudpPacketBuffer;
			[ReadOnly]
			public NativeStreamWriter udpPacketBuffer;

			public NativeArray<NetworkPipeline> qosPipelines;
			[ReadOnly]
			public ushort senderPlayerId;

			unsafe void SendPacket(byte qos, ref NativeStreamWriter writer)
			{
				var sendWriter = driver.BeginSend(qosPipelines[qos], serverConnection);
				sendWriter.WriteBytes(writer.GetUnsafePtr(), writer.Length);
				driver.EndSend(sendWriter);
				//Debug.Log($"playerId{playerId} : {connection.InternalId} : qos{qos} : Len{writer.Length}");
			}

			public unsafe void Execute()
			{
				var multiCastList = new NativeList<ushort>(Allocator.Temp);
				var tempBuffer = new NativeArray<byte>(NetworkParameterConstants.MTU, Allocator.Temp);
				var tempWriter = new NativeStreamWriter(tempBuffer);

				if (singlePacketBuffer.Length != 0)
				{
					var reader = new NativeStreamReader(singlePacketBuffer, 0, singlePacketBuffer.Length);
					while (true)
					{
						if (!CreateSingleSendPacket(ref tempWriter, out ushort targetPlayerId, out byte qos, ref reader, multiCastList, senderPlayerId)) break;

						SendPacket(qos, ref tempWriter);
					}
				}

				if (udpPacketBuffer.Length != 0)
				{
					var reader = new NativeStreamReader(udpPacketBuffer, 0, udpPacketBuffer.Length);
					while (true)
					{
						if (!CreateChunkSendPacket(ref tempWriter, ref reader, multiCastList, senderPlayerId, (byte)QosType.Unreliable)) break;

						SendPacket((byte)QosType.Unreliable, ref tempWriter);
						//serverConnection.Send(driver, temp);
					}
				}
				if (rudpPacketBuffer.Length != 0)
				{
					var reader = new NativeStreamReader(rudpPacketBuffer, 0, rudpPacketBuffer.Length);
					while (true)
					{
						if (!CreateChunkSendPacket(ref tempWriter, ref reader, multiCastList, senderPlayerId, (byte)QosType.Reliable)) break;

						SendPacket((byte)QosType.Reliable, ref tempWriter);
					}
				}
				tempBuffer.Dispose();
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
			JobHandle.Complete();

			if (MyPlayerInfo.PlayerId != 0 && MyPlayerInfo.PlayerId < ushort.MaxValue-2)
			{
				Debug.Log("IsReConnected" + connId);
				//再接続
				//サーバーに自分のPlayerInfoを教える
				SendReconnectPlayerPacket(ServerPlayerId);
			}
			else
			{
				Debug.Log("IsConnected" + connId);
				//サーバーに自分のPlayerInfoを教える
				SendRegisterPlayerPacket(ServerPlayerId);
			}
		}

		protected void DisconnectServer(int connId, bool reconnect = true)
		{
			JobHandle.Complete();
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
					if (!reconnect) NetworkState = NetworkConnection.State.Disconnected;
				}
				else
				{
					DisconnectPlayer(MyPlayerInfo.UniqueId);
					ExecOnDisconnectAll(1);    //TODO ErrorCodeを取得する方法を探す
					NetworkState = NetworkConnection.State.AwaitingResponse;
				}
				if (reconnect) ReconnectMethod();
			}
		}

		protected abstract void ReconnectMethod();

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
			using (var array = new NativeArray<byte>(NetworkLinkerConstants.MaxPacketSize, Allocator.Temp))
			{
				var packet = new NativeStreamWriter(array);

				packet.WriteByte((byte)BuiltInPacket.Type.UpdatePlayerInfo);
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
		}

		protected void BroadcastUpdatePlayerPacket(ushort playerId)
		{
			var connInfo = GetConnectionInfoByPlayerId(playerId);
			var playerInfo = GetPlayerInfoByPlayerId(playerId);
			if (connInfo != null && playerInfo != null && connInfo.State != NetworkConnection.State.Connecting)
			{
				using (var array = new NativeArray<byte>(2 + playerInfo.PacketSize, Allocator.Temp))
				{
					var packet = new NativeStreamWriter(array);
					packet.WriteByte((byte)BuiltInPacket.Type.UpdatePlayerInfo);
					packet.WriteByte((byte)connInfo.State);
					playerInfo.AppendPlayerInfoPacket(ref packet);
					Broadcast(packet, QosType.Reliable);
				} 
			}
			else
			{
				using (var array = new NativeArray<byte>(2 + playerInfo.PacketSize, Allocator.Temp))
				{
					playerInfo = new PlayerInfo { PlayerId = playerId };
					var packet = new NativeStreamWriter(array);
					packet.WriteByte((byte)BuiltInPacket.Type.UpdatePlayerInfo);
					packet.WriteByte((byte)NetworkConnection.State.Disconnected);
					playerInfo.AppendPlayerInfoPacket(ref packet);
					Broadcast(packet, QosType.Reliable);
				}
			}
		}

		protected void SendRegisterPlayerPacket(ushort id, bool isReconnect)
		{
			using (var array = new NativeArray<byte>(14 + _ActivePlayerIdList.Count + MyPlayerInfo.PacketSize, Allocator.Temp))
			{
				var registerPacket = new NativeStreamWriter(array);
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

				Debug.Log($"Send Reg {id} : {LeaderStatTime} : {isReconnect} : count={_ActivePlayerIdList.Count} : {registerPacket.Length}");

				Send(id, registerPacket, QosType.Reliable);
			}
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

		protected void RecieveClientStream(ref NativeStreamReader stream)
		{
			byte qos = stream.ReadByte();
			ushort targetPlayerId = stream.ReadUShort();

			if (targetPlayerId == NetworkLinkerConstants.MulticastId)
			{
				ushort multiCastCount = stream.ReadUShort();
				for (int i = 0; i < multiCastCount; i++) stream.ReadUShort();
			}

			ushort senderPlayerId = stream.ReadUShort();

			GetUniqueIdByPlayerId(senderPlayerId, out ulong uniqueId);
			while (true)
			{
				int pos = stream.GetBytesRead();
				if (pos >= stream.Length) break;
				ushort dataLen = stream.ReadUShort();
				if (dataLen == 0 || pos + dataLen >= stream.Length) break;

				var chunk = stream.ReadChunk(dataLen);
				byte type = chunk.ReadByte();

				DeserializePacket(ServerConnId, uniqueId, type, chunk);
			}
		}

		protected static unsafe bool CreateSingleSendPacket(ref NativeStreamWriter writer, out ushort targetPlayerId, out byte qos, ref NativeStreamReader reader, NativeList<ushort> multiCastList, ushort senderPlayerId)
		{
			qos = 0;
			targetPlayerId = 0;
			writer.Clear();

			int pos = reader.GetBytesRead();
			if (pos >= reader.Length) return false;

			qos = reader.ReadByte();
			targetPlayerId = reader.ReadUShort();

			if (targetPlayerId == NetworkLinkerConstants.MulticastId)
			{
				multiCastList.Clear();
				ushort multiCastCount = reader.ReadUShort();
				for (int i = 0; i < multiCastCount; i++)
				{
					multiCastList.Add(reader.ReadUShort());
				}
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
				for (int i = 0; i < multiCastList.Length; i++)
				{
					writer.WriteUShort(multiCastList[i]);
					//Debug.Log("send multiCastList : " + multiCastList[i]);
				}
			}

			writer.WriteUShort(senderPlayerId);
			writer.WriteUShort(packetDataLen);
			writer.WriteBytes(packetPtr, packetDataLen);
			return true;
		}

		protected static unsafe bool CreateChunkSendPacket(ref NativeStreamWriter writer, ref NativeStreamReader reader, NativeList<ushort> multiCastList, ushort senderPlayerId, byte qos)
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
			writer.WriteUShort(senderPlayerId);
			//temp.Write(packetDataLen);
			writer.WriteBytes(packetPtr, packetDataLen);
			//Debug.Log("chunkedPacketBuffer : " + packetDataLen);
			return true;
		}

		protected override bool DeserializePacket(ConnIdType connId, ulong senderUniqueId, byte type, NativeStreamReader chunk)
		{
			//自分宛パケットの解析
			switch (type)
			{
				case (byte)BuiltInPacket.Type.RegisterPlayer:
					NetworkState = NetworkConnection.State.Connected;
					MyPlayerInfo.PlayerId = chunk.ReadUShort();

					LeaderStatTime = chunk.ReadLong();
					bool isRecconnect = chunk.ReadByte() != 0;

					Debug.Log($"Register ID={MyPlayerId} Time={LeaderStatTime} IsRec={isRecconnect}");

					byte count = chunk.ReadByte();
					_ActivePlayerIdList.Clear();
					for (int i = 0; i < count; i++)
					{
						_ActivePlayerIdList.Add(chunk.ReadByte());
					}

					var serverPlayerInfo = new PlayerInfo();
					serverPlayerInfo.Deserialize(ref chunk);

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
						while (chunk.GetBytesRead() + 2 < chunk.Length)
						{
							var state = (NetworkConnection.State)chunk.ReadByte();
							var updatePlayerInfo = new PlayerInfo();
							updatePlayerInfo.Deserialize(ref chunk);
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
						RecieveData(senderPlayerId, senderUniqueId, type, chunk);
					}
					break;
			}
			return false;
		}
	}
}
