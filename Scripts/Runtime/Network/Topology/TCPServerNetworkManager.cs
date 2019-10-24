using ICKX.Radome;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Networking.Transport;
using UnityEngine;
using Unity.Jobs;
using Unity.Networking.Transport.LowLevel.Unsafe;
using Unity.Collections.LowLevel.Unsafe;
using System.Linq;

namespace ICKX.Radome
{
	public class TcpStateObject
	{
		public const int StateBufferSize = ushort.MaxValue;
		public const int MaxRecievePacketBufferSize = ushort.MaxValue;
		public const int MaxSendPacketBufferSize = 4096;

		//public TcpServerManager<PlayerInfo> Server;
		public short ConnId = -1;
		public Socket Socket;
		public byte[] TempBuffer = new byte[MaxRecievePacketBufferSize];
		public int SendBufferStartIndex;
		public byte[] SendBuffer = new byte[MaxSendPacketBufferSize];

		public int BufferStartIndex;
		public byte[] Buffer = new byte[StateBufferSize];
		public List<ushort> BufferSizeList = new List<ushort>();

		public DataStreamWriter DataStreamWriter = default;
		public NativeList<ushort> DataStreamSizeList;
		public NativeList<DataStreamReader> DataStreamPackets;

		public object PacketWriteLockObject = new object();
	}

	public class TCPServerNetworkManager<PlayerInfo> : ServerNetworkManagerBase<short, PlayerInfo>  where PlayerInfo : DefaultPlayerInfo, new()
	{

		private static List<TCPServerNetworkManager<PlayerInfo>> _TcpServers = new List<TCPServerNetworkManager<PlayerInfo>>();

		private byte ManagerId;

		protected override short EmptyConnId => -1;

		private ManualResetEvent AcceptDone = new ManualResetEvent(false);

		public IPEndPoint IPEndPoint { get; private set; }
		//public SynchronizedCollection<Socket> ClientSockets { get; } = new SynchronizedCollection<Socket>();

		public Socket ListenerSocket { get; private set; }

		private object _ConnectLockObject = new object();
		private List<short> _ConnectConnIdList = new List<short>();
		private List<short> _DisconnectConnIdList = new List<short>();

		private List<TcpStateObject> _ClientSocketStates = new List<TcpStateObject>();
		public IReadOnlyList<TcpStateObject> ClientSocketStates => _ClientSocketStates;

		private bool _IsFirstUpdateComplete = false;
		
		public TCPServerNetworkManager(PlayerInfo playerInfo) : base(playerInfo)
		{

			_TcpServers.Add(this);
			ManagerId = (byte)(_TcpServers.Count - 1);
		}

		public override void Dispose()
		{
			if (_IsDispose) return;

			if (NetworkState != NetworkConnection.State.Disconnected)
			{
				StopComplete();
			}

			JobHandle.Complete();

			lock (_ConnectLockObject)
			{
				foreach (var state in _ClientSocketStates)
				{
					if (state != null)
					{
						if (state.Socket != null) state.Socket.Close();
						if (state.DataStreamWriter.IsCreated) state.DataStreamWriter.Dispose();
						if (state.DataStreamPackets.IsCreated) state.DataStreamPackets.Dispose();
						if (state.DataStreamSizeList.IsCreated) state.DataStreamSizeList.Dispose();
						state.Socket = null;
					}
				}
			}

			if (ListenerSocket != null) ListenerSocket.Close();
			ListenerSocket = null;

			base.Dispose();
		}

		public void Start (int port)
		{
			if (NetworkState != NetworkConnection.State.Disconnected)
			{
				Debug.LogError("Start Failed  currentState = " + NetworkState);
				return;
			}

			IPHostEntry ipHostInfo = Dns.GetHostEntry(Dns.GetHostName());
			IPAddress ipAddress = ipHostInfo.AddressList.First(a=>a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().Contains("169"));
			IPEndPoint = new IPEndPoint(ipAddress, port);

			ListenerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			ListenerSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
			
			try
			{
				ListenerSocket.Bind(IPEndPoint);
				ListenerSocket.Listen(100);
				ExecOnConnect();
				Debug.Log("Listen : " + IPEndPoint);

				Start();

				if (ListenerSocket != null)
				{
					//AcceptDone.Reset();
					ListenerSocket.BeginAccept(new System.AsyncCallback(AcceptCallback), this);
					//AcceptDone.WaitOne();
				}
			}
			catch (System.Exception e)
			{
				Debug.Log("Failed to bind to port : " + IPEndPoint);
				ExecOnConnectFailed(1);
			}
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
			
			NetworkState = NetworkConnection.State.Disconnected;

			_IsFirstUpdateComplete = false;
			NetworkState = NetworkConnection.State.Disconnected;
		}

		protected override void DisconnectMethod(short connId)
		{
			lock (_ConnectLockObject)
			{
				if (connId >= _ClientSocketStates.Count) return;
				var state = _ClientSocketStates[connId];
				state.Socket.Disconnect(true);
			}
		}

		public void AcceptCallback(System.IAsyncResult ar)
		{
			var server = (TCPServerNetworkManager<PlayerInfo>)ar.AsyncState;

			Socket listener = server.ListenerSocket;

			if(server.ListenerSocket != null)
			{
				Socket handler = listener.EndAccept(ar);
				// Create the state object.  
				var state = new TcpStateObject();
				//stateObject.Server = server;
				state.Socket = handler;

				lock (_ConnectLockObject)
				{
					server._ClientSocketStates.Add(state);
					short connId = (short)(server._ClientSocketStates.Count - 1);
					server._ClientSocketStates[connId].ConnId = connId;

					_ConnectConnIdList.Add(connId);
				}
				Debug.Log($"AcceptCallback EP = {handler.RemoteEndPoint}");

				listener.BeginAccept(new System.AsyncCallback(AcceptCallback), this);
				handler.BeginReceive(state.TempBuffer, 0, state.TempBuffer.Length, SocketFlags.None, new System.AsyncCallback(ReceiveCallback), state);
			}
		}

		private void ReceiveCallback(System.IAsyncResult asyncResult)
		{
			// StateObjectとクライアントソケットを取得
			var state = asyncResult.AsyncState as TcpStateObject;
			if (state == null) return;
			var socket = state.Socket;
			if (state.Socket == null) return;

			// クライアントソケットから受信データを取得終了
			ushort byteLen = 0;

			try
			{
				byteLen = (ushort)socket.EndReceive(asyncResult);
			}
			catch (System.Exception e)
			{
				Debug.LogError(e);
			}

			if (byteLen > 0)
			{
				lock (state.PacketWriteLockObject)
				{
					if(state.BufferStartIndex + byteLen > state.Buffer.Length)
					{
						System.Array.Resize(ref state.Buffer, state.Buffer.Length * 2);
					}

					System.Array.Copy(state.TempBuffer, 0, state.Buffer, state.BufferStartIndex, byteLen);
					state.BufferStartIndex += byteLen;
					state.BufferSizeList.Add(byteLen);
				}

				//再度受け取る
				socket.BeginReceive(state.TempBuffer, 0, state.TempBuffer.Length, SocketFlags.None, new System.AsyncCallback(ReceiveCallback), state);
			}
			else
			{
				// 0バイトデータの受信時は、切断されたとき
				socket.Close();
				lock (_ConnectLockObject)
				{
					_ClientSocketStates[state.ConnId].Socket = null;
					_DisconnectConnIdList.Add(state.ConnId);
				}
			}
		}
		
		private void SendCallback(System.IAsyncResult asyncResult)
		{
			try
			{
				var state = asyncResult.AsyncState as TcpStateObject;
				lock (state.PacketWriteLockObject)
				{
					state.SendBufferStartIndex = 0;
					var byteSize = state.Socket.EndSend(asyncResult);
					//Debug.Log($"SendCallback byteSize = {byteSize}");
				}
			}
			catch (System.Exception e)
			{
				Debug.LogError($"SendCallback Error : {e.Message}");
			}
		}

		protected override unsafe void SendToConnIdImmediately(short connId, DataStreamWriter packet, bool reliable)
		{
			if (connId == -1) return;

			var sizeData = stackalloc byte[2];
			var byteLen = (ushort)packet.Length;
			var lenPtr = (byte*)&byteLen;

			TcpStateObject state;

			lock (_ConnectLockObject)
			{
				if (connId >= _ClientSocketStates.Count) return;
				state = _ClientSocketStates[connId];
			}
			lock (state.PacketWriteLockObject)
			{
				if (state == null) return;
				var socket = state.Socket;

				if(state.SendBufferStartIndex + 2 + byteLen > state.SendBuffer.Length)
				{
					System.Array.Resize(ref state.SendBuffer, state.SendBuffer.Length * 2);
				}

				UnsafeUtility.MemCpy(sizeData, lenPtr, 2);
				state.SendBuffer[state.SendBufferStartIndex] = sizeData[0];
				state.SendBuffer[state.SendBufferStartIndex + 1] = sizeData[1];
				//packet.CopyTo(0, packet.Length, ref state.SendBuffer);

				fixed (byte* ptr = state.SendBuffer)
				{
					UnsafeUtility.MemCpy(ptr + state.SendBufferStartIndex + 2, packet.GetUnsafeReadOnlyPtr(), packet.Length);
				}
				
				//Debug.Log("SendToConnIdImmediately : " + packet.Length);
				socket.BeginSend(state.SendBuffer, state.SendBufferStartIndex, byteLen + 2, SocketFlags.None, new System.AsyncCallback(SendCallback), state);

				state.SendBufferStartIndex += byteLen + 2;
			}
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

			lock (_ConnectLockObject)
			{
				//接続確認
				for (ushort i = 0; i < _ConnectConnIdList.Count; i++)
				{
					Debug.Log($"Connect ConnId = {_ConnectConnIdList[i]}");
				}
				for (ushort i = 0; i < _DisconnectConnIdList.Count; i++)
				{
					OnDisconnectMethod(_DisconnectConnIdList[i]);
				}

				_ConnectConnIdList.Clear();
				_DisconnectConnIdList.Clear();
			}

			//パケット解析
			lock (_ConnectLockObject)
			{
				foreach (var state in _ClientSocketStates)
				{
					if (state == null) continue;
					if (!state.DataStreamPackets.IsCreated) continue;

					for (int i = 0; i < state.DataStreamPackets.Length; i++)
					{
						var chunk = state.DataStreamPackets[i];
						var ctx = default(DataStreamReader.Context);
						byte type = chunk.ReadByte(ref ctx);

						GetUniqueIdByConnId(state.ConnId, out ulong uniqueId);
						DeserializePacket(state.ConnId, uniqueId, type, ref chunk, ref ctx);
					}
				}
			}

			_IsFirstUpdateComplete = true;
		}

		private float _PrevSendTime;

		/// <summary>
		/// まとめたパケット送信など、最後に行うべきUpdateループ
		/// </summary>
		public override unsafe void OnLastUpdate()
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

			//BufferからJobSystem向けDataに変換
			lock (_ConnectLockObject)
			{
				foreach (var state in _ClientSocketStates)
				{
					if (state == null) continue;
					if (state.Socket == null) continue;

					if (!state.DataStreamWriter.IsCreated) state.DataStreamWriter = new DataStreamWriter(TcpStateObject.StateBufferSize, Allocator.Persistent);
					if (!state.DataStreamSizeList.IsCreated) state.DataStreamSizeList = new NativeList<ushort>(32, Allocator.Persistent);
					if (!state.DataStreamPackets.IsCreated) state.DataStreamPackets = new NativeList<DataStreamReader>(32, Allocator.Persistent);

					state.DataStreamWriter.Clear();
					state.DataStreamSizeList.Clear();
					state.DataStreamPackets.Clear();

					lock (state.PacketWriteLockObject)
					{
						fixed (byte* ptr = state.Buffer)
						{
							state.DataStreamWriter.WriteBytes(ptr, state.BufferStartIndex);
						}
						for (int i = 0; i < state.BufferSizeList.Count; i++)
						{
							state.DataStreamSizeList.Add(state.BufferSizeList[i]);
						}
						state.BufferStartIndex = 0;
						state.BufferSizeList.Clear();
					}
				}
			}

			JobHandle = ScheduleSendPacket(default);
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
				managerId = ManagerId,
				connections = _ConnectionIdList,
				singlePacketBuffer = _SinglePacketBuffer,
				rudpPacketBuffer = _BroadcastRudpChunkedPacketManager.ChunkedPacketBuffer,
				udpPacketBuffer = _BroadcastUdpChunkedPacketManager.ChunkedPacketBuffer,
				serverPlayerId = ServerPlayerId,
			};

			return sendPacketsJob.Schedule(jobHandle);
		}

		protected JobHandle ScheduleRecieve(JobHandle jobHandle)
		{
			lock (_ConnectLockObject)
			{
				foreach (var state in _ClientSocketStates)
				{
					if (state == null) continue;
					var recievePacketJob = new RecievePacketJob()
					{
						managerId = ManagerId,
						connections = _ConnectionIdList,
						recieveDataStream = state.DataStreamWriter,
						recieveDataSizeList = state.DataStreamSizeList,
						recieveDataPackets = state.DataStreamPackets,
						serverPlayerId = ServerPlayerId,
					};

					jobHandle = recievePacketJob.Schedule(jobHandle);
				}
			}

			return jobHandle;
		}

		struct SendPacketaJob : IJob
		{
			[ReadOnly]
			public byte managerId;
			[ReadOnly]
			public NativeList<short> connections;
			[ReadOnly]
			public DataStreamWriter singlePacketBuffer;
			[ReadOnly]
			public DataStreamWriter rudpPacketBuffer;
			[ReadOnly]
			public DataStreamWriter udpPacketBuffer;

			[ReadOnly]
			public ushort serverPlayerId;
			
			public unsafe void Execute()
			{
				var manager = _TcpServers[managerId];

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
								manager.SendToConnIdImmediately(connections[i], temp, true);
							}
						}
						else if (targetPlayerId == NetworkLinkerConstants.MulticastId)
						{
							for (ushort i = 0; i < multiCastList.Length; i++)
							{
								if (multiCastList[i] < connections.Length)
								{
									if (multiCastList[i] >= connections.Length) continue;
									manager.SendToConnIdImmediately(connections[multiCastList[i]], temp, true);
								}
							}
						}
						else
						{
							if (targetPlayerId >= connections.Length) continue;
							manager.SendToConnIdImmediately(connections[targetPlayerId], temp, true);
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
							if (i >= connections.Length) continue;

							if (connections[i] != -1)
							{
								manager.SendToConnIdImmediately(connections[i], temp, true);
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
							if (i >= connections.Length) continue;

							if (connections[i] != -1)
							{
								manager.SendToConnIdImmediately(connections[i], temp, true);
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
			[ReadOnly]
			public byte managerId;
			[ReadOnly]
			public NativeList<short> connections;

			public DataStreamWriter recieveDataStream;
			public NativeList<ushort> recieveDataSizeList;
			public NativeList<DataStreamReader> recieveDataPackets;

			[ReadOnly]
			public ushort serverPlayerId;

			public unsafe void Execute()
			{
				var manager = _TcpServers[managerId];
				var relayWriter = new DataStreamWriter(NetworkParameterConstants.MTU, Allocator.Temp);
				var multiCastList = new NativeList<ushort>(Allocator.Temp);

				int offset = 0;
				for(int index=0; index < recieveDataSizeList.Length; index++)
				{
					var stream = new DataStreamReader(recieveDataStream, offset, recieveDataSizeList[index]);
					offset += recieveDataSizeList[index];
					var ctx = new DataStreamReader.Context();

					while (stream.GetBytesRead(ref ctx) < stream.Length)
					{
						ushort tcpPacketSize = stream.ReadUShort(ref ctx);
						Debug.Log($"tcpPacketSize {tcpPacketSize} : streamLen {stream.Length} : pos {stream.GetBytesRead(ref ctx)}");

						if(stream.GetBytesRead(ref ctx) + tcpPacketSize > stream.Length)
						{
							var c = new DataStreamReader.Context();
							Debug.Log("BugPacketDump : " + string.Join(", ", stream.ReadBytesAsArray(ref c, stream.Length)));
							continue;
						}

						var packet = stream.ReadChunk(ref ctx, tcpPacketSize);

						var ctx2 = new DataStreamReader.Context();
						byte qos = packet.ReadByte(ref ctx2);
						ushort targetPlayerId = packet.ReadUShort(ref ctx2);
						ushort senderPlayerId = packet.ReadUShort(ref ctx2);
						
						if (targetPlayerId == NetworkLinkerConstants.MulticastId)
						{
							multiCastList.Clear();
							ushort multiCastCount = packet.ReadUShort(ref ctx2);
							for (int i = 0; i < multiCastCount; i++)
							{
								multiCastList.Add(packet.ReadUShort(ref ctx2));
							}
						}

						if (targetPlayerId == NetworkLinkerConstants.BroadcastId)
						{
							for (ushort i = 0; i < connections.Length; i++)
							{
								if (i == serverPlayerId) continue;
								if (connections[i] != -1 && senderPlayerId != i)
								{
									RelayPacket(i, packet, qos, manager, ref relayWriter);
								}
							}
							PurgeChunk(senderPlayerId, ref packet, ref ctx2);
						}
						else if (targetPlayerId == NetworkLinkerConstants.MulticastId)
						{
							for (int i = 0; i < multiCastList.Length; i++)
							{
								if (multiCastList[i] == serverPlayerId)
								{
									PurgeChunk(senderPlayerId, ref packet, ref ctx2);
								}
								else
								{
									if (senderPlayerId != multiCastList[i])
									{
										RelayPacket(multiCastList[i], packet, qos, manager, ref relayWriter);
									}
								}
							}
						}
						else
						{
							if (targetPlayerId == serverPlayerId)
							{
								PurgeChunk(senderPlayerId, ref packet, ref ctx2);
							}
							else
							{
								RelayPacket(targetPlayerId, packet, qos, manager, ref relayWriter);
							}
						}
					}
				}
				
				relayWriter.Dispose();
				multiCastList.Dispose();
			}

			private void PurgeChunk(ushort senderPlayerId, ref DataStreamReader stream, ref DataStreamReader.Context ctx)
			{
				while (true)
				{
					int pos = stream.GetBytesRead(ref ctx);
					if (pos >= stream.Length) break;
					ushort dataLen = stream.ReadUShort(ref ctx);
					if (dataLen == 0 || pos + dataLen >= stream.Length) break;

					var chunk = stream.ReadChunk(ref ctx, dataLen);

					recieveDataPackets.Add(chunk);
				}
			}

			private unsafe void RelayPacket(ushort targetPlayerId, DataStreamReader stream, byte qos
				, TCPServerNetworkManager<PlayerInfo> manager, ref DataStreamWriter relayWriter)
			{
				relayWriter.Clear();
				relayWriter.WriteBytes(stream.GetUnsafeReadOnlyPtr(), stream.Length);

				if (targetPlayerId >= connections.Length) return;
				manager.SendToConnIdImmediately(connections[targetPlayerId], relayWriter, true);
			}
		}
	}
}