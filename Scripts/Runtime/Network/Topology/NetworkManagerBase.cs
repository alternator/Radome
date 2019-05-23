using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.LowLevel.Unsafe;
using UnityEngine;

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

    public enum ConnectionFlagDef
    {
        IsConnected = 0,
        IsDisconnected,
        IsNotInitialUpdate,
        End
    }

    [System.Serializable]
    public class DefaultPlayerInfo
    {
        public ushort PlayerId;
        public ulong UniqueId;

        public DefaultPlayerInfo() { }

        public virtual int PacketSize => 12;

        public DataStreamWriter CreateUpdatePlayerInfoPacket(byte packetType)
        {
            var updatePlayerPacket = new DataStreamWriter(PacketSize + 1, Allocator.Temp);
            updatePlayerPacket.Write(packetType);
            AppendPlayerInfoPacket(ref updatePlayerPacket);
            return updatePlayerPacket;
        }

        public virtual void AppendPlayerInfoPacket(ref DataStreamWriter writer)
        {
            writer.Write(PlayerId);
            writer.Write(UniqueId);
        }

        public virtual void Deserialize(ref DataStreamReader chunk, ref DataStreamReader.Context ctx2)
        {
            PlayerId = chunk.ReadUShort(ref ctx2);
            UniqueId = chunk.ReadULong(ref ctx2);
        }
    }

    public static class NetworkLinkerConstants
    {
        public const ushort BroadcastId = ushort.MaxValue;
        public const ushort MulticastId = ushort.MaxValue - 1;

        public const int TimeOutFrameCount = 300;
        public const int HeaderSize = 1 + 2 + 2 + 2 + 2;
    }
    
    /// <summary>
    /// NetworkManagerの基底クラス
    /// 
    /// PlayerID : 現在接続しているすべてのクライアントで共通の2byteの識別番号 
    /// UniqueID : すべてのユーザーで重複しないID (Packetに8byteも書き込まないようにPlayerIDをなるべく使う)
    /// </summary>
    public abstract class NetworkManagerBase : System.IDisposable
    {
        //[System.Serializable]
        public class DefaultConnectionInfo
        {
            public NetworkConnection.State State;
            public float DisconnectTime;

            public DefaultConnectionInfo(NetworkConnection.State state)
            {
                this.State = state;
                this.DisconnectTime = 0.0f;
            }
        }

        public delegate void OnConnectEvent();
        public delegate void OnDisconnectAllEvent(byte errorCode);
        public delegate void OnConnectFailedEvent(byte errorCode);

        public delegate void OnReconnectPlayerEvent(ushort playerId, ulong uniqueId);
        public delegate void OnDisconnectPlayerEvent(ushort playerId, ulong uniqueId);
        public delegate void OnRegisterPlayerEvent(ushort playerId, ulong uniqueId);
        public delegate void OnUnregisterPlayerEvent(ushort playerId, ulong uniqueId);
        public delegate void OnRecievePacketEvent(ushort senderPlayerId, ulong uniqueId, byte type, DataStreamReader stream, DataStreamReader.Context ctx);

        public const ushort ServerPlayerId = 0;

        protected List<byte> _ActivePlayerIdList = new List<byte>(16);
        
        protected DataStreamWriter _SinglePacketBuffer;
        protected ChuckPacketManager _BroadcastRudpChunkedPacketManager;
        protected ChuckPacketManager _BroadcastUdpChunkedPacketManager;

        protected Dictionary<ulong, ushort> _UniquePlayerIdTable = new Dictionary<ulong, ushort>();
        public IReadOnlyDictionary<ulong, ushort> UniquePlayerIdTable { get { return _UniquePlayerIdTable; } }

        public NetworkConnection.State NetwrokState { get; protected set; } = NetworkConnection.State.Disconnected;

        public DefaultPlayerInfo MyPlayerInfo { get; protected set; }

        public ushort MyPlayerId { get { return MyPlayerInfo.PlayerId; } }
        public bool IsLeader { get { return MyPlayerId == 0; } }
        public bool IsStopRequest { get; protected set; }

        public long LeaderStatTime { get; protected set; }

        public JobHandle JobHandle { get; protected set; }

        //public event System.Action OnConnectionFailed = null;
        public event OnConnectEvent OnConnect = null;
        public event OnDisconnectAllEvent OnDisconnectAll = null;
        public event OnConnectFailedEvent OnConnectFailed = null;
        public event OnReconnectPlayerEvent OnReconnectPlayer = null;
        public event OnDisconnectPlayerEvent OnDisconnectPlayer = null;
        public event OnRegisterPlayerEvent OnRegisterPlayer = null;
        public event OnUnregisterPlayerEvent OnUnregisterPlayer = null;
        public event OnRecievePacketEvent OnRecievePacket = null;

        protected bool _IsDispose = false;

        public NetworkManagerBase()
        {
            _SinglePacketBuffer = new DataStreamWriter(ushort.MaxValue, Allocator.Persistent);
            _BroadcastRudpChunkedPacketManager = new ChuckPacketManager(800);
            _BroadcastUdpChunkedPacketManager = new ChuckPacketManager(1000);
        }

        public virtual void Dispose()
        {
            if (_IsDispose) return;
            _IsDispose = true;
            JobHandle.Complete();
            _SinglePacketBuffer.Dispose();
            _BroadcastRudpChunkedPacketManager.Dispose();
            _BroadcastUdpChunkedPacketManager.Dispose();
            Debug.Log("Dispose");
        }

        public virtual void Stop()
        {
            if (NetwrokState == NetworkConnection.State.Disconnected)
            {
                Debug.LogError("Start Failed  currentState = " + NetwrokState);
                return;
            }
            JobHandle.Complete();
        }

        protected virtual void StopComplete()
        {
            if (NetwrokState == NetworkConnection.State.Disconnected)
            {
                Debug.LogError("Start Failed  currentState = " + NetwrokState);
                return;
            }
            JobHandle.Complete();
            _SinglePacketBuffer.Clear();
            _BroadcastRudpChunkedPacketManager.Clear();
            _BroadcastUdpChunkedPacketManager.Clear();
            _UniquePlayerIdTable.Clear();
            _ActivePlayerIdList.Clear();
            Debug.Log("StopComplete");
        }

        public ushort GetPlayerCount()
        {
            ushort count = 0;
            for (int i = 0; i < _ActivePlayerIdList.Count; i++)
            {
                byte bits = _ActivePlayerIdList[i];

                bits = (byte)((bits & 0x55) + (bits >> 1 & 0x55));
                bits = (byte)((bits & 0x33) + (bits >> 2 & 0x33));
                count += (byte)((bits & 0x0f) + (bits >> 4));
            }
            return count;
        }

        public ushort GetLastPlayerId ()
        {
            ushort count = 0;
            for (ushort i = 0; i < _ActivePlayerIdList.Count * 8; i++)
            {
                if(IsActivePlayerId(i))
                {
                    count = i;
                }
            }
            return count;
        }

        protected ushort GetDeactivePlayerId()
        {
            ushort id = 0;
            while (IsActivePlayerId(id)) id++;
            return id;
        }

        public bool IsActivePlayerId(ushort playerId)
        {
            ushort index = (ushort)(playerId / 8);
            if (index >= _ActivePlayerIdList.Count)
            {
                return false;
            }
            else
            {
                byte bit = (byte)(1 << (playerId % 8));
                return (_ActivePlayerIdList[index] & bit) != 0;
            }
        }

        public abstract DefaultConnectionInfo GetConnectionInfo(ushort playerId);
        public abstract DefaultConnectionInfo GetConnectionInfo(ulong uniqueId);
        public abstract DefaultPlayerInfo GetPlayerInfo(ushort playerId);
        public abstract DefaultPlayerInfo GetPlayerInfo(ulong uniqueId);
        public abstract bool GetUniqueId(ushort playerId, out ulong uniqueId);
        public abstract bool GetPlayerId(ulong uniqueId, out ushort playerId);

        protected virtual void RegisterPlayerId(ushort playerId, ulong uniqueId)
        {
            _UniquePlayerIdTable[uniqueId] = playerId;

            ushort index = (ushort)(playerId / 8);
            byte bit = (byte)(1 << (playerId % 8));
            if (index > _ActivePlayerIdList.Count)
            {
                throw new System.Exception("Register Failed, id=" + playerId + ", active=" + _ActivePlayerIdList.Count);
            }
            else if (index == _ActivePlayerIdList.Count)
            {
                _ActivePlayerIdList.Add(bit);
            }
            else
            {
                _ActivePlayerIdList[index] = (byte)(_ActivePlayerIdList[index] | bit);
            }
        }

        protected virtual void UnregisterPlayerId(ushort playerId, ulong uniqueId)
        {
            ushort index = (ushort)Mathf.CeilToInt(playerId / 8);
            byte bit = (byte)(1 << (playerId % 8));
            if (index >= _ActivePlayerIdList.Count)
            {
                //throw new System.Exception("Unregister Failed, id=" + playerId + ", active=" + _ActivePlayerIdList.Count);
            }
            else
            {
                _ActivePlayerIdList[index] = (byte)(_ActivePlayerIdList[index] & ~bit);
            }
        }

        protected void ExecOnConnect()
        {
            OnConnect?.Invoke();
        }

        protected void ExecOnDisconnectAll(byte errorCode)
        {
            OnDisconnectAll?.Invoke(errorCode);
        }

        protected void ExecOnConnectFailed(byte errorCode)
        {
            OnConnectFailed?.Invoke(errorCode);
        }

        protected void ExecOnReconnectPlayer(ushort playerId, ulong uniqueId)
        {
            OnReconnectPlayer?.Invoke(playerId, uniqueId);
        }

        protected void ExecOnDisconnectPlayer(ushort iplayerIdd, ulong uniqueId)
        {
            OnDisconnectPlayer?.Invoke(iplayerIdd, uniqueId);
        }

        protected void ExecOnRegisterPlayer(ushort playerId, ulong uniqueId)
        {
            OnRegisterPlayer?.Invoke(playerId, uniqueId);
        }

        protected void ExecOnUnregisterPlayer(ushort id, ulong uniqueId)
        {
            OnUnregisterPlayer?.Invoke(id, uniqueId);
        }

        protected void ExecOnRecievePacket(ushort senderPlayerId, ulong senderUniqueId, byte type, DataStreamReader stream, DataStreamReader.Context ctx)
        {
            OnRecievePacket?.Invoke(senderPlayerId, senderUniqueId, type, stream, ctx);
        }
        
        protected virtual void RecieveData(ushort senderPlayerId, ulong senderUniqueId, byte type, DataStreamReader chunk, DataStreamReader.Context ctx)
        {
            ExecOnRecievePacket(senderPlayerId, senderUniqueId, type, chunk, ctx);
        }

        public abstract bool IsFullMesh { get; }
        public abstract IReadOnlyList<DefaultPlayerInfo> PlayerInfoList { get; }

        public abstract void OnFirstUpdate();
        public abstract void OnLastUpdate();

        public abstract void Send(ushort targetPlayerId, DataStreamWriter data, QosType qos);
        public abstract void Send(ulong targetUniqueId, DataStreamWriter data, QosType qos);
        public abstract void Multicast(NativeList<ushort> playerIdList, DataStreamWriter data, QosType qos);
        public abstract void Multicast(NativeList<ulong> uniqueIdList, DataStreamWriter data, QosType qos);
        public abstract void Broadcast(DataStreamWriter data, QosType qos, bool noChunk = false);
    }
}
