using System.Collections;
using System.Collections.Generic;
using System.Net;
using Unity.Collections;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.LowLevel.Unsafe;
using Unity.Networking.Transport.Utilities;
using UnityEngine;

namespace ICKX.Radome
{
    public class UDPClientNetworkManager<PlayerInfo> : ClientNetworkManager<UdpNetworkDriver, PlayerInfo> where PlayerInfo : DefaultPlayerInfo, new()
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
                NetworkDriver = new UdpNetworkDriver(new INetworkParameter[] {
                    Config,
                    new ReliableUtility.Parameters { WindowSize = 128 },
                    new NetworkPipelineParams {initialCapacity = ushort.MaxValue},
                    //new SimulatorUtility.Parameters {MaxPacketSize = 256, MaxPacketCount = 32, PacketDelayMs = 100},
                });
            }

            _QosPipelines[(int)QosType.Empty] = NetworkDriver.CreatePipeline();
            //_QosPipelines[(int)QosType.Reliable] = NetworkDriver.CreatePipeline();
            //_QosPipelines[(int)QosType.Unreliable] = NetworkDriver.CreatePipeline();
            _QosPipelines[(int)QosType.Reliable] = NetworkDriver.CreatePipeline(typeof(ReliableSequencedPipelineStage), typeof(SimulatorPipelineStage));
            _QosPipelines[(int)QosType.Unreliable] = NetworkDriver.CreatePipeline(typeof(SimulatorPipelineStage));

            var endpoint = NetworkEndPoint.Parse(serverAdress.ToString(), serverPort);
            ServerConnection = NetworkDriver.Connect(endpoint);
            
            while (ServerPlayerId >= _ActiveConnectionInfoList.Count) _ActiveConnectionInfoList.Add(null);
            _ActiveConnectionInfoList[ServerPlayerId] = new SCConnectionInfo(NetworkConnection.State.Connecting);

            base.Start();
        }

        protected override void Reconnect()
        {
            Debug.Log("Reconnect" + NetworkState + " : " + serverAdress + ":" + serverPort);

            if (NetworkState != NetworkConnection.State.Connected)
            {
                var endpoint = NetworkEndPoint.Parse(serverAdress.ToString(), serverPort);
                NetworkState = NetworkConnection.State.Connecting;

                ServerConnection = NetworkDriver.Connect(endpoint);

                while (ServerPlayerId >= _ActiveConnectionInfoList.Count) _ActiveConnectionInfoList.Add(null);
                _ActiveConnectionInfoList[ServerPlayerId] = new SCConnectionInfo(NetworkConnection.State.Connecting);

                Debug.Log("Reconnect");
            }
        }
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
    public abstract class ClientNetworkManager<Driver, PlayerInfo> : SCNetowrkManagerBase<Driver, PlayerInfo>
            where Driver : struct, INetworkDriver where PlayerInfo : DefaultPlayerInfo, new()
    {
        public override bool IsFullMesh => false;

        public bool AutoRecconect { get; set; } = true;
        public NetworkConnection ServerConnection { get; protected set; }
        
        public ClientNetworkManager(PlayerInfo playerInfo) : base(playerInfo)
        {
        }

        public override void Dispose()
        {
            if (_IsDispose) return;

            if (NetworkState != NetworkConnection.State.Disconnected)
            {
                StopComplete();
            }

            JobHandle.Complete();

            base.Dispose();
        }

        protected void Start()
        {
            IsLeader = false;
            NetworkState = NetworkConnection.State.Connecting;
        }

        //再接続
        protected abstract void Reconnect();

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

            //Playerリストから削除するリクエストを送る
            using (var unregisterPlayerPacket = new DataStreamWriter(9, Allocator.Temp))
            {
                unregisterPlayerPacket.Write((byte)BuiltInPacket.Type.UnregisterPlayer);
                unregisterPlayerPacket.Write(MyPlayerInfo.UniqueId);
                Send(ServerPlayerId, unregisterPlayerPacket, QosType.Reliable);
            }
            Debug.Log("Stop");
        }

        // サーバーから切断されたらLinkerを破棄して停止
        protected override void StopComplete()
        {
            if (NetworkState != NetworkConnection.State.Disconnected)
            {
                foreach (var pair in _UniquePlayerIdTable)
                {
                    ExecOnUnregisterPlayer(pair.Value, pair.Key);
                }
            }
            base.StopComplete();
            if (NetworkState == NetworkConnection.State.Disconnected)
            {
                Debug.LogError("Start Failed  currentState = " + NetworkState);
                return;
            }
            JobHandle.Complete();

            if (ServerConnection.GetState(NetworkDriver) != NetworkConnection.State.Disconnected)
            {
                ServerConnection.Disconnect(NetworkDriver);
            }

            NetworkState = NetworkConnection.State.Disconnected;
        }

        public override DefaultConnectionInfo GetConnectionInfo(ushort playerId)
        {
            if (playerId >= _ActiveConnectionInfoList.Count) return null;
            return _ActiveConnectionInfoList[playerId];
        }
        public override DefaultConnectionInfo GetConnectionInfo(ulong uniqueId)
        {
            if (UniquePlayerIdTable.TryGetValue(uniqueId, out ushort playerId))
            {
                return GetConnectionInfo(playerId);
            }
            return null;
        }
        public override DefaultPlayerInfo GetPlayerInfo(ushort playerId)
        {
            if (playerId >= _ActivePlayerInfoList.Count) return null;
            return _ActivePlayerInfoList[playerId];
        }
        public override DefaultPlayerInfo GetPlayerInfo(ulong uniqueId)
        {
            if (UniquePlayerIdTable.TryGetValue(uniqueId, out ushort playerId))
            {
                return GetPlayerInfo(playerId);
            }
            return null;
        }
        public override bool GetPlayerId(ulong uniqueId, out ushort playerId)
        {
            playerId = 0;
            return UniquePlayerIdTable.TryGetValue(uniqueId, out playerId);
        }
        public override bool GetUniqueId(ushort playerId, out ulong uniqueId)
        {
            var playerInfo = (playerId < _ActivePlayerInfoList.Count) ? _ActivePlayerInfoList[playerId] : null;
            uniqueId = (playerInfo == null) ? 0 : playerInfo.UniqueId;
            return uniqueId != 0;
        }

        //新しいPlayerを登録する処理
        protected void RegisterPlayerId(PlayerInfo playerInfo)
        {
            base.RegisterPlayerId(playerInfo.PlayerId, playerInfo.UniqueId);
            //ConnectionInfo更新
            while (playerInfo.PlayerId >= _ActiveConnectionInfoList.Count) _ActiveConnectionInfoList.Add(null);
            if(_ActiveConnectionInfoList[playerInfo.PlayerId] == null)
            {
                _ActiveConnectionInfoList[playerInfo.PlayerId] = new SCConnectionInfo(NetworkConnection.State.Connected);
            }
            else
            {
                _ActiveConnectionInfoList[playerInfo.PlayerId].State = NetworkConnection.State.Connected;
            }

            //playerInfo領域確保
            while (playerInfo.PlayerId >= _ActivePlayerInfoList.Count) _ActivePlayerInfoList.Add(null);
            _ActivePlayerInfoList[playerInfo.PlayerId] = playerInfo;
            _UniquePlayerIdTable[playerInfo.UniqueId] = playerInfo.PlayerId;

            //イベント通知
            ExecOnRegisterPlayer(playerInfo.PlayerId, playerInfo.UniqueId);
        }

        //Playerを登録解除する処理
        protected void UnregisterPlayerId(ulong uniqueId)
        {
            if(!GetPlayerId(uniqueId, out ushort playerId))
            {
                return;
            }

            if (IsActivePlayerId(playerId))
            {
                _UniquePlayerIdTable.Remove(uniqueId);
                _ActiveConnectionInfoList[playerId] = default;
                _ActivePlayerInfoList[playerId] = null;

                base.UnregisterPlayerId(playerId, uniqueId);

                ExecOnUnregisterPlayer(playerId, uniqueId);
            }
        }

        //Playerを再接続させる処理
        protected virtual void ReconnectPlayerId(ulong uniqueId)
        {
            if (!GetPlayerId(uniqueId, out ushort playerId))
            {
                return;
            }
            if (IsActivePlayerId(playerId))
            {
                var info = _ActiveConnectionInfoList[playerId];
                if (info != null)
                {
                    info.State = NetworkConnection.State.Connected;

                    ExecOnReconnectPlayer(playerId, uniqueId);
                }
                else
                {
                    Debug.LogError($"ReconnectPlayerId Error. ID={playerId}は未登録");
                }
            }
            else
            {
                Debug.LogError($"ReconnectPlayerId Error. ID={playerId}は未登録");
            }
        }

        //Playerを一旦切断状態にする処理
        protected virtual void DisconnectPlayerId(ulong uniqueId)
        {
            if (!GetPlayerId(uniqueId, out ushort playerId))
            {
                return;
            }
            if (IsActivePlayerId(playerId))
            {
                var info = _ActiveConnectionInfoList[playerId];
                if (info != null)
                {
                    info.State = NetworkConnection.State.AwaitingResponse;
                    info.DisconnectTime = Time.realtimeSinceStartup;

                    ExecOnDisconnectPlayer(playerId, uniqueId);
                }
                else
                {
                    Debug.LogError($"DisconnectPlayerId Error. ID={playerId}は未登録");
                }
            }
            else
            {
                Debug.LogError($"DisconnectPlayerId Error. ID={playerId}は未登録");
            }
        }

        private void SendRegisterPlayerPacket(ushort targetPlayerId)
        {
            var addPlayerPacket = MyPlayerInfo.CreateUpdatePlayerInfoPacket((byte)BuiltInPacket.Type.RegisterPlayer);
            Send(targetPlayerId, addPlayerPacket, QosType.Reliable);
            addPlayerPacket.Dispose();
        }

        private void SendReconnectPlayerPacket(ushort targetPlayerId)
        {
            var recconectPlayerPacket = MyPlayerInfo.CreateUpdatePlayerInfoPacket((byte)BuiltInPacket.Type.ReconnectPlayer);
            Send(targetPlayerId, recconectPlayerPacket, QosType.Reliable);
            recconectPlayerPacket.Dispose();
        }

        private void SendUpdatePlayerPacket(ushort targetPlayerId)
        {
            var addPlayerPacket = MyPlayerInfo.CreateUpdatePlayerInfoPacket((byte)BuiltInPacket.Type.UpdatePlayerInfo);
            Send(targetPlayerId, addPlayerPacket, QosType.Reliable);
            addPlayerPacket.Dispose();
        }

        /// <summary>
        /// 受信パケットの受け取りなど、最初に行うべきUpdateループ
        /// </summary>
        public override void OnFirstUpdate()
        {
            JobHandle.Complete();

            if (ServerConnection == default)
            {
                return;
            }
            if (NetworkState == NetworkConnection.State.Disconnected)
            {
                return;
            }

            _SinglePacketBuffer.Clear();
            _BroadcastRudpChunkedPacketManager.Clear();
            _BroadcastUdpChunkedPacketManager.Clear();

            NetworkConnection con;
            DataStreamReader stream;
            NetworkEvent.Type cmd;

            while ((cmd = NetworkDriver.PopEvent(out con, out stream)) != NetworkEvent.Type.Empty)
            {
                if (cmd == NetworkEvent.Type.Connect)
                {
                    Debug.Log("IsConnected" + con.InternalId);

                    //Serverと接続完了
                    ServerConnection = con;

                    if (NetworkState == NetworkConnection.State.AwaitingResponse)
                    {
                        //再接続
                        //サーバーに自分のPlayerInfoを教える
                        SendReconnectPlayerPacket(ServerPlayerId);
                    }
                    else
                    {
                        //サーバーに自分のPlayerInfoを教える
                        SendRegisterPlayerPacket(ServerPlayerId);
                    }
                }
                else if (cmd == NetworkEvent.Type.Disconnect)
                {
                    Debug.Log ("IsDisconnected" + con.InternalId);
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
                            NetworkState = NetworkConnection.State.Disconnected;
                        }
                        else
                        {
                            DisconnectPlayerId(MyPlayerInfo.UniqueId);
                            ExecOnDisconnectAll(1);    //TODO ErrorCodeを取得する方法を探す
                            NetworkState = NetworkConnection.State.AwaitingResponse;
                        }
                        Reconnect();
                    }
                    return;
                }
                else if (cmd == NetworkEvent.Type.Data)
                {
                    if (!stream.IsCreated)
                    {
                        continue;
                    }
                    //Debug.Log($"driver.PopEvent={cmd} con={con.InternalId} : {stream.Length}");

                    var ctx = new DataStreamReader.Context();
                    byte qos = stream.ReadByte(ref ctx);
                    ushort targetPlayerId = stream.ReadUShort(ref ctx);
                    ushort senderPlayerId = stream.ReadUShort(ref ctx);

                    while (true)
                    {
                        int pos = stream.GetBytesRead(ref ctx);
                        if (pos >= stream.Length) break;
                        ushort dataLen = stream.ReadUShort(ref ctx);
                        if (dataLen == 0 || pos + dataLen >= stream.Length) break;

                        var chunk = stream.ReadChunk(ref ctx, dataLen);

                        var ctx2 = new DataStreamReader.Context();
                        byte type = chunk.ReadByte(ref ctx2);

                        //if(type != (byte)BuiltInPacket.Type.MeasureRtt)
                        //{
                        //    var c = new DataStreamReader.Context();
                        //    Debug.Log($"Dump : {string.Join(",", chunk.ReadBytesAsArray(ref c, chunk.Length))}");
                        //}

                        DeserializePacket(senderPlayerId, type, ref chunk, ctx2);
                    }
                }
            }
            _IsFirstUpdateComplete = true;
        }

        protected virtual void DeserializePacket(ushort senderPlayerId, byte type, ref DataStreamReader chunk, DataStreamReader.Context ctx2)
        {
            var playerInfo = (senderPlayerId < _ActivePlayerInfoList.Count) ? _ActivePlayerInfoList[senderPlayerId] : null;
            ulong senderUniqueId = (playerInfo == null) ? 0 : playerInfo.UniqueId;

            //Debug.Log($"DeserializePacket : {senderPlayerId} : {(BuiltInPacket.Type)type} {chunk.Length}");
            //自分宛パケットの解析
            switch (type)
            {
                case (byte)BuiltInPacket.Type.RegisterPlayer:
                    NetworkState = NetworkConnection.State.Connected;
                    MyPlayerInfo.PlayerId = chunk.ReadUShort(ref ctx2);

                    LeaderStatTime = chunk.ReadLong(ref ctx2);
                    bool isRecconnect = chunk.ReadByte(ref ctx2) != 0;

                    Debug.Log($"Register ID={MyPlayerId} Time={LeaderStatTime} IsRec={isRecconnect}");

                    byte count = chunk.ReadByte(ref ctx2);
                    _ActivePlayerIdList.Clear();
                    for (int i = 0; i < count; i++)
                    {
                        _ActivePlayerIdList.Add(chunk.ReadByte(ref ctx2));
                    }

                    while (GetLastPlayerId() >= _ActiveConnectionInfoList.Count) _ActiveConnectionInfoList.Add(null);
                    for (ushort i = 0; i < _ActivePlayerIdList.Count * 8; i++)
                    {
                        if (IsActivePlayerId(i))
                        {
                            _ActiveConnectionInfoList[i] = new SCConnectionInfo(NetworkConnection.State.Connecting);
                        }
                    }

                    var serverPlayerInfo = new PlayerInfo();
                    serverPlayerInfo.Deserialize(ref chunk, ref ctx2);

                    //サーバーを登録
                    RegisterPlayerId(serverPlayerInfo);
                    //自分を登録
                    RegisterPlayerId(MyPlayerInfo as PlayerInfo);

                    ExecOnConnect();

                    //recconect
                    if (isRecconnect)
                    {
                        ReconnectPlayerId(MyPlayerInfo.UniqueId);
                    }
                    break;
                case (byte)BuiltInPacket.Type.StopNetwork:
                    Stop();
                    break;
                case (byte)BuiltInPacket.Type.UpdatePlayerInfo:
                    {
                        while (ctx2.GetReadByteIndex() + 2 < chunk.Length)
                        {
                            var state = (NetworkConnection.State)chunk.ReadByte(ref ctx2);
                            var updatePlayerInfo = new PlayerInfo();
                            updatePlayerInfo.Deserialize(ref chunk, ref ctx2);
                            Debug.Log("UpdatePlayerInfo : " + state + " : " + updatePlayerInfo.PlayerId);
                            if (updatePlayerInfo.PlayerId != MyPlayerId)
                            {
                                var connInfo = GetConnectionInfo(updatePlayerInfo.PlayerId);
                                switch (state)
                                {
                                    case NetworkConnection.State.Connected:
                                        if (connInfo != null && connInfo.State == NetworkConnection.State.AwaitingResponse)
                                        {
                                            Debug.Log("ReconnectPlayerId : " + state + " : " + updatePlayerInfo.PlayerId);
                                            ReconnectPlayerId(updatePlayerInfo.UniqueId);
                                        }
                                        else if (!(connInfo != null && connInfo.State == NetworkConnection.State.Connected))
                                        {
                                            Debug.Log("RegisterPlayerId : " + state + " : " + updatePlayerInfo.PlayerId);
                                            RegisterPlayerId(updatePlayerInfo);
                                        }
                                        break;
                                    case NetworkConnection.State.Disconnected:
                                        if (connInfo != null && connInfo.State != NetworkConnection.State.Disconnected)
                                        {
                                            Debug.Log("UnregisterPlayerId : " + state + " : " + updatePlayerInfo.PlayerId);
                                            if (GetUniqueId(updatePlayerInfo.PlayerId, out ulong uniqueId))
                                            {
                                                UnregisterPlayerId(uniqueId);
                                            }
                                        }
                                        break;
                                    case NetworkConnection.State.AwaitingResponse:
                                        if (connInfo != null && connInfo.State != NetworkConnection.State.AwaitingResponse)
                                        {
                                            Debug.Log("DisconnectPlayerId : " + state + " : " + updatePlayerInfo.PlayerId);
                                            DisconnectPlayerId(updatePlayerInfo.UniqueId);
                                        }
                                        break;
                                }
                            }
                        }
                    }
                    break;
                default:
                    RecieveData(senderPlayerId, senderUniqueId, type, chunk, ctx2);
                    break;
            }
        }

        protected override void RecieveData(ushort senderPlayerId, ulong senderUniqueId, byte type, DataStreamReader chunk, DataStreamReader.Context ctx)
        {
            base.RecieveData(senderPlayerId, senderUniqueId, type, chunk, ctx);
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
            if (ServerConnection == default)
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
            using (var packet = new DataStreamWriter(9, Allocator.Temp))
            {
                packet.Write((byte)BuiltInPacket.Type.MeasureRtt);
                packet.Write(GamePacketManager.currentUnixTime);

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
                singlePacketBuffer = _SinglePacketBuffer,
                rudpPacketBuffer = _BroadcastRudpChunkedPacketManager.ChunkedPacketBuffer,
                udpPacketBuffer = _BroadcastUdpChunkedPacketManager.ChunkedPacketBuffer,
                qosPipelines = _QosPipelines,
                senderPlayerId = MyPlayerId,
            };

            return sendPacketsJob.Schedule(jobHandle);
        }

        struct SendPacketaJob : IJob
        {
            public Driver driver;
            [ReadOnly]
            public NetworkConnection serverConnection;

            [ReadOnly]
            public DataStreamWriter singlePacketBuffer;
            [ReadOnly]
            public DataStreamWriter rudpPacketBuffer;
            [ReadOnly]
            public DataStreamWriter udpPacketBuffer;

            public NativeArray<NetworkPipeline> qosPipelines;
            [ReadOnly]
            public ushort senderPlayerId;

            public unsafe void Execute()
            {
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
                        serverConnection.Send(driver, qosPipelines[qos], temp);
                        //serverConnection.Send(driver, temp);
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
                        //Debug.Log("chunkedPacketBuffer : " + packetDataLen);
                        serverConnection.Send(driver, qosPipelines[(byte)QosType.Unreliable], temp);
                        //serverConnection.Send(driver, temp);
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
                        //Debug.Log("chunkedPacketBuffer : " + packetDataLen);
                        serverConnection.Send(driver, qosPipelines[(byte)QosType.Reliable], temp);
                        //serverConnection.Send(driver, temp);
                    }
                }
                temp.Dispose();
                multiCastList.Dispose();
            }
        }
    }
}
