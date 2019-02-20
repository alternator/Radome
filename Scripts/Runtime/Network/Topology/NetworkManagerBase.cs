using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.LowLevel.Unsafe;
using UnityEngine;
using UdpCNetworkDriver = Unity.Networking.Transport.BasicNetworkDriver<Unity.Networking.Transport.IPv4UDPSocket>;

namespace ICKX.Radome {

    public abstract class NetworkManagerBase : System.IDisposable {

        public enum State : byte {
            Offline = 0,
            Connecting,
            Online,
            Disconnecting,
        }

        public delegate void OnReconnectPlayerEvent (ushort id);
        public delegate void OnDisconnectPlayerEvent (ushort id);
        public delegate void OnRegisterPlayerEvent (ushort id);
        public delegate void OnUnregisterPlayerEvent (ushort id);
        public delegate void OnRecievePacketEvent (ushort senderPlayerId, byte type, DataStreamReader stream, DataStreamReader.Context ctx);

		public const ushort ServerPlayerId = 0;
		public const int AdressHeaderSize = 4;

        public State state { get; protected set; } = State.Offline;

        public ushort playerId { get; protected set; }
        public bool isLeader { get { return playerId == 0; } }
        public bool isJobProgressing { get; protected set; }

        public long leaderStatTime { get; protected set; }

        protected NativeList<byte> activePlayerIdList;

        protected JobHandle jobHandle;
        public UdpCNetworkDriver driver;

		//public event System.Action OnConnectionFailed = null;
		public event OnReconnectPlayerEvent OnReconnectPlayer = null;
		public event OnDisconnectPlayerEvent OnDisconnectPlayer = null;
		public event OnRegisterPlayerEvent OnRegisterPlayer = null;
		public event OnUnregisterPlayerEvent OnUnregisterPlayer = null;
        public event OnRecievePacketEvent OnRecievePacket = null;

        public NetworkManagerBase () {
            activePlayerIdList = new NativeList<byte> (8, Allocator.Persistent);
        }

        public virtual void Dispose () {
            activePlayerIdList.Dispose ();
        }

        public ushort GetPlayerCount () {
            ushort count = 0;
            for (int i=0; i<activePlayerIdList.Length;i++) {
                byte bits = activePlayerIdList[i];

                bits = (byte)((bits & 0x55) + (bits >> 1 & 0x55));
                bits = (byte)((bits & 0x33) + (bits >> 2 & 0x33));
                count += (byte)((bits & 0x0f) + (bits >> 4));
            }
            return count;
        }

        protected ushort GetDeactivePlayerId () {
            ushort id = 0;
            while (IsActivePlayerId (id)) id++;
            return id;
        }

        public bool IsActivePlayerId (ushort playerId) {
            ushort index = (ushort)(playerId / 8);
            if (index >= activePlayerIdList.Length) {
                return false;
            }else {
                byte bit = (byte)(1 << (playerId % 8));
                return (activePlayerIdList[index] & bit) != 0;
            }
        }

        protected void RegisterPlayerId (ushort id) {
            ushort index = (ushort)(id / 8);
            byte bit = (byte)(1 << (id % 8));
            if (index > activePlayerIdList.Length) {
                throw new System.Exception ("Register Failed, id=" + id + ", active=" + activePlayerIdList.Length);
            } else if (index == activePlayerIdList.Length) {
                activePlayerIdList.Add(bit);
            } else {
                activePlayerIdList[index] = (byte)(activePlayerIdList[index] | bit);
            }
        }

        protected void UnregisterPlayerId (ushort id) {
            ushort index = (ushort)Mathf.CeilToInt (id / 8);
            byte bit = (byte)(1 << (id % 8));
            if (index >= activePlayerIdList.Length) {
                throw new System.Exception ("Unregister Failed, id=" + id + ", active=" + activePlayerIdList.Length);
            } else {
                activePlayerIdList[index] = (byte)(activePlayerIdList[index] & ~bit);
            }
        }

        protected DataStreamWriter CreateSendPacket (DataStreamWriter data, QosType qos, ushort targetId, ushort senderId) {
            unsafe {
                byte* dataPtr = DataStreamUnsafeUtility.GetUnsafeReadOnlyPtr (data);
                ushort dataLength = (ushort)data.Length;
                var writer = new DataStreamWriter (data.Length + 4, Allocator.Temp);
                writer.Write (targetId);
                writer.Write (senderId);
                writer.WriteBytes (dataPtr, data.Length);
                return writer;
            }
        }

		protected void ExecOnReconnectPlayer (ushort id) {
			OnReconnectPlayer?.Invoke (id);
		}

		protected void ExecOnDisconnectPlayer (ushort id) {
			OnDisconnectPlayer?.Invoke (id);
		}

		protected void ExecOnRegisterPlayer (ushort id) {
			OnRegisterPlayer?.Invoke (id);
		}

		protected void ExecOnUnregisterPlayer (ushort id) {
			OnUnregisterPlayer?.Invoke (id);
		}

		protected void ExecOnRecievePacket (ushort senderPlayerId, byte type, DataStreamReader stream, DataStreamReader.Context ctx) {
            OnRecievePacket?.Invoke (senderPlayerId, type, stream, ctx);
        }

		protected bool ReadQosHeader (DataStreamReader stream, ref DataStreamReader.Context ctx, out QosType qosType, out ushort seqNum, out ushort ackNum) {
			if (!stream.IsCreated) {
				qosType = QosType.Empty;
				seqNum = 0;
				ackNum = 0;
				return false;
			}
			qosType = (QosType)stream.ReadByte (ref ctx);
			seqNum = stream.ReadUShort (ref ctx);
			ackNum = stream.ReadUShort (ref ctx);
			return true;
		}

		protected bool ReadChunkHeader (DataStreamReader stream, ref DataStreamReader.Context ctx
				, out DataStreamReader chunk, out DataStreamReader.Context ctx2, out ushort targetPlayerId, out ushort senderPlayerId) {

			chunk = default;
			ctx2 = default;
			targetPlayerId = 0;
			senderPlayerId = 0;

			int pos = stream.GetBytesRead (ref ctx);
			if (pos >= stream.Length) return false;
			ushort dataLength = stream.ReadUShort (ref ctx);
			if (dataLength == 0) return false;

			chunk = stream.ReadChunk (ref ctx, dataLength);
			targetPlayerId = chunk.ReadUShort (ref ctx2);
			senderPlayerId = chunk.ReadUShort (ref ctx2);
			return true;
		}

		protected virtual void RecieveData (ushort senderPlayerId, byte type, DataStreamReader chunk, DataStreamReader.Context ctx) {
			//switch ((BuiltInPacket.Type)type) {
			//	case BuiltInPacket.Type.:
			//		break;
			//	default:
			//		ExecOnRecievePacket (senderPlayerId, type, chunk, ctx);
			//		break;
			//}
			ExecOnRecievePacket (senderPlayerId, type, chunk, ctx);
		}

		public abstract void OnFirstUpdate ();
        public abstract void OnLastUpdate ();

        public abstract bool isFullMesh { get; }
		public abstract ushort Send (ushort targetPlayerId, DataStreamWriter data, QosType qos, bool noChunk = false);
		public abstract ushort Send (NativeList<ushort> playerIdList, DataStreamWriter data, QosType qos, bool noChunk = false);
		public abstract void Brodcast (DataStreamWriter data, QosType qos, bool noChunk = false);
        public abstract void SendReliable (ushort targetPlayerId, DataStreamWriter data, QosType qos, System.Action<ushort> onComplete, bool noChunk = false);
        public abstract void BrodcastReliable (DataStreamWriter data, QosType qos, System.Action<ushort> onComplete, bool noChunk = false);
        public abstract void Stop ();
        public abstract void StopComplete ();
    }
}
