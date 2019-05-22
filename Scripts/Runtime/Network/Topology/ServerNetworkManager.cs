using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using Unity.Collections;
using Unity.Jobs;
using Unity.Networking.Transport;
using Unity.Networking.Transport.LowLevel.Unsafe;
using Unity.Networking.Transport.Utilities;
using UnityEngine;
using UnityEngine.Profiling;

namespace ICKX.Radome
{

    public class UDPServerNetworkManager<PlayerInfo> : ServerNetworkManager<UdpNetworkDriver, PlayerInfo> where PlayerInfo : DefaultPlayerInfo, new()
    {
        private NetworkConfigParameter Config;

        public UDPServerNetworkManager(PlayerInfo playerInfo) : base (playerInfo)
        {
            Config = new NetworkConfigParameter()
            {
                connectTimeoutMS = 1000 * 5,
                disconnectTimeoutMS = 1000 * 5,
            };
        }

        public UDPServerNetworkManager(PlayerInfo playerInfo, NetworkConfigParameter config) : base(playerInfo)
        {
            Config = config;
        }

        /// <summary>
        /// サーバー起動
        /// </summary>
        public void Start(int port)
        {
            if (NetwrokState != NetworkConnection.State.Disconnected)
            {
                Debug.LogError("Start Failed  currentState = " + NetwrokState);
                return;
            }

            if (!NetworkDriver.IsCreated)
            {
                NetworkDriver = new UdpNetworkDriver(new INetworkParameter[] {
                    Config,
                    new ReliableUtility.Parameters { WindowSize = 32 },
                    //new SimulatorUtility.Parameters {MaxPacketSize = 256, MaxPacketCount = 32, PacketDelayMs = 100},
                });
            }

            _QosPipelines[(int)QosType.Empty] = NetworkDriver.CreatePipeline();
            //_QosPipelines[(int)QosType.Reliable] = NetworkDriver.CreatePipeline();
            //_QosPipelines[(int)QosType.Unreliable] = NetworkDriver.CreatePipeline();
            _QosPipelines[(int)QosType.Reliable] = NetworkDriver.CreatePipeline(typeof(ReliableSequencedPipelineStage), typeof(SimulatorPipelineStage));
            _QosPipelines[(int)QosType.Unreliable] = NetworkDriver.CreatePipeline(typeof(SimulatorPipelineStage));

            var endPoint = NetworkEndPoint.AnyIpv4;
            endPoint.Port = (ushort)port;
            if (NetworkDriver.Bind(endPoint) != 0)
            {
                Debug.Log("Failed to bind to port");
            }
            else
            {
                NetworkDriver.Listen();
                Debug.Log("Listen");
            }

            Start();
        }
    }

    /// <summary>
    /// サーバー用のNetworkManager
    /// 通信の手順は
    /// 
    /// Server.Send  -> Chunk化 -> Pipline -> con.Send -> Pop -> Pipline -> Chunk解除 -> Recieve
    /// </summary>
    /// <typeparam name="Driver"></typeparam>
    /// <typeparam name="PlayerInfo"></typeparam>
	public abstract class ServerNetworkManager<Driver, PlayerInfo> : SCNetowrkManagerBase<Driver, PlayerInfo>
            where Driver : struct, INetworkDriver where PlayerInfo : DefaultPlayerInfo, new()
    {

        public float registrationTimeOut { get; set; } = 60.0f;

        public override bool IsFullMesh => false;
        protected Dictionary<int, ushort> _ConnIdPlayerIdTable = new Dictionary<int, ushort>();
        public IReadOnlyDictionary<int, ushort> ConnIdPlayerIdTable => _ConnIdPlayerIdTable;
        
        protected NativeList<NetworkConnection> _Connections;

        protected NativeList<int> _ConnectConnIdList;
        protected NativeList<int> _DisconnectConnIdList;
        protected DataStreamWriter _RelayWriter;
        //protected NativeMultiHashMap<int, DataStreamReader> _RecieveDataStream;
        protected NativeList<DataPacket> _RecieveDataStream;

        public struct DataPacket
        {
            public NetworkConnection Connection;
            public DataStreamReader Chunk;
        } 

        public ServerNetworkManager(PlayerInfo playerInfo) : base(playerInfo)
        {
            _Connections = new NativeList<NetworkConnection>(16, Allocator.Persistent);
            _ConnectConnIdList = new NativeList<int>(4, Allocator.Persistent);
            _DisconnectConnIdList = new NativeList<int>(4, Allocator.Persistent);
            _RelayWriter = new DataStreamWriter(NetworkParameterConstants.MTU, Allocator.Persistent);
            //_RecieveDataStream = new NativeMultiHashMap<int, DataStreamReader>(32, Allocator.Persistent);
            _RecieveDataStream = new NativeList<DataPacket>(32, Allocator.Persistent);
        }

        public override void Dispose()
        {
            if (_IsDispose) return;

            if (NetwrokState != NetworkConnection.State.Disconnected)
            {
                StopComplete();
            }

            JobHandle.Complete();

            _Connections.Dispose();
            _ConnectConnIdList.Dispose();
            _DisconnectConnIdList.Dispose();
            _RelayWriter.Dispose();
            _RecieveDataStream.Dispose();

            base.Dispose();
        }

        protected void Start()
        {
            LeaderStatTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            NetwrokState = NetworkConnection.State.Connecting;

            _ActiveConnectionInfoList.Add(new SCConnectionInfo(NetworkConnection.State.Connecting));
            MyPlayerInfo.PlayerId = ServerPlayerId;
            RegisterPlayerId(MyPlayerInfo as PlayerInfo, default);
        }

        /// <summary>
        /// サーバー停止
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
            if (NetwrokState != NetworkConnection.State.Disconnected)
            {
                foreach (var pair in _UniquePlayerIdTable)
                {
                    ExecOnUnregisterPlayer(pair.Value, pair.Key);
                }
            }
            base.StopComplete();
            if (NetwrokState == NetworkConnection.State.Disconnected)
            {
                Debug.LogError("CompleteStop Failed  currentState = " + NetwrokState);
                return;
            }
            JobHandle.Complete();

            _ConnIdPlayerIdTable.Clear();
            _Connections.Clear();
            _ConnectConnIdList.Clear();
            _DisconnectConnIdList.Clear();
            _RelayWriter.Clear();
            _RecieveDataStream.Clear();

            NetwrokState = NetworkConnection.State.Disconnected;
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
        protected void RegisterPlayerId(PlayerInfo playerInfo, NetworkConnection connection)
        {
            base.RegisterPlayerId(playerInfo.PlayerId, playerInfo.UniqueId);

            //ConnectionInfo更新
            while (_Connections.Length <= playerInfo.PlayerId) _Connections.Add(default);
            _Connections[playerInfo.PlayerId] = connection;
            _ConnIdPlayerIdTable[connection.InternalId] = playerInfo.PlayerId;

            //ConnectionInfo更新
            while (playerInfo.PlayerId >= _ActiveConnectionInfoList.Count) _ActiveConnectionInfoList.Add(null);
            _ActiveConnectionInfoList[playerInfo.PlayerId] = new SCConnectionInfo(NetworkConnection.State.Connected);

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
            if (!GetPlayerId(uniqueId, out ushort playerId) && playerId >= _Connections.Length)
            {
                return;
            }
            var connect = _Connections[playerId];

            if (IsActivePlayerId(playerId) && connect != default)
            {
                _ConnIdPlayerIdTable.Remove(connect.InternalId);
                _UniquePlayerIdTable.Remove(uniqueId);
                _ActiveConnectionInfoList[playerId] = default;
                _ActivePlayerInfoList[playerId] = null;

                base.UnregisterPlayerId(playerId, uniqueId);

                ExecOnUnregisterPlayer(playerId, uniqueId);
            }
        }

        protected virtual void ReconnectPlayerId(PlayerInfo playerInfo, NetworkConnection connection)
        {
            //ConnectionInfo更新
            while (_Connections.Length <= playerInfo.PlayerId) _Connections.Add(default);
            _Connections[playerInfo.PlayerId] = connection;
            _ConnIdPlayerIdTable[connection.InternalId] = playerInfo.PlayerId;

            //ConnectionInfo更新
            _ActiveConnectionInfoList[playerInfo.PlayerId].State = NetworkConnection.State.Connected;

            //playerInfo領域確保
            while (playerInfo.PlayerId >= _ActivePlayerInfoList.Count) _ActivePlayerInfoList.Add(null);
            _ActivePlayerInfoList[playerInfo.PlayerId] = playerInfo;
            _UniquePlayerIdTable[playerInfo.UniqueId] = playerInfo.PlayerId;

            //イベント通知
            ExecOnReconnectPlayer(playerInfo.PlayerId, playerInfo.UniqueId);
        }

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

        protected void SendUpdateAllPlayerPacket(ushort targetPlayerId)
        {
            var packet = new DataStreamWriter(NetworkParameterConstants.MTU, Allocator.Temp);

            packet.Write((byte)BuiltInPacket.Type.UpdatePlayerInfo);
            for (ushort i = 1; i < _ActivePlayerInfoList.Count; i++)
            {
                var connInfo = _ActiveConnectionInfoList[i];
                var playerInfo = _ActivePlayerInfoList[i];
                if (connInfo != null && playerInfo != null && connInfo.State != NetworkConnection.State.Connecting)
                {
                    packet.Write((byte)connInfo.State);
                    playerInfo.AppendPlayerInfoPacket(ref packet);
                }
                else
                {
                    playerInfo = new PlayerInfo() { PlayerId = i };
                    packet.Write((byte)NetworkConnection.State.Disconnected);
                    playerInfo.AppendPlayerInfoPacket(ref packet);
                }

                if (packet.Length > NetworkParameterConstants.MTU - playerInfo.PacketSize - 1)
                {
                    Send(targetPlayerId, packet, QosType.Reliable);
                    packet.Clear();
                    packet.Write((byte)BuiltInPacket.Type.UpdatePlayerInfo);
                }
            }
            if (packet.Length > 1)
            {
                Send(targetPlayerId, packet, QosType.Reliable);
            }
            packet.Dispose();
        }

        protected void BroadcastUpdatePlayerPacket(ushort playerId)
        {
            var connInfo = _ActiveConnectionInfoList[playerId];
            var playerInfo = _ActivePlayerInfoList[playerId];
            if (connInfo != null && playerInfo != null && connInfo.State != NetworkConnection.State.Connecting)
            {
                var packet = new DataStreamWriter(2 + playerInfo.PacketSize, Allocator.Temp);
                packet.Write((byte)BuiltInPacket.Type.UpdatePlayerInfo);
                packet.Write((byte)connInfo.State);
                playerInfo.AppendPlayerInfoPacket(ref packet);
                Broadcast(packet, QosType.Reliable);
                packet.Dispose();
            }
            else
            {
                playerInfo = new PlayerInfo {  PlayerId = playerId };
                var packet = new DataStreamWriter(2 + playerInfo.PacketSize, Allocator.Temp);
                packet.Write((byte)BuiltInPacket.Type.UpdatePlayerInfo);
                packet.Write((byte)NetworkConnection.State.Disconnected);
                playerInfo.AppendPlayerInfoPacket(ref packet);
                Broadcast(packet, QosType.Reliable);
                packet.Dispose();
            }
        }

        private void SendRegisterPlayerPacket(ushort id, bool isReconnect)
        {
            var registerPacket = new DataStreamWriter(14 + _ActivePlayerIdList.Count + MyPlayerInfo.PacketSize, Allocator.Temp);
            registerPacket.Write((byte)BuiltInPacket.Type.RegisterPlayer);
            registerPacket.Write(id);
            registerPacket.Write(LeaderStatTime);
            registerPacket.Write((byte)(isReconnect ? 1 : 0));

            registerPacket.Write((byte)_ActivePlayerIdList.Count);
            for (int i = 0; i < _ActivePlayerIdList.Count; i++)
            {
                registerPacket.Write(_ActivePlayerIdList[i]);
            }
            MyPlayerInfo.AppendPlayerInfoPacket(ref registerPacket);

            Debug.Log($"Send Reg {id} : {LeaderStatTime} : {isReconnect} : count={_ActivePlayerIdList.Count} : {registerPacket.Length}");
            
            Send(id, registerPacket, QosType.Reliable);

            registerPacket.Dispose();
        }

        private void BroadcastStopNetworkPacket()
        {
            using (var stopNetworkPacket = new DataStreamWriter(2, Allocator.Temp))
            {
                stopNetworkPacket.Write((byte)BuiltInPacket.Type.StopNetwork);
                stopNetworkPacket.Write((byte)0);    //TODO error code

                Broadcast(stopNetworkPacket, QosType.Reliable, true);
            }
        }
        
        /// <summary>
        /// 受信パケットの受け取りなど、最初に行うべきUpdateループ
        /// </summary>
        public override void OnFirstUpdate()
        {
            if (NetwrokState == NetworkConnection.State.Disconnected)
            {
                return;
            }

            //job完了待ち
            JobHandle.Complete();

            _SinglePacketBuffer.Clear();
            _BroadcastRudpChunkedPacketManager.Clear();
            _BroadcastUdpChunkedPacketManager.Clear();

            //接続確認
            NetworkConnection connection;
            while ((connection = NetworkDriver.Accept()) != default)
            {
                Debug.Log("Accepted a connection =" + connection.InternalId);
            }

            //一定時間切断したままのplayerの登録を解除
            for (ushort i = 0; i < _ActiveConnectionInfoList.Count; i++)
            {
                var conInfo = GetConnectionInfo(i);
                var playerInfo = GetPlayerInfo(i);
                if (conInfo != null && playerInfo != null)
                {
                    if (conInfo.State == NetworkConnection.State.Connecting)
                    {
                        if (Time.realtimeSinceStartup - conInfo.DisconnectTime > registrationTimeOut)
                        {
                            UnregisterPlayerId(i, playerInfo.UniqueId);
                        }
                    }
                }
            }

            for (ushort i=0;i< _DisconnectConnIdList.Length;i++)
            {
                if(_ConnIdPlayerIdTable.TryGetValue(_DisconnectConnIdList[i], out ushort playerId))
                {
                    if (GetUniqueId(playerId, out ulong uniqueId))
                    {
                        var connInfo = GetConnectionInfo(uniqueId);
                        if (connInfo != null && connInfo.State != NetworkConnection.State.AwaitingResponse)
                        {
                            Debug.Log($"_DisconnectConnIdList[{i}] : {_DisconnectConnIdList[i]} PlayerID={playerId}");
                            DisconnectPlayerId(uniqueId);
                            BroadcastUpdatePlayerPacket(playerId);
                        }
                    }
                }
            }

            for (int i = 0; i < _RecieveDataStream.Length; i++)
            {
                connection = _RecieveDataStream[i].Connection;
                var chunk = _RecieveDataStream[i].Chunk;
                _ConnIdPlayerIdTable.TryGetValue(connection.InternalId, out ushort senderPlayerId);

                var ctx = default(DataStreamReader.Context);
                byte type = chunk.ReadByte(ref ctx);

                DeserializePacket(senderPlayerId, type, connection, ref chunk, ref ctx);
            }

            _IsFirstUpdateComplete = true;
        }

        protected virtual bool DeserializePacket(ushort senderPlayerId, byte type, NetworkConnection con, ref DataStreamReader chunk, ref DataStreamReader.Context ctx2)
        {
            //Debug.Log($"DeserializePacket : {senderPlayerId} : {(BuiltInPacket.Type)type} {chunk.Length}");
            switch (type)
            {
                case (byte)BuiltInPacket.Type.RegisterPlayer:
                    {
                        var addPlayerInfo = new PlayerInfo();
                        addPlayerInfo.Deserialize(ref chunk, ref ctx2);

                        var connInfo = GetConnectionInfo(addPlayerInfo.UniqueId);
                        if (connInfo == null || connInfo.State != NetworkConnection.State.Connected)
                        {
                            bool isReconnect = _UniquePlayerIdTable.TryGetValue(addPlayerInfo.UniqueId, out ushort newPlayerId);

                            if (!isReconnect)
                            {
                                newPlayerId = GetDeactivePlayerId();
                            }

                            addPlayerInfo.PlayerId = newPlayerId;

                            Debug.Log($"Register newID={newPlayerId} UniqueId={addPlayerInfo.UniqueId} IsRec={isReconnect}");
                            
                            if (isReconnect)
                            {
                                ReconnectPlayerId(addPlayerInfo, con);
                                SendRegisterPlayerPacket(newPlayerId, isReconnect);
                            }
                            else
                            {
                                RegisterPlayerId(addPlayerInfo, con);
                                SendRegisterPlayerPacket(newPlayerId, isReconnect);
                            }
                            BroadcastUpdatePlayerPacket(addPlayerInfo.PlayerId);

                            SendUpdateAllPlayerPacket(newPlayerId);

                            NetwrokState = NetworkConnection.State.Connected;
                        }
                    }
                    break;
                case (byte)BuiltInPacket.Type.ReconnectPlayer:
                    {
                        var reconnectPlayerInfo = new PlayerInfo();
                        reconnectPlayerInfo.Deserialize(ref chunk, ref ctx2);
                        if(GetPlayerId(reconnectPlayerInfo.UniqueId, out ushort playerId) && playerId == reconnectPlayerInfo.PlayerId)
                        {
                            var connInfo = GetConnectionInfo(reconnectPlayerInfo.UniqueId);
                            if (connInfo == null || connInfo.State != NetworkConnection.State.Connected)
                            {
                                ReconnectPlayerId(reconnectPlayerInfo, con);
                                BroadcastUpdatePlayerPacket(reconnectPlayerInfo.PlayerId);
                                SendUpdateAllPlayerPacket(playerId);
                            }
                        }
                    }
                    break;
                case (byte)BuiltInPacket.Type.UnregisterPlayer:
                    //登録解除リクエスト
                    {
                        ulong unregisterUniqueId = chunk.ReadULong(ref ctx2);
                        if(GetPlayerId(unregisterUniqueId, out ushort playerId))
                        {
                            var connInfo = GetConnectionInfo(unregisterUniqueId);
                            if (connInfo != null && connInfo.State != NetworkConnection.State.Disconnected)
                            {
                                UnregisterPlayerId(unregisterUniqueId);
                                BroadcastUpdatePlayerPacket(playerId);
                                NetworkDriver.Disconnect(con);

                                if (IsStopRequest && GetPlayerCount() == 1)
                                {
                                    StopComplete();
                                }
                            }
                        }
                    }
                    return true;
                case (byte)BuiltInPacket.Type.UpdatePlayerInfo:
                    {
                        //Serverは追えているのでPlayerInfoの更新のみ
                        var state = (NetworkConnection.State)chunk.ReadByte(ref ctx2);
                        var ctx3 = ctx2;
                        var playerId = chunk.ReadUShort(ref ctx3);

                        _ActivePlayerInfoList[playerId].Deserialize(ref chunk, ref ctx2);
                        BroadcastUpdatePlayerPacket(playerId);
                    }
                    break;
                default:
                    {
                        //自分宛パケットの解析
                        var playerInfo = (senderPlayerId < _ActivePlayerInfoList.Count) ? _ActivePlayerInfoList[senderPlayerId] : null;
                        ulong senderUniqueId = (playerInfo == null) ? 0 : playerInfo.UniqueId;
                        RecieveData(senderPlayerId, senderUniqueId, type, chunk, ctx2);
                    }
                    break;
            }
            return false;
        }

        private float _PrevSendTime;

        /// <summary>
        /// まとめたパケット送信など、最後に行うべきUpdateループ
        /// </summary>
        public override void OnLastUpdate()
        {
            if (NetwrokState == NetworkConnection.State.Disconnected)
            {
                return;
            }

            if (!_IsFirstUpdateComplete) return;

            //main thread処理
            if (NetwrokState == NetworkConnection.State.Connected || NetwrokState == NetworkConnection.State.AwaitingResponse)
            {
                if(Time.realtimeSinceStartup - _PrevSendTime > 1.0)
                {
                    _PrevSendTime = Time.realtimeSinceStartup;
                    SendMeasureLatencyPacket();
                }
            }
            _BroadcastRudpChunkedPacketManager.WriteCurrentBuffer();
            _BroadcastUdpChunkedPacketManager.WriteCurrentBuffer();
            _RecieveDataStream.Clear();

            JobHandle = ScheduleSendPacket(default);
            JobHandle = NetworkDriver.ScheduleUpdate(JobHandle);
            JobHandle = ScheduleRecieve(JobHandle);

            JobHandle.ScheduleBatchedJobs();
        }

        protected void SendMeasureLatencyPacket()
        {
            //Debug.Log("SendMeasureLatencyPacket");
            using (var packet = new DataStreamWriter(9, Allocator.Temp))
            {
                packet.Write((byte)BuiltInPacket.Type.MeasureRtt);
                packet.Write(GamePacketManager.currentUnixTime);

                Broadcast(packet, QosType.Unreliable, true);
            }
        }

        protected JobHandle ScheduleSendPacket(JobHandle jobHandle)
        {
            var sendPacketsJob = new SendPacketaJob()
            {
                driver = NetworkDriver,
                connections = _Connections,
                singlePacketBuffer = _SinglePacketBuffer,
                rudpPacketBuffer = _BroadcastRudpChunkedPacketManager.ChunkedPacketBuffer,
                udpPacketBuffer = _BroadcastUdpChunkedPacketManager.ChunkedPacketBuffer,
                qosPipelines = _QosPipelines,
            };

            return sendPacketsJob.Schedule(jobHandle);
        }

        protected JobHandle ScheduleRecieve(JobHandle jobHandle)
        {
            var recievePacketJob = new RecievePacketJob()
            {
                driver = NetworkDriver,
                connections = _Connections,
                qosPipelines = _QosPipelines,
                connectConnIdList = _ConnectConnIdList,
                disconnectConnIdList = _DisconnectConnIdList,
                relayWriter = _RelayWriter,
                dataStream = _RecieveDataStream,
            };
            return recievePacketJob.Schedule(jobHandle);
        }

        struct SendPacketaJob : IJob
        {
            public Driver driver;
            [ReadOnly]
            public NativeArray<NetworkConnection> connections;

            [ReadOnly]
            public DataStreamWriter singlePacketBuffer;
            [ReadOnly]
            public DataStreamWriter rudpPacketBuffer;
            [ReadOnly]
            public DataStreamWriter udpPacketBuffer;

            public NativeArray<NetworkPipeline> qosPipelines;

            public unsafe void Execute()
            {
                var multiCastList = new NativeList<ushort>(Allocator.Temp);
                var temp = new DataStreamWriter(NetworkParameterConstants.MTU, Allocator.Temp);

                if(singlePacketBuffer.Length != 0)
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
                        temp.Write(ServerPlayerId);
                        temp.Write(packetDataLen);
                        temp.WriteBytes(packetPtr, packetDataLen);

                        if (targetPlayerId == NetworkLinkerConstants.BroadcastId)
                        {
                            for (ushort i = 1; i < connections.Length; i++)
                            {
                                if (connections[i] != default)
                                {
                                    connections[i].Send(driver, qosPipelines[qos], temp);
                                    //connections[i].Send(driver, temp);
                                }
                            }
                        }
                        else if (targetPlayerId == NetworkLinkerConstants.MulticastId)
                        {
                            for (ushort i = 0; i < multiCastList.Length; i++)
                            {
                                if (connections[multiCastList[i]] != default)
                                {
                                    connections[multiCastList[i]].Send(driver, qosPipelines[qos], temp);
                                    //connections[multiCastList[i]].Send(driver, temp);
                                }
                            }
                        }
                        else
                        {
                            connections[targetPlayerId].Send(driver, qosPipelines[(byte)QosType.Unreliable], temp);
                            //connections[targetPlayerId].Send(driver, temp);
                        }
                        //Debug.Log("singlePacketBuffer : " + packetDataLen);
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
                        temp.Write(ServerPlayerId);
                        //temp.Write(packetDataLen);
                        temp.WriteBytes(packetPtr, packetDataLen);

                        for (ushort i = 1; i < connections.Length; i++)
                        {
                            if (connections[i] != default)
                            {
                                connections[i].Send(driver, qosPipelines[(byte)QosType.Unreliable], temp);
                                //connections[i].Send(driver, temp);
                            }
                        }
                        //Debug.Log("chunkedPacketBuffer : " + packetDataLen);
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
                        temp.Write(ServerPlayerId);
                        //temp.Write(packetDataLen);
                        temp.WriteBytes(packetPtr, packetDataLen);

                        for (ushort i = 1; i < connections.Length; i++)
                        {
                            if (connections[i] != default)
                            {
                                connections[i].Send(driver, qosPipelines[(byte)QosType.Reliable], temp);
                                //connections[i].Send(driver, temp);
                            }
                        }
                        //Debug.Log("chunkedPacketBuffer : " + packetDataLen);
                    }
                }
                temp.Dispose();
            }
        }

        struct RecievePacketJob : IJob
        {
            public Driver driver;
            [ReadOnly]
            public NativeArray<NetworkConnection> connections;
            public NativeArray<NetworkPipeline> qosPipelines;

            public NativeList<int> connectConnIdList;
            public NativeList<int> disconnectConnIdList;
            public DataStreamWriter relayWriter;
            //public NativeMultiHashMap<int, DataStreamReader> dataStream;
            public NativeList<DataPacket> dataStream;

            public unsafe void Execute()
            {
                var multiCastList = new NativeList<ushort>(Allocator.Temp);
                connectConnIdList.Clear();
                disconnectConnIdList.Clear();

                NetworkConnection con;
                DataStreamReader stream;
                NetworkEvent.Type cmd;

                while ((cmd = driver.PopEvent(out con, out stream)) != NetworkEvent.Type.Empty)
                {
                    if (cmd == NetworkEvent.Type.Connect)
                    {
                        //Debug.Log($"NetworkEvent.Type.Connect con={con.InternalId}");
                        connectConnIdList.Add(con.InternalId);
                    }
                    else if (cmd == NetworkEvent.Type.Disconnect)
                    {
                        //Debug.Log($"NetworkEvent.Type.Disconnect con={con.InternalId}");
                        disconnectConnIdList.Add(con.InternalId);
                    }
                    else if (cmd == NetworkEvent.Type.Data)
                    {
                        if (!stream.IsCreated)
                        {
                            continue;
                        }
                        //Debug.Log($"driver.PopEvent={cmd} con={con.InternalId} : {stream.Length}");

                        //var c = new DataStreamReader.Context();
                        //Debug.Log($"Dump : {string.Join(",", stream.ReadBytesAsArray(ref c, stream.Length))}");

                        var ctx = new DataStreamReader.Context();
                        byte qos = stream.ReadByte(ref ctx);
                        ushort targetPlayerId = stream.ReadUShort(ref ctx);
                        ushort senderPlayerId = stream.ReadUShort(ref ctx);

                        if (targetPlayerId == NetworkLinkerConstants.MulticastId)
                        {
                            multiCastList.Clear();
                            ushort multiCastCount = stream.ReadUShort(ref ctx);
                            for (int i = 0; i < multiCastCount; i++)
                            {
                                multiCastList.Add(stream.ReadUShort(ref ctx));
                            }
                        }

                        if (targetPlayerId == NetworkLinkerConstants.BroadcastId)
                        {
                            for (ushort i = 1; i < connections.Length; i++)
                            {
                                if (connections[i] != default && senderPlayerId != i)
                                {
                                    RelayPacket(i, stream, qos);
                                }
                            }
                            PurgeChunk(senderPlayerId, con, ref stream, ref ctx);
                        }
                        else if (targetPlayerId == NetworkLinkerConstants.MulticastId)
                        {
                            for (int i = 0; i < multiCastList.Length; i++)
                            {
                                if (multiCastList[i] == ServerPlayerId)
                                {
                                    PurgeChunk(senderPlayerId, con, ref stream, ref ctx);
                                }
                                else
                                {
                                    if (senderPlayerId != multiCastList[i])
                                    {
                                        RelayPacket(multiCastList[i], stream, qos);
                                    }
                                }
                            }
                        }
                        else
                        {
                            if (targetPlayerId == ServerPlayerId)
                            {
                                PurgeChunk(senderPlayerId, con, ref stream, ref ctx);
                            }
                            else
                            {
                                RelayPacket(targetPlayerId, stream, qos);
                            }
                        }
                    }
                }
                multiCastList.Dispose();
            }

            private void PurgeChunk(ushort senderPlayerId, NetworkConnection con, ref DataStreamReader stream, ref DataStreamReader.Context ctx)
            {
                while (true)
                {
                    int pos = stream.GetBytesRead(ref ctx);
                    if (pos >= stream.Length) break;
                    ushort dataLen = stream.ReadUShort(ref ctx);
                    if (dataLen == 0 || pos + dataLen >= stream.Length) break;

                    var chunk = stream.ReadChunk(ref ctx, dataLen);
                    var ctx2 = new DataStreamReader.Context();
                    byte type = chunk.ReadByte(ref ctx2);

                    //if (type != (byte)BuiltInPacket.Type.MeasureRtt)
                    //{
                    //    var c = new DataStreamReader.Context();
                    //    Debug.Log($"Dump : {string.Join(",", chunk.ReadBytesAsArray(ref c, chunk.Length))}");
                    //}

                    dataStream.Add(new DataPacket() { Connection = con, Chunk=chunk });
                }
            }

            private unsafe void RelayPacket(ushort targetPlayerId, DataStreamReader stream, byte qos)
            {
                relayWriter.Clear();
                relayWriter.WriteBytes(stream.GetUnsafeReadOnlyPtr(), stream.Length);
                connections[targetPlayerId].Send(driver, qosPipelines[qos], relayWriter);
                //connections[targetPlayerId].Send(driver, relayWriter);
            }
        }
    }
}
