using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Networking.Transport.LowLevel.Unsafe;
using UnityEngine;

namespace ICKX.Radome
{
    public abstract class SCNetowrkManagerBase<Driver, PlayerInfo> : NetworkManagerBase
            where Driver : struct, INetworkDriver where PlayerInfo : DefaultPlayerInfo, new()
    {
        public class SCConnectionInfo : NetworkManagerBase.DefaultConnectionInfo
        {
            public SCConnectionInfo(NetworkConnection.State state) : base(state)
            {
            }
        }

        public override bool IsFullMesh => false;

        public Driver NetworkDriver;
        protected NativeArray<NetworkPipeline> _QosPipelines;

        protected List<SCConnectionInfo> _ActiveConnectionInfoList = new List<SCConnectionInfo>(16);
        public IReadOnlyList<SCConnectionInfo> ActiveConnectionInfoList { get { return _ActiveConnectionInfoList; } }

        protected List<PlayerInfo> _ActivePlayerInfoList = new List<PlayerInfo>(16);
        public IReadOnlyList<PlayerInfo> ActivePlayerInfoList { get { return _ActivePlayerInfoList; } }

        public override IReadOnlyList<DefaultPlayerInfo> PlayerInfoList => ActivePlayerInfoList;

        protected bool _IsFirstUpdateComplete = false;

        public SCNetowrkManagerBase(PlayerInfo playerInfo) : base()
        {
            MyPlayerInfo = playerInfo;
            _QosPipelines = new NativeArray<NetworkPipeline>((int)QosType.ChunkEnd, Allocator.Persistent);
        }

        public override void Dispose()
        {
            if (_IsDispose) return;
            JobHandle.Complete();

            if (NetworkDriver.IsCreated)
            {
                NetworkDriver.Dispose();
            }
            _QosPipelines.Dispose();
            base.Dispose();
        }

        /// <summary>
        /// クライアント接続停止
        /// </summary>
        public override void Stop()
        {
            base.Stop();
            if (NetwrokState == NetworkConnection.State.Disconnected)
            {
                Debug.LogError("Start Failed  currentState = " + NetwrokState);
                return;
            }
            JobHandle.Complete();

            NetwrokState = NetworkConnection.State.AwaitingResponse;
            IsStopRequest = true;
            
            Debug.Log("Stop");
        }

        // サーバーから切断されたらLinkerを破棄して停止
        protected override void StopComplete()
        {
            base.StopComplete();
            if (NetwrokState == NetworkConnection.State.Disconnected)
            {
                Debug.LogError("Start Failed  currentState = " + NetwrokState);
                return;
            }
            JobHandle.Complete();

            MyPlayerInfo.PlayerId = 0;
            IsStopRequest = false;
            _IsFirstUpdateComplete = false;

            _ActiveConnectionInfoList.Clear();
            _ActivePlayerInfoList.Clear();
        }
        
        protected unsafe void SendSingle(ushort targetPlayerId, DataStreamWriter data, QosType qos)
        {
            byte* dataPtr = DataStreamUnsafeUtility.GetUnsafeReadOnlyPtr(data);

            if (_SinglePacketBuffer.Length + data.Length + 5 >= _SinglePacketBuffer.Capacity)
            {
                _SinglePacketBuffer.Capacity *= 2;
            }
            _SinglePacketBuffer.Write((byte)qos);
            _SinglePacketBuffer.Write(targetPlayerId);
            _SinglePacketBuffer.Write((ushort)data.Length);
            _SinglePacketBuffer.WriteBytes(dataPtr, data.Length);
        }

        public override void Send(ulong targetUniqueId, DataStreamWriter data, QosType qos)
        {
            if (_UniquePlayerIdTable.TryGetValue(targetUniqueId, out var playerId))
            {
                Send(playerId, data, qos);
            }
        }

        /// <summary>
        /// Player1人にパケットを送信
        /// </summary>
        public override void Send(ushort targetPlayerId, DataStreamWriter data, QosType qos)
        {
            //Debug.Log($"Sennd {targetPlayerId} Len={data.Length} qos={qos}");
            if (NetwrokState == NetworkConnection.State.Disconnected)
            {
                Debug.LogError("Send Failed : NetworkConnection.State.Disconnected");
                return;
            }

            SendSingle(targetPlayerId, data, qos);
        }

        /// <summary>
        /// 複数のPlayerにパケットを送信
        /// </summary>
        public override unsafe void Multicast(NativeList<ushort> playerIdList, DataStreamWriter data, QosType qos)
        {
            //Debug.Log($"Sennd {playerIdList.Length} Len={data.Length} qos={qos}");
            if (NetwrokState == NetworkConnection.State.Disconnected)
            {
                Debug.LogError("Send Failed : NetworkConnection.State.Disconnected");
                return;
            }
            if (_SinglePacketBuffer.Length + data.Length + 7 + 2 * playerIdList.Length >= _SinglePacketBuffer.Capacity)
            {
                _SinglePacketBuffer.Capacity *= 2;
            }

            byte* dataPtr = DataStreamUnsafeUtility.GetUnsafeReadOnlyPtr(data);
            ushort dataLength = (ushort)data.Length;

            _SinglePacketBuffer.Write((byte)qos);
            _SinglePacketBuffer.Write(NetworkLinkerConstants.MulticastId);

            _SinglePacketBuffer.Write((ushort)playerIdList.Length);
            for (int i = 0; i < playerIdList.Length; i++)
            {
                _SinglePacketBuffer.Write(playerIdList[i]);
            }
            _SinglePacketBuffer.Write((ushort)data.Length);
            _SinglePacketBuffer.WriteBytes(dataPtr, data.Length);
        }

        /// <summary>
        /// 複数のPlayerにパケットを送信
        /// </summary>
        public override unsafe void Multicast(NativeList<ulong> uniqueIdList, DataStreamWriter data, QosType qos)
        {
            //Debug.Log($"Multicast {uniqueIdList.Length} Len={data.Length} qos={qos}");
            if (NetwrokState == NetworkConnection.State.Disconnected)
            {
                Debug.LogError("Send Failed : NetworkConnection.State.Disconnected");
                return;
            }
            if (_SinglePacketBuffer.Length + data.Length + 7 + 2 * uniqueIdList.Length >= _SinglePacketBuffer.Capacity)
            {
                _SinglePacketBuffer.Capacity *= 2;
            }

            byte* dataPtr = DataStreamUnsafeUtility.GetUnsafeReadOnlyPtr(data);
            ushort dataLength = (ushort)data.Length;

            _SinglePacketBuffer.Write((byte)qos);
            _SinglePacketBuffer.Write(NetworkLinkerConstants.MulticastId);

            _SinglePacketBuffer.Write((ushort)uniqueIdList.Length);
            for (int i = 0; i < uniqueIdList.Length; i++)
            {
                if (UniquePlayerIdTable.TryGetValue(uniqueIdList[i], out ushort playerId))
                {
                    _SinglePacketBuffer.Write(playerId);
                }
            }
            _SinglePacketBuffer.Write((ushort)data.Length);
            _SinglePacketBuffer.WriteBytes(dataPtr, dataLength);
        }

        /// <summary>
        /// 全Playerにパケットを送信
        /// </summary>
        public override void Broadcast(DataStreamWriter data, QosType qos, bool noChunk = false)
        {
            //Debug.Log($"Brodcast Len={data.Length} qos={qos}");
            if (NetwrokState == NetworkConnection.State.Disconnected)
            {
                Debug.LogError("Send Failed : NetworkConnection.State.Disconnected");
                return;
            }

            if (noChunk)
            {
                SendSingle(NetworkLinkerConstants.BroadcastId, data, qos);
            }
            else
            {
                if(qos == QosType.Unreliable)
                {
                    _BroadcastUdpChunkedPacketManager.AddChunk(data);
                }
                if (qos == QosType.Reliable)
                {
                    _BroadcastRudpChunkedPacketManager.AddChunk(data);
                }
            }
        }
    }
}