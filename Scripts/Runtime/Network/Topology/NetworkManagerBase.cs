using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.LowLevel.Unsafe;
using UnityEngine;

namespace ICKX.Radome {

	[System.Serializable]
	public abstract class DefaultPlayerInfo {

		//public ushort playerId;

		public DefaultPlayerInfo () { }
        
		public virtual int PacketSize => 3;

		public virtual DataStreamWriter CreateUpdatePlayerInfoPacket (ushort id) {
			var updatePlayerPacket = new DataStreamWriter (PacketSize, Allocator.Temp);
			updatePlayerPacket.Write ((byte)BuiltInPacket.Type.UpdatePlayerInfo);
			updatePlayerPacket.Write (id);
			return updatePlayerPacket;
		}

		public virtual void Deserialize (ref DataStreamReader chunk, ref DataStreamReader.Context ctx2) {

		}
	}

	public abstract class NetworkManagerBase : System.IDisposable {

		[System.Serializable]
		public struct ConnectionInfo {
			public byte _isCreated;
			public State state;
			public float disconnectTime;

			public ConnectionInfo(State state) {
				this._isCreated = 1;
				this.state = state;
				this.disconnectTime = 0.0f;
			}

			public bool isCreated { get { return _isCreated != 0; } }
		}

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

		public State state { get; protected set; } = State.Offline;

        public ushort playerId { get; protected set; }
        public bool isLeader { get { return playerId == 0; } }
        public bool isJobProgressing { get; protected set; }

        public long leaderStatTime { get; protected set; }

        protected List<byte> activePlayerIdList = new List<byte> (16);

		public List<ConnectionInfo> activeConnectionInfoList = new List<ConnectionInfo> (16);

		protected JobHandle jobHandle;

		//public event System.Action OnConnectionFailed = null;
		public event OnReconnectPlayerEvent OnReconnectPlayer = null;
		public event OnDisconnectPlayerEvent OnDisconnectPlayer = null;
		public event OnRegisterPlayerEvent OnRegisterPlayer = null;
		public event OnUnregisterPlayerEvent OnUnregisterPlayer = null;
        public event OnRecievePacketEvent OnRecievePacket = null;

        public NetworkManagerBase () {
		}

        public virtual void Dispose () {
        }

        public ushort GetPlayerCount () {
            ushort count = 0;
            for (int i=0; i<activePlayerIdList.Count;i++) {
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
            if (index >= activePlayerIdList.Count) {
                return false;
            }else {
                byte bit = (byte)(1 << (playerId % 8));
                return (activePlayerIdList[index] & bit) != 0;
            }
        }

        protected virtual void RegisterPlayerId (ushort id) {
            ushort index = (ushort)(id / 8);
            byte bit = (byte)(1 << (id % 8));
            if (index > activePlayerIdList.Count) {
                throw new System.Exception ("Register Failed, id=" + id + ", active=" + activePlayerIdList.Count);
            } else if (index == activePlayerIdList.Count) {
                activePlayerIdList.Add(bit);
            } else {
                activePlayerIdList[index] = (byte)(activePlayerIdList[index] | bit);
            }
        }

        protected virtual void UnregisterPlayerId (ushort id) {
            ushort index = (ushort)Mathf.CeilToInt (id / 8);
            byte bit = (byte)(1 << (id % 8));
            if (index >= activePlayerIdList.Count) {
                throw new System.Exception ("Unregister Failed, id=" + id + ", active=" + activePlayerIdList.Count);
            } else {
                activePlayerIdList[index] = (byte)(activePlayerIdList[index] & ~bit);
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

		protected bool ReadQosHeader (DataStreamReader stream, ref DataStreamReader.Context ctx, out QosType qosType
				, out ushort seqNum, out ushort ackNum, out ushort targetPlayerId, out ushort senderPlayerId) {
			if (!stream.IsCreated) {
				qosType = QosType.Empty;
				seqNum = 0;
				ackNum = 0;
				targetPlayerId = 0;
				senderPlayerId = 0;
				return false;
			}
			qosType = (QosType)stream.ReadByte (ref ctx);
			seqNum = stream.ReadUShort (ref ctx);
			ackNum = stream.ReadUShort (ref ctx);
			targetPlayerId = stream.ReadUShort (ref ctx);
			senderPlayerId = stream.ReadUShort (ref ctx);
			return true;
		}

		protected bool ReadChunkHeader (DataStreamReader stream, ref DataStreamReader.Context ctx
				, out DataStreamReader chunk, out DataStreamReader.Context ctx2) {

			chunk = default;
			ctx2 = default;

			int pos = stream.GetBytesRead (ref ctx);
			if (pos >= stream.Length) return false;
			ushort dataLength = stream.ReadUShort (ref ctx);
			//Debug.Log ("ReadChunkHeader : " + dataLength + " : " + stream.Length + " : " + pos);
			if (dataLength == 0 || pos + dataLength >= stream.Length) return false;
			chunk = stream.ReadChunk (ref ctx, dataLength);
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
		public abstract ushort Send (ushort targetPlayerId, DataStreamWriter data, QosType qos);
		public abstract void Multicast (NativeList<ushort> playerIdList, DataStreamWriter data, QosType qos);
		public abstract void Brodcast (DataStreamWriter data, QosType qos, bool noChunk = false);
        public abstract void Stop ();
        protected abstract void StopComplete ();
    }
}
