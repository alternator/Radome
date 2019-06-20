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
            if (NetworkState == NetworkConnection.State.Disconnected)
            {
                Debug.LogError("Start Failed  currentState = " + NetworkState);
                return;
            }
            JobHandle.Complete();

            NetworkState = NetworkConnection.State.AwaitingResponse;
            IsStopRequest = true;
            
            Debug.Log("Stop");
        }

        // サーバーから切断されたらLinkerを破棄して停止
        protected override void StopComplete()
        {
            base.StopComplete();
            if (NetworkState == NetworkConnection.State.Disconnected)
            {
                Debug.LogError("Start Failed  currentState = " + NetworkState);
                return;
            }
            JobHandle.Complete();

            MyPlayerInfo.PlayerId = 0;
            IsStopRequest = false;
            _IsFirstUpdateComplete = false;

            _ActiveConnectionInfoList.Clear();
            _ActivePlayerInfoList.Clear();
        }
    }
}