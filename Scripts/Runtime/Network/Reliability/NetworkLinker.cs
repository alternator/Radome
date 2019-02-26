﻿using System.Net;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Events;
using Unity.Networking.Transport;
using Unity.Networking.Transport.LowLevel.Unsafe;
using UnityEngine.Assertions;
using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;

namespace ICKX.Radome
{

	/// <summary>
	/// ライブラリ利用側は「パケットの送信」「受取りパケットのデシリアライズ」をmain threadのUserUpdate内で行う
	/// </summary>
	public enum QosType : byte
	{
		Empty = 0,
		Reliable,
		Unreliable,
		ChunkEnd,       //以下はChunkにしない内部処理用パケット
		MeasureLatency,
		End,
	}

	public static class NetworkLinkerConstants
	{
		public const int TimeOutFrameCount = 300;
		public const int QosHeaderSize = 1 + 2 + 2;
	}

	/// <summary>
	/// Notification Layer
	/// RUDPに対応させる
	/// </summary>
	public class NetworkLinker<T> : System.IDisposable where T : struct, INetworkDriver
	{

		struct SendUnreliableChunksJob : IJob
		{
			[ReadOnly]
			public T driver;
			[ReadOnly]
			public NetworkConnection connection;

			[ReadOnly]
			public DataStreamWriter packetChunks;
			[ReadOnly]
			public NativeList<ushort> packetLengths;

			public void Execute()
			{
				var reader = new DataStreamReader(packetChunks, 0, packetChunks.Length);
				var ctx = default(DataStreamReader.Context);

				for (int i = 0; i < packetLengths.Length; i++)
				{
					ushort packetDataLen = packetLengths[i];
					if (packetDataLen == 0) continue;
					var packet = reader.ReadChunk(ref ctx, packetDataLen);

					using (var temp = new DataStreamWriter(packetDataLen, Allocator.Temp))
					{
						unsafe
						{
							byte* packetPtr = packet.GetUnsafeReadOnlyPtr();
							temp.WriteBytes(packetPtr, packetDataLen);
						}
						//Debug.Log ("SendUnreliableChunksJob ChunkIndex = " + i + ", packetDataLen=" + packetDataLen);
						connection.Send(driver, temp);
					}
				}
			}
		}

        struct SendReliableChunksJob : IJob {

			[ReadOnly]
			public T driver;
			[ReadOnly]
			public NetworkConnection connection;

			public NativeArray<ushort> seqNumbers;

			[ReadOnly]
			public DataStreamWriter packetChunks;
			[ReadOnly]
			public NativeList<ushort> packetLengths;

			public NativeArray<int> uncheckedPacketCount;
			public DataStreamWriter uncheckedPacketsWriter;
			public DataStreamWriter uncheckedNewPacketsWriter;

			[ReadOnly]
			public int currentFrame;

			public unsafe void Execute () {
				var reader = new DataStreamReader (packetChunks, 0, packetChunks.Length);
				var ctx = default (DataStreamReader.Context);
				
				for (int i = 0; i < packetLengths.Length; i++) {
					ushort packetDataLen = packetLengths[i];
					if (packetDataLen == 0) continue;
					var packet = reader.ReadChunk (ref ctx, packetDataLen);

					var temp = new DataStreamWriter (packetDataLen, Allocator.Temp);
					unsafe {
						byte* packetPtr = packet.GetUnsafeReadOnlyPtr ();
						temp.WriteBytes (packetPtr, packetDataLen);
					}
					//Debug.Log ("SendReliableChunksJob ChunkIndex = " + i + ", packetDataLen=" + packetDataLen);
					connection.Send (driver, temp);

					uncheckedPacketCount[0] += 1;

					if (uncheckedPacketsWriter.Capacity < uncheckedPacketsWriter.Length + temp.Length + 6) {
						uncheckedPacketsWriter.Capacity *= 2;
					}
					uncheckedPacketsWriter.Write ((ushort)(temp.Length));
					uncheckedPacketsWriter.Write (currentFrame);
					uncheckedPacketsWriter.WriteBytes (temp.GetUnsafeReadOnlyPtr(), temp.Length);
					temp.Dispose ();
				}

				if (uncheckedPacketCount[0] > 0) {
					//相手が受け取ったSeqNumberのパケットを解放.
					int currentSeqNum = seqNumbers[(int)SeqNumberDef.SelfSeq];
					int otherAckNum = seqNumbers[(int)SeqNumberDef.OtherAck];
					int oldestSeqNum = (seqNumbers[(int)SeqNumberDef.SelfSeq] - uncheckedPacketCount[0] + 1);

					if ((oldestSeqNum < ushort.MaxValue / 2) && (otherAckNum > ushort.MaxValue / 2)) {
						otherAckNum -= ushort.MaxValue + 1;
					}

					//受け取り確認パケットの解放
					ushort releaseCount = (ushort)(otherAckNum - oldestSeqNum + 1);

					var uncheckedPacketsReader = new DataStreamReader (uncheckedPacketsWriter, 0, uncheckedPacketsWriter.Length);
					ctx = new DataStreamReader.Context ();

					//Debug.Log ($"otherAckNum={otherAckNum}, oldestSeqNum={oldestSeqNum}, releaseCount={releaseCount}, PacketCount={uncheckedPacketCount[0]}");

					int releasePos = 0;
					for (int i = 0; i < releaseCount; i++) {
						if (uncheckedPacketCount[0] > 0) {
							var size = uncheckedPacketsReader.ReadUShort (ref ctx);
							var frame = uncheckedPacketsReader.ReadInt (ref ctx);
							var chunk = uncheckedPacketsReader.ReadChunk (ref ctx, size);
							releasePos += 6 + size;
						}
					}
					uncheckedPacketsReader = new DataStreamReader (uncheckedPacketsWriter, releasePos, uncheckedPacketsWriter.Length - releasePos);
					if (uncheckedNewPacketsWriter.Capacity < uncheckedPacketsReader.Length) {
						uncheckedNewPacketsWriter.Capacity *= 2;
					}
					uncheckedNewPacketsWriter.WriteBytes (uncheckedPacketsReader.GetUnsafeReadOnlyPtr(), uncheckedPacketsReader.Length);

					if(uncheckedPacketCount[0] > releaseCount) {
						uncheckedPacketCount[0] -= releaseCount;
					} else {
						uncheckedPacketCount[0] = 0;
					}

					//受け取り確認できてないパケットを再送
					uncheckedPacketsReader = new DataStreamReader (uncheckedNewPacketsWriter, 0, uncheckedNewPacketsWriter.Length);
					ctx = new DataStreamReader.Context ();
					for (int i = 0; i < uncheckedPacketCount[0]; i++) {
						var size = uncheckedPacketsReader.ReadUShort (ref ctx);
						var frame = uncheckedPacketsReader.ReadInt (ref ctx);
						var chunk = uncheckedPacketsReader.ReadChunk (ref ctx, size);
						ushort frameCount = (ushort)(currentFrame - frame);

						//タイムアウト
						if (frameCount > NetworkLinkerConstants.TimeOutFrameCount) {
							Debug.LogWarning ("uncheckedSelfReliablePackets FrameCount TimeOut Index=" + i);
						}

						if (Mathf.IsPowerOfTwo (frameCount)) {
							//if (frameCount == 64 || frameCount == 1024) {
							//	Debug.Log ("ResendUncheckedPacket : index=" + i + ", frameCount=" + frameCount);
							//}
							//時間が経つごとに送信間隔を開ける n乗のフレームの時だけ送る
							using (var temp = new DataStreamWriter(size, Allocator.Temp)) {
								temp.WriteBytes (chunk.GetUnsafeReadOnlyPtr(), size);
								connection.Send (driver, temp);
							}
						}
					}
				}
			}
		}

		struct UpdateJob : IJob
		{
			public T driver;
			[ReadOnly]
			public NetworkConnection connection;

			public NativeArray<ushort> seqNumbers;
			public NativeArray<byte> flags;

			public DataStreamWriter savedUncheckedReliableDataStream;
			public DataStreamWriter tempUncheckedReliableDataStream;

			public NativeList<DataStreamReader> dataStreams;
			public NativeList<DataStreamReader> uncheckedreliableStreams;

			public void Execute()
			{
				DataStreamReader stream;
				NetworkEvent.Type cmd;

				//前フレームで解決できなかったbufferedからuncheckedに登録.
				if (!savedUncheckedReliableDataStream.IsCreated)
				{
					stream = new DataStreamReader(savedUncheckedReliableDataStream, 0, savedUncheckedReliableDataStream.Length);
					int offset = 0;
					while (offset < savedUncheckedReliableDataStream.Length)
					{
						var readerCtx = default(DataStreamReader.Context);
						ushort length = stream.ReadUShort(ref readerCtx);
						if (0 < length && length <= savedUncheckedReliableDataStream.Length - offset - 2)
						{
							uncheckedreliableStreams.Add(
								new DataStreamReader(savedUncheckedReliableDataStream, offset + 2, length));
							offset += length + 2;
						}
						else
						{
							break;
						}
					}
				}

				while ((cmd = connection.PopEvent(driver, out stream)) != NetworkEvent.Type.Empty)
				{
					if (cmd == NetworkEvent.Type.Connect)
					{
						flags[(int)FlagDef.IsConnected] = 1;
						//Debug.Log ("Connect : " + connection.InternalId);
					}
					else if (cmd == NetworkEvent.Type.Disconnect)
					{
						flags[(int)FlagDef.IsDisconnected] = 1;
						//Debug.Log ("Disconnect : " + connection.InternalId);
					}
					else if (cmd == NetworkEvent.Type.Data)
					{
						if (!stream.IsCreated)
						{
							continue;
						}

						var readerCtx = default(DataStreamReader.Context);
						byte qosType = stream.ReadByte(ref readerCtx);
						ushort seqNum = stream.ReadUShort(ref readerCtx);
						ushort ackNum = stream.ReadUShort(ref readerCtx);

						//if (qosType != (byte)QosType.MeasureLatency) {
						//	Debug.Log ("Recieve Data Len=" + stream.Length + ",QoS=" + qosType + ",Seq=" + seqNum + ",Ack=" + ackNum);
						//}
						//最初のregister packetはseqNumに

						bool isInitialUpdate = flags[(int)FlagDef.IsNotInitialUpdate] == 0;

						//ackNumの更新
						ushort oldAckNum = seqNumbers[(int)SeqNumberDef.OtherAck];
						if (oldAckNum < ackNum || (oldAckNum > ushort.MaxValue * 0.9f && ackNum < ushort.MaxValue * 0.1f))
						{
							seqNumbers[(int)SeqNumberDef.OtherAck] = ackNum;
						}

						switch ((QosType)qosType)
						{
							case QosType.MeasureLatency:
								long currentUnixTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
								long otherUnixTime = stream.ReadLong(ref readerCtx);
								seqNumbers[(int)SeqNumberDef.Latency] = (ushort)(currentUnixTime - otherUnixTime);
								break;
							case QosType.Unreliable:
								dataStreams.Add(stream);
								break;
							case QosType.Reliable:
								int seqNumberDiff = (int)seqNum - seqNumbers[(int)SeqNumberDef.OtherSeq];
								if (!isInitialUpdate && seqNumberDiff > 1)
								{
									//Debug.Log ("Reliable seqNumberDiff > 1 recieve");
									//順番が入れ替わってるからバッファに貯める
									if (seqNumberDiff - 2 < uncheckedreliableStreams.Length)
									{
										if (uncheckedreliableStreams[seqNumberDiff - 2].IsCreated)
										{
											uncheckedreliableStreams[seqNumberDiff - 2] = stream;
										}
									}
									else
									{
										uncheckedreliableStreams.Add(stream);
									}
								}
								else if (isInitialUpdate || (seqNumberDiff == 1 || seqNumberDiff == -ushort.MaxValue))
								{
									flags[(int)FlagDef.IsNotInitialUpdate] = 1;
									//次の順のパケットなら確定する
									seqNumbers[(int)SeqNumberDef.OtherSeq] = seqNum;
									//Debug.Log ("update OtherSeq = " + seqNumbers[(int)SeqNumberDef.OtherSeq] + " first=" + isInitialUpdate);
									//AddChunksInDataStream (stream, ref readerCtx);
									dataStreams.Add(stream);

									//順番待ちのパケットを確定する
									while (uncheckedreliableStreams.Length != 0)
									{
										if (!uncheckedreliableStreams[0].IsCreated)
										{
											IncrementSequenceNumber(SeqNumberDef.OtherSeq);
											//Debug.Log ("update OtherSeq = " + seqNumbers[(int)SeqNumberDef.OtherSeq]);
											//AddChunksInDataStream (uncheckedreliableStreams[0], ref readerCtx);
											dataStreams.Add(uncheckedreliableStreams[0]);
											uncheckedreliableStreams.RemoveAtSwapBack(0);
										}
										else
										{
											break;
										}
									}
								}
								else
								{
									//受信済みのSeqNumのパケットなら無視
									//Debug.Log ("Reliable same recieve");
								}
								break;
						}
					}
				}

				//uncheckedreliableStreamsに残ったパケットはnetworkdriverから実態が消される前にコピーしておく
				unsafe
				{
					//uncheckedreliableStreamsはsavedUncheckedReliableDataStreamに実態を持つ場合があるので
					//直接savedUncheckedReliableDataStream書き込むと実態が消えてしまうのでtempまず書く
					tempUncheckedReliableDataStream.Clear();
					for (int i = 0; i < uncheckedreliableStreams.Length; i++)
					{
						int dataLength = uncheckedreliableStreams[i].Length;
						if (tempUncheckedReliableDataStream.Capacity - tempUncheckedReliableDataStream.Length < dataLength + 2)
						{
							tempUncheckedReliableDataStream.Capacity *= 2;
						}
						byte* dataPtr = DataStreamUnsafeUtility.GetUnsafeReadOnlyPtr(uncheckedreliableStreams[i]);
						tempUncheckedReliableDataStream.Write(dataLength);
						tempUncheckedReliableDataStream.WriteBytes(dataPtr, dataLength);
					}
					savedUncheckedReliableDataStream.Clear();
					if (savedUncheckedReliableDataStream.Capacity < tempUncheckedReliableDataStream.Capacity)
					{
						savedUncheckedReliableDataStream.Capacity = tempUncheckedReliableDataStream.Capacity;
					}
					savedUncheckedReliableDataStream.WriteBytes(
						tempUncheckedReliableDataStream.GetUnsafeReadOnlyPtr(), tempUncheckedReliableDataStream.Length);
				}

				//次に自分が送るパケットでどこまで受け取ったか伝える.
				seqNumbers[(int)SeqNumberDef.SelfAck] = seqNumbers[(int)SeqNumberDef.OtherSeq];
			}

			void IncrementSequenceNumber(SeqNumberDef def)
			{
				if (seqNumbers[(int)def] == ushort.MaxValue)
				{
					seqNumbers[(int)def] = 0;
				}
				else
				{
					seqNumbers[(int)def]++;
				}
			}
		}

		public enum SeqNumberDef
		{
			SelfSeq = 0,
			SelfAck,
			OtherSeq,
			OtherAck,
			Latency,    //NativeArrayを分けるのが勿体ないのでここにいれた
		}

		public enum FlagDef
		{
			IsConnected = 0,
			IsDisconnected,
			IsNotInitialUpdate,
		}

		public delegate void OnConnectEvent(NetworkConnection connection);
		public delegate void OnDisconnectEvent(NetworkConnection connection);

		//public NetworkLinkerHandle handle { get; private set; }

		public int targetPacketSize { get; private set; }
		public int uncheckedSelfReliablePacketCount { get { return uncheckedSelfReliablePacketCountBuffer[0]; } }

		public T driver { get; private set; }
		public NetworkConnection connection { get; private set; }

		public ushort SelfSeqNumber { get { return seqNumbers[(int)SeqNumberDef.SelfSeq]; } }
		public ushort SelfAckNumber { get { return seqNumbers[(int)SeqNumberDef.SelfAck]; } }
		public ushort OtherSeqNumber { get { return seqNumbers[(int)SeqNumberDef.OtherSeq]; } }
		public ushort OtherAckNumber { get { return seqNumbers[(int)SeqNumberDef.OtherAck]; } }

		public ushort Latency { get; private set; }

		public bool IsConnected { get; private set; }
		public bool IsDisconnected { get; private set; }

		public bool CompleteConnection {
			get {
				if (!driver.IsCreated || !connection.IsCreated) return false;
				return connection.GetState(driver) == NetworkConnection.State.Connected;
			}
		}
		public bool EnableConnection { get { return connection.IsCreated; } }

		private NativeArray<ushort> seqNumbers;
		private NativeArray<byte> flags;
		private NativeArray<ushort> latencyLog;

		private DataStreamWriter uncheckedRecieveReliableDataStream;
		private DataStreamWriter tempRecieveReliableDataStream;

		public NativeList<DataStreamReader> dataStreams;

		private NativeList<DataStreamReader> uncheckedreliableStreams;

		public JobHandle LinkerJobHandle;

		private NativeArray<int> uncheckedSelfReliablePacketCountBuffer;
		private DataStreamWriter uncheckedSelfReliablePacketsWriter;
		private DataStreamWriter uncheckedNewSelfReliablePacketsWriter;

		private DataStreamWriter[] packetChunksQosTable;
		private NativeList<ushort>[] packetLengthsQosTable;
		private ushort[] chunkCountQosTable;

		public NetworkLinker(T driver, NetworkConnection connection, int targetPacketSize)
		{
			this.driver = driver;
			this.connection = connection;
			this.targetPacketSize = targetPacketSize;

			seqNumbers = new NativeArray<ushort>(5, Allocator.Persistent);
			flags = new NativeArray<byte>(3, Allocator.Persistent);
			latencyLog = new NativeArray<ushort>(16, Allocator.Persistent);

			uncheckedSelfReliablePacketCountBuffer = new NativeArray<int> (1, Allocator.Persistent);
			uncheckedSelfReliablePacketsWriter = new DataStreamWriter (ushort.MaxValue, Allocator.Persistent);
			uncheckedNewSelfReliablePacketsWriter = new DataStreamWriter (ushort.MaxValue, Allocator.Persistent);

			uncheckedRecieveReliableDataStream = new DataStreamWriter(ushort.MaxValue, Allocator.Persistent);
			tempRecieveReliableDataStream = new DataStreamWriter(ushort.MaxValue, Allocator.Persistent);
			dataStreams = new NativeList<DataStreamReader>(32, Allocator.Persistent);
			uncheckedreliableStreams = new NativeList<DataStreamReader>(32, Allocator.Persistent);

			packetChunksQosTable = new DataStreamWriter[(int)QosType.ChunkEnd - 1];
			packetLengthsQosTable = new NativeList<ushort>[(int)QosType.ChunkEnd - 1];

			for (int i = 0; i < (int)QosType.ChunkEnd - 1; i++)
			{
				packetChunksQosTable[i] = new DataStreamWriter(ushort.MaxValue, Allocator.Persistent);
				packetLengthsQosTable[i] = new NativeList<ushort>(4, Allocator.Persistent);
				packetLengthsQosTable[i].Add(default);
			}
			chunkCountQosTable = new ushort[(int)QosType.ChunkEnd - 1];

			LinkerJobHandle = default(JobHandle);
		}

		public void Dispose()
		{
			LinkerJobHandle.Complete();

			seqNumbers.Dispose();
			flags.Dispose();
			latencyLog.Dispose();
			uncheckedRecieveReliableDataStream.Dispose();
			tempRecieveReliableDataStream.Dispose();
			dataStreams.Dispose();
			uncheckedreliableStreams.Dispose();

			for (int i = 0; i < (int)QosType.ChunkEnd - 1; i++)
			{
				packetChunksQosTable[i].Dispose();
				packetLengthsQosTable[i].Dispose();
			}

			uncheckedSelfReliablePacketCountBuffer.Dispose ();
			uncheckedSelfReliablePacketsWriter.Dispose ();
			uncheckedNewSelfReliablePacketsWriter.Dispose ();
		}

		public void Reconnect(NetworkConnection connection)
		{
			this.connection = connection;
		}

		internal void SyncSeqNum(ushort selfSeqNum)
		{
			seqNumbers[(int)SeqNumberDef.SelfSeq] = selfSeqNum;
		}

		void IncrementSequenceNumber(SeqNumberDef def)
		{
			if (seqNumbers[(int)def] == ushort.MaxValue)
			{
				seqNumbers[(int)def] = 0;
			}
			else
			{
				seqNumbers[(int)def]++;
			}
		}

		public void SendUnreliable(DataStreamWriter data, bool noChunk = false)
		{
			Send(data, QosType.Unreliable, noChunk);
		}

		public ushort SendReliable(DataStreamWriter data, bool noChunk = false)
		{
			return Send(data, QosType.Reliable, noChunk);
		}

		public ushort Send(DataStreamWriter data, QosType qos, bool noChunk = false)
		{
			Assert.IsTrue (LinkerJobHandle.IsCompleted);
			if (!LinkerJobHandle.IsCompleted) {
				throw new System.NotSupportedException ("Update/LateUpdate/FixedUpdateでのみSendメソッドは利用できます");
			}
			//Debug.Log ("Send " + qos + " : dataLen=" + data.Length);

			ushort dataLength = (ushort)data.Length;

			//TODO 瞬断中にたまったパケットがcapacityを超えても保存できるようにしたい
			unsafe
			{
				byte* dataPtr = DataStreamUnsafeUtility.GetUnsafeReadOnlyPtr(data);
				var packetLengths = packetLengthsQosTable[(byte)qos - 1];
				DataStreamWriter writer;

				if (noChunk)
				{
					if (qos == QosType.Reliable)
					{
						IncrementSequenceNumber(SeqNumberDef.SelfSeq);
					}
					writer = new DataStreamWriter (7 + data.Length, Allocator.Temp);

					writer.Write((byte)qos);
					writer.Write(seqNumbers[(int)SeqNumberDef.SelfSeq]);
					writer.Write(seqNumbers[(int)SeqNumberDef.SelfAck]);
					writer.Write(dataLength);
					writer.WriteBytes(dataPtr, data.Length);
					connection.Send(driver, writer);

					if (qos == QosType.Reliable)
					{
						uncheckedSelfReliablePacketCountBuffer[0] += 1;

						if (uncheckedSelfReliablePacketsWriter.Capacity < uncheckedSelfReliablePacketsWriter.Length + writer.Length + 6) {
							uncheckedSelfReliablePacketsWriter.Capacity *= 2;
						}
						uncheckedSelfReliablePacketsWriter.Write ((ushort)(writer.Length));
						uncheckedSelfReliablePacketsWriter.Write (Time.frameCount);
						uncheckedSelfReliablePacketsWriter.WriteBytes (writer.GetUnsafeReadOnlyPtr (), writer.Length);
					}
					writer.Dispose ();
				} else
				{
					writer = packetChunksQosTable[(byte)qos - 1];

					if (writer.Capacity - writer.Length < targetPacketSize)
					{
						writer.Capacity *= 2;
					}

					ushort chunkCount = chunkCountQosTable[(int)qos - 1];
					if (packetLengths[chunkCount] + (ushort)(dataLength + 2) > targetPacketSize)
					{
						chunkCount += 1;
					}
					if (chunkCount != chunkCountQosTable[(int)qos - 1])
					{
						packetLengths.Add(default);
						chunkCountQosTable[(int)qos - 1] = chunkCount;
					}
					if (packetLengths[chunkCount] == 0)
					{
						if (qos == QosType.Reliable)
						{
							IncrementSequenceNumber(SeqNumberDef.SelfSeq);
						}
						writer.Write((byte)qos);
						writer.Write(seqNumbers[(int)SeqNumberDef.SelfSeq]);
						writer.Write(seqNumbers[(int)SeqNumberDef.SelfAck]);
						packetLengths[chunkCount] += 5;
					}
					packetLengths[chunkCount] += (ushort)(dataLength + 2);
					writer.Write(dataLength);
					writer.WriteBytes(dataPtr, data.Length);
				}
			}
			return SelfSeqNumber;
		}

		/// <summary>
		/// レイテンシ計測パケットの送信
		/// (ついでにreliableのAckNumも送る)
		/// </summary>
		public void SendMeasureLatencyPacket()
		{
			if (!EnableConnection) return;

			using (var writer = new DataStreamWriter(13, Allocator.Temp))
			{
				writer.Write((byte)QosType.MeasureLatency);
				writer.Write((ushort)0);
				writer.Write(seqNumbers[(int)SeqNumberDef.SelfAck]);
				writer.Write(System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());           //unixtime
				connection.Send(driver, writer);
			}
		}

		public void Complete()
		{
			//MainThreadのjobの完了待ち
			LinkerJobHandle.Complete();

			float ave = 0.0f;
			for (int i = latencyLog.Length - 1; i > 0; i--)
			{
				latencyLog[i] = latencyLog[i - 1];
				ave += latencyLog[i];
			}
			latencyLog[0] = seqNumbers[(int)SeqNumberDef.Latency];
			ave += latencyLog[0];
			ave /= latencyLog.Length;

			Latency = (ushort)ave;

			IsConnected = flags[(int)FlagDef.IsConnected] == 1;
			IsDisconnected = flags[(int)FlagDef.IsDisconnected] == 1;

			if (IsDisconnected)
			{
				connection = default;
			}

			if (!EnableConnection) return;

			for (int i = 0; i < packetChunksQosTable.Length; i++)
			{
				packetChunksQosTable[i].Clear();
			}
			for (int i = 0; i < packetLengthsQosTable.Length; i++)
			{
				packetLengthsQosTable[i].Clear();
				packetLengthsQosTable[i].Add(default);
			}
			for (int i = 0; i < chunkCountQosTable.Length; i++)
			{
				chunkCountQosTable[i] = 0;
			}

			uncheckedSelfReliablePacketsWriter.Clear ();
			if(uncheckedNewSelfReliablePacketsWriter.Length > 0) {
				unsafe {
					uncheckedSelfReliablePacketsWriter.WriteBytes (
						uncheckedNewSelfReliablePacketsWriter.GetUnsafeReadOnlyPtr (), uncheckedNewSelfReliablePacketsWriter.Length);
				}
			}
			uncheckedNewSelfReliablePacketsWriter.Clear ();
		}

		public JobHandle ScheduleSendChunks(JobHandle jobHandle)
		{
			if (!EnableConnection || !CompleteConnection)
			{
				return jobHandle;
			}

			var sendUnreliableChunksJob = new SendUnreliableChunksJob()
			{
				driver = driver,
				connection = connection,
				packetChunks = packetChunksQosTable[(int)QosType.Unreliable - 1],
				packetLengths = packetLengthsQosTable[(int)QosType.Unreliable - 1],
			};

			LinkerJobHandle = sendUnreliableChunksJob.Schedule(jobHandle);
			
			var sendReliableChunksJob = new SendReliableChunksJob () {
				driver = driver,
				connection = connection,
				seqNumbers = seqNumbers,
				packetChunks = packetChunksQosTable[(int)QosType.Reliable - 1],
				packetLengths = packetLengthsQosTable[(int)QosType.Reliable - 1],
				uncheckedPacketCount = uncheckedSelfReliablePacketCountBuffer,
				uncheckedPacketsWriter = uncheckedSelfReliablePacketsWriter,
				uncheckedNewPacketsWriter = uncheckedNewSelfReliablePacketsWriter,
				currentFrame = Time.frameCount,
			};

			LinkerJobHandle = sendReliableChunksJob.Schedule (LinkerJobHandle);
			return LinkerJobHandle;
		}

		public JobHandle ScheduleRecieve(JobHandle jobHandle)
		{

			flags[(int)FlagDef.IsConnected] = 0;
			flags[(int)FlagDef.IsDisconnected] = 0;

			if (!EnableConnection)
			{
				return jobHandle;
			}

			dataStreams.Clear();
			uncheckedreliableStreams.Clear();

			var updateJob = new UpdateJob()
			{
				driver = driver,
				connection = connection,
				flags = flags,
				seqNumbers = seqNumbers,
				savedUncheckedReliableDataStream = uncheckedRecieveReliableDataStream,
				tempUncheckedReliableDataStream = tempRecieveReliableDataStream,
				dataStreams = dataStreams,
				uncheckedreliableStreams = uncheckedreliableStreams,
			};

			LinkerJobHandle = updateJob.Schedule(jobHandle);
			return LinkerJobHandle;
		}
	}
}
