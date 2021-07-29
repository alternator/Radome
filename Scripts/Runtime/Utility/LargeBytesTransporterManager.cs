using System.Collections;
using System.Collections.Generic;
using Unity.Networking.Transport;
using UnityEngine;
using UpdateLoop = UnityEngine.PlayerLoop.Update;
#if !UNITY_2019_3_OR_NEWER
using UnityEngine.Experimental.LowLevel;
#endif
using System.Security.Cryptography;
using Unity.Collections;
using System.IO;

namespace ICKX.Radome {
	
	public class LargeBytesTransporter : TransporterBase {

		public string name;
		public byte[] data;

		public LargeBytesTransporter (int hash, byte[] data) : base (hash) {
			this.data = data;
		}
	}

	public class LargeBytesTransporterManager : TransporterBaseManager<LargeBytesTransporterManager, LargeBytesTransporter> {

		public override byte Type => (byte)TransporterType.LargeBytes;
		
		public bool IsSending(string name)
		{
			foreach (var transporter in _sendTransporterTable.Values)
			{
				if (transporter.name == name)
				{
					return true;
				}
			}
			return false;
		}

		public bool IsReceiving(string name)
		{
			foreach (var transporter in _recieveTransporterTable.Values)
			{
				if (transporter.name == name)
				{
					return true;
				}
			}
			return false;
		}

		public int Send (ushort playerId, string name, byte[] data) {
			if (data.Length <= NetworkLinkerConstants.MaxPacketSize - HeaderSize) {
				Debug.LogError ("MTU以下のサイズのデータは送れません");
				return 0;
			}

			int hash = ByteToHash (data);
			var transporter = new LargeBytesTransporter (hash, data);
			transporter.targetPlayerId = playerId;
			transporter.name = name;

            int nameByteCount = NativeStreamWriter.GetByteSizeStr (name);
			int dataSize = NetworkLinkerConstants.MaxPacketSize - HeaderSize - 13 - nameByteCount;
			unsafe {
				fixed ( byte* dataPtr = &data[transporter.pos]) {
					using (var array = new NativeArray<byte>(dataSize + 13 + nameByteCount, Unity.Collections.Allocator.Temp))
					{
						var writer = new NativeStreamWriter(array);
						writer.WriteByte ((byte)BuiltInPacket.Type.DataTransporter);
						writer.WriteByte((byte)TransporterType.LargeBytes);
						writer.WriteInt (hash);
						writer.WriteByte((byte)FlagDef.Start);
						writer.WriteString (name);
						writer.WriteInt(data.Length);
						writer.WriteBytes (dataPtr, dataSize);

						if (transporter.targetPlayerId == NetworkLinkerConstants.BroadcastId)
						{
							NetworkManager.Broadcast(writer, QosType.Reliable, true);
						}
						else
						{
							NetworkManager.Send(transporter.targetPlayerId, writer, QosType.Reliable);
						}
					}
				}
			}
			transporter.pos += dataSize;
			_sendTransporterTable[hash] = transporter;

			return hash;
		}

		public int Broadcast (string name, byte[] data) {
			return Send (ushort.MaxValue, name, data);
		}

		List<int> removeTransporterList = new List<int> ();

		protected override void SendFragmentData () {
			if (_sendTransporterTable == null) return;

			removeTransporterList.Clear ();

			foreach (var pair in _sendTransporterTable) {
				var transporter = pair.Value;

				int sendAmount = 0;
				while (sendAmount < SendBytePerFrame) {
					FlagDef flag = FlagDef.None;
					int dataSize = NetworkLinkerConstants.MaxPacketSize - HeaderSize - 7;

					if (transporter.pos + dataSize > transporter.data.Length) {
						flag = FlagDef.Complete;
						dataSize = transporter.data.Length - transporter.pos;
						//Debug.Log ("Complete");
					}
					unsafe {
						fixed (byte* dataPtr = &transporter.data[transporter.pos]) {
							using (var array = new NativeArray<byte>(dataSize + 7, Unity.Collections.Allocator.Temp))
							{
								var writer = new NativeStreamWriter(array);
								writer.WriteByte ((byte)BuiltInPacket.Type.DataTransporter);
								writer.WriteByte ((byte)TransporterType.LargeBytes);
								writer.WriteInt(transporter.hash);
								writer.WriteByte ((byte)flag);
								writer.WriteBytes (dataPtr, dataSize);

								if (transporter.targetPlayerId == NetworkLinkerConstants.BroadcastId)
								{
									NetworkManager.Broadcast(writer, QosType.Reliable, true);
								}
								else
								{
									NetworkManager.Send(transporter.targetPlayerId, writer, QosType.Reliable);
								}
							}
						}
					}
					transporter.pos += dataSize;
					sendAmount += dataSize;
					if(flag == FlagDef.Complete) {
						removeTransporterList.Add (transporter.hash);
						ExeceOnSendComplete (transporter, true);
						break;
					}
                    //Debug.Log("SendFragmentData Hash=" + transporter.hash + ", Pos" + transporter.pos + " : " + sendAmount + ": " + Time.frameCount );
                }
            }


			foreach (int hash in removeTransporterList) {
				_sendTransporterTable.Remove (hash);
			}
		}

		protected override LargeBytesTransporter RecieveStart (int hash, NativeStreamReader stream) {
			string name = stream.ReadString ();
			int dataSize = stream.ReadInt ();
			var transporter = new LargeBytesTransporter (hash, new byte[dataSize]);
			transporter.name = name;
			int fragmentSize = stream.Length - stream.GetBytesRead();
			unsafe {
				fixed (byte* data = &transporter.data[transporter.pos]) {
					stream.ReadBytes (data, fragmentSize);
					transporter.pos += fragmentSize;
				}
			}
			return transporter;
		}

		protected override void RecieveFragmentData (int hash, NativeStreamReader stream, LargeBytesTransporter transporter) {
			int fragmentSize = stream.Length - stream.GetBytesRead ();
			unsafe {
				fixed (byte* data = &transporter.data[transporter.pos]) {
					stream.ReadBytes (data, fragmentSize);
					transporter.pos += fragmentSize;
				}
			}
			//Debug.Log ("RecieveFragmentData Hash=" + transporter.hash + ", Pos" + transporter.pos);
		}

		protected override void RecieveComplete (int hash, LargeBytesTransporter transporter) {
			if(transporter.hash != ByteToHash(transporter.data)) {
				Debug.LogError ("ファイル送信失敗");
				ExecOnRecieveComplete (transporter, false);
			}else {
				//Debug.Log ("RecieveComplete LargeData : " + transporter.hash);
				ExecOnRecieveComplete (transporter, true);
			}
		}
	}
}