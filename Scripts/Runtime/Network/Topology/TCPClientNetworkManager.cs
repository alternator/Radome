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
	public class TCPClientNetworkManager<PlayerInfo> : ClientNetworkManagerBase<short, PlayerInfo> where PlayerInfo : DefaultPlayerInfo, new()
	{
		private static List<TCPClientNetworkManager<PlayerInfo>> _TcpClients = new List<TCPClientNetworkManager<PlayerInfo>>();
		
		private byte ManagerId;

		public IPEndPoint IPEndPoint { get; private set; }
		public TcpStateObject ServerSocketState;

		private static ManualResetEvent connectDone = new ManualResetEvent(false);

		private object _ConnectLockObject = new object();
		private List<short> _ConnectConnIdList = new List<short>();
		private List<short> _DisconnectConnIdList = new List<short>();

		public bool AutoRecconect { get; set; } = true;
		protected byte[] _SendBuffer = new byte[2048];

		protected override short EmptyConnId => -1;
		private bool _IsFirstUpdateComplete = false;

		public TCPClientNetworkManager(PlayerInfo playerInfo) : base(playerInfo)
		{
			_TcpClients.Add(this);
			ManagerId = (byte)(_TcpClients.Count - 1);
		}

		public override void Dispose()
		{
			if (_IsDispose) return;

			if (NetworkState != NetworkConnection.State.Disconnected)
			{
				StopComplete();
			}

			JobHandle.Complete();

			if (ServerSocketState != null)
			{
				if (ServerSocketState.Socket != null) ServerSocketState.Socket.Close();
				if (ServerSocketState.DataStreamWriter.IsCreated) ServerSocketState.DataStreamWriter.Dispose();
				if (ServerSocketState.DataStreamPackets.IsCreated) ServerSocketState.DataStreamPackets.Dispose();
				if (ServerSocketState.DataStreamSizeList.IsCreated) ServerSocketState.DataStreamSizeList.Dispose();
				ServerSocketState.Socket = null;
			}

			base.Dispose();
		}

		public override void OnApplicationPause(bool pause)
		{
		}

		public void Start(IPAddress adress, ushort port)
		{
			Debug.Log(adress);
			if (adress.ToString().Contains("169.254"))
			{
				IPEndPoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), port);
			}
			else
			{
				IPEndPoint = new IPEndPoint(adress, port);
			}
			Connect();
		}

		private void Connect ()
		{
			if(ServerSocketState == null) ServerSocketState = new TcpStateObject();
			var state = ServerSocketState;

			connectDone.Reset();

			state.Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			state.Socket.BeginConnect(IPEndPoint, ConnectCallback, state.Socket);
			
			base.Start();
		}

		public void ConnectCallback(System.IAsyncResult ar)
		{
			Debug.Log($"ConnectCallback {IPEndPoint.Address} : {IPEndPoint.Port}");
			Socket socket = (Socket)ar.AsyncState;
			try
			{
				socket.EndConnect(ar);
				lock (_ConnectLockObject)
				{
					_ConnectConnIdList.Add(0);
				}
				var state = ServerSocketState;
				state.Socket.BeginReceive(state.TempBuffer, 0, state.TempBuffer.Length, SocketFlags.None, new System.AsyncCallback(ReceiveCallback), state);
			}
			catch (System.Exception e)
			{
				Debug.LogError($"Connection Failed {IPEndPoint.Address} : {IPEndPoint.Port} : {e.Message}");

				lock (_ConnectLockObject)
				{
					ServerSocketState.Socket = null;
				}
				_DisconnectConnIdList.Add(0);
			}
			connectDone.Set();
		}

		protected override void ReconnectMethod()
		{
			JobHandle.Complete();

			Debug.Log("ReconnectMethod" + NetworkState + " : " + IPEndPoint.Address + ":" + IPEndPoint.Port);

			if (NetworkState != NetworkConnection.State.Connected)
			{
				Connect();
				Debug.Log("Reconnect");
			}
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
			
			if(ServerSocketState != null && ServerSocketState.Socket != null)
			{
				if(ServerSocketState.Socket.Connected)
				{
					try
					{
						ServerSocketState.Socket.Disconnect(true);
					}
					catch (System.Exception e)
					{
						Debug.Log(e);
					}
				}
			}

			NetworkState = NetworkConnection.State.Disconnected;

			_IsFirstUpdateComplete = false;
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
					if (state.BufferStartIndex + byteLen > state.Buffer.Length)
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
				//throw new System.NotImplementedException("byteLen = 0");
				socket.Close();
				lock (_ConnectLockObject)
				{
					ServerSocketState.Socket = null;
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
			var sizeData = stackalloc byte[2];
			var byteLen = (ushort)packet.Length;
			var lenPtr = (byte*)&byteLen;

			TcpStateObject state = ServerSocketState;

			lock (state.PacketWriteLockObject)
			{
				if (state == null) return;
				var socket = state.Socket;

				if (state.SendBufferStartIndex + 2 + byteLen > state.SendBuffer.Length)
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

				socket.BeginSend(state.SendBuffer, state.SendBufferStartIndex, byteLen + 2, SocketFlags.None, new System.AsyncCallback(SendCallback), state);

				state.SendBufferStartIndex += byteLen + 2;
			}
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

			if(_IsFirstUpdateComplete)
			{
				_SinglePacketBuffer.Clear();
				_BroadcastRudpChunkedPacketManager.Clear();
				_BroadcastUdpChunkedPacketManager.Clear();
			}
			lock (_ConnectLockObject)
			{
				//接続確認
				for (ushort i = 0; i < _ConnectConnIdList.Count; i++)
				{
					//Serverと接続完了
					ServerConnId = 0;
					ConnectServer(0);
				}
				//切断処理
				for (ushort i = 0; i < _DisconnectConnIdList.Count; i++)
				{
					DisconnectServer(0);
				}

				_ConnectConnIdList.Clear();
				_DisconnectConnIdList.Clear();
			}

			//パケット解析
			var state = ServerSocketState;

			if (state != null && state.Socket != null && state.DataStreamPackets.IsCreated)
			{
				for (int i = 0; i < state.DataStreamPackets.Length; i++)
				{
					var chunk = state.DataStreamPackets[i];
					var ctx = default(DataStreamReader.Context);
					byte type = chunk.ReadByte(ref ctx);

					//Debug.Log("Deserialize : " + chunk.Length + " : " + (BuiltInPacket.Type)type);

					GetUniqueIdByConnId(state.ConnId, out ulong uniqueId);
					DeserializePacket(state.ConnId, uniqueId, type, ref chunk, ref ctx);
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

			if (NetworkState == NetworkConnection.State.Connected)
			{
				if (Time.realtimeSinceStartup - _PrevSendTime > 1.0)
				{
					_PrevSendTime = Time.realtimeSinceStartup;
					SendMeasureLatencyPacket();
				}
			}
			_BroadcastRudpChunkedPacketManager.WriteCurrentBuffer();
			_BroadcastUdpChunkedPacketManager.WriteCurrentBuffer();

			var state = ServerSocketState;

			//パケット解析
			if (state != null && state.Socket != null)
			{
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

				//ServerConnection.Send(NetworkDriver, _QosPipelines[(byte)QosType.Reliable], packet);
				Send(ServerPlayerId, packet, QosType.Unreliable);
			}
		}

		protected JobHandle ScheduleSendPacket(JobHandle jobHandle)
		{
			//if (NetworkState != NetworkConnection.State.Connected)
			//{
			//	return jobHandle;
			//}

			var sendPacketsJob = new SendPacketaJob()
			{
				managerId = ManagerId,
				singlePacketBuffer = _SinglePacketBuffer,
				rudpPacketBuffer = _BroadcastRudpChunkedPacketManager.ChunkedPacketBuffer,
				udpPacketBuffer = _BroadcastUdpChunkedPacketManager.ChunkedPacketBuffer,
				senderPlayerId = MyPlayerId,
			};

			return sendPacketsJob.Schedule(jobHandle);
		}

		protected JobHandle ScheduleRecieve(JobHandle jobHandle)
		{
			var state = ServerSocketState;
			if (state == null) return jobHandle;

			var recievePacketJob = new RecievePacketJob()
			{
				recieveDataStream = state.DataStreamWriter,
				recieveDataSizeList = state.DataStreamSizeList,
				recieveDataPackets = state.DataStreamPackets,
				serverPlayerId = ServerPlayerId,
			};

			return recievePacketJob.Schedule(jobHandle);
		}

		struct SendPacketaJob : IJob
		{
			[ReadOnly]
			public byte managerId;
			[ReadOnly]
			public DataStreamWriter singlePacketBuffer;
			[ReadOnly]
			public DataStreamWriter rudpPacketBuffer;
			[ReadOnly]
			public DataStreamWriter udpPacketBuffer;
			[ReadOnly]
			public ushort senderPlayerId;

			public unsafe void Execute()
			{
				var manager = _TcpClients[managerId];
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
						manager.SendToConnIdImmediately(0, temp, true);
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

						manager.SendToConnIdImmediately(0, temp, true);
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

						manager.SendToConnIdImmediately(0, temp, true);
					}
				}
				temp.Dispose();
				multiCastList.Dispose();
			}
		}


		struct RecievePacketJob : IJob
		{
			public DataStreamWriter recieveDataStream;
			public NativeList<ushort> recieveDataSizeList;
			public NativeList<DataStreamReader> recieveDataPackets;

			[ReadOnly]
			public ushort serverPlayerId;

			public unsafe void Execute()
			{
				int offset = 0;

				for (int index = 0; index < recieveDataSizeList.Length; index++)
				{
					//Debug.Log("RecievePacketJob : " + index + ":" + recieveDataSizeList[index]);
					var stream = new DataStreamReader(recieveDataStream, offset, recieveDataSizeList[index]);
					offset += recieveDataSizeList[index];
					
					var ctx = new DataStreamReader.Context();

					while (stream.GetBytesRead(ref ctx) < stream.Length)
					{
						ushort tcpPacketSize = stream.ReadUShort(ref ctx);

						Debug.Log($"tcpPacketSize {tcpPacketSize} : streamLen {stream.Length} : pos {stream.GetBytesRead(ref ctx)}");
						if (stream.GetBytesRead(ref ctx) + tcpPacketSize > stream.Length)
						{
							var c = new DataStreamReader.Context();
							Debug.Log("BugPacketDump : " + string.Join(", ", stream.ReadBytesAsArray(ref c, stream.Length)));
							break;
						}

						var packet = stream.ReadChunk(ref ctx, tcpPacketSize);

						var ctx2 = new DataStreamReader.Context();
						byte qos = packet.ReadByte(ref ctx2);
						ushort targetPlayerId = packet.ReadUShort(ref ctx2);
						ushort senderPlayerId = packet.ReadUShort(ref ctx2);

						PurgeChunk(senderPlayerId, ref packet, ref ctx2);
					}
				}
			}

			private void PurgeChunk(ushort senderPlayerId, ref DataStreamReader stream, ref DataStreamReader.Context ctx)
			{
				while (true)
				{
					int pos = stream.GetBytesRead(ref ctx);
					if (pos >= stream.Length) break;
					ushort dataLen = stream.ReadUShort(ref ctx);
					//Debug.Log($"Purge chunk {stream.Length} , {dataLen}, {pos + dataLen}  > {stream.Length}");
					if (dataLen == 0 || pos + dataLen >= stream.Length) break;

					var chunk = stream.ReadChunk(ref ctx, dataLen);

					recieveDataPackets.Add(chunk);
				}
			}
		}
	}
}