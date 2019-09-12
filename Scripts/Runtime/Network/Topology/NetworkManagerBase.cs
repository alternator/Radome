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
        public ushort PlayerId = NetworkLinkerConstants.InvailedId;
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

		public virtual void CopyTo (DefaultPlayerInfo playerInfo)
		{
			playerInfo.PlayerId = this.PlayerId;
			playerInfo.UniqueId = this.UniqueId;
		}
    }

    public static class NetworkLinkerConstants
    {
        public const int MaxPacketSize = NetworkParameterConstants.MTU * 4 / 5;
		public const ushort InvailedId = ushort.MaxValue;
		public const ushort BroadcastId = ushort.MaxValue -1;
		public const ushort MulticastId = ushort.MaxValue -2;

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
		public delegate void OnConnectEvent();
        public delegate void OnDisconnectAllEvent(byte errorCode);
        public delegate void OnConnectFailedEvent(byte errorCode);

        public delegate void OnReconnectPlayerEvent(ushort playerId, ulong uniqueId);
        public delegate void OnDisconnectPlayerEvent(ushort playerId, ulong uniqueId);
        public delegate void OnRegisterPlayerEvent(ushort playerId, ulong uniqueId);
        public delegate void OnUnregisterPlayerEvent(ushort playerId, ulong uniqueId);
        public delegate void OnRecievePacketEvent(ushort senderPlayerId, ulong uniqueId, byte type, DataStreamReader stream, DataStreamReader.Context ctx);

        public ushort ServerPlayerId { get; protected set; } = 0;

        protected DataStreamWriter _SinglePacketBuffer;
        protected ChuckPacketManager _BroadcastRudpChunkedPacketManager;
        protected ChuckPacketManager _BroadcastUdpChunkedPacketManager;
		
        protected List<byte> _ActivePlayerIdList = new List<byte>(16);

		protected List<ulong> _PlayerIdUniqueIdList = new List<ulong>();

		protected Dictionary<ulong, DefaultPlayerInfo> _ActivePlayerInfoTable = new Dictionary<ulong, DefaultPlayerInfo>();
		public IReadOnlyDictionary<ulong, DefaultPlayerInfo> ActivePlayerInfoTable { get { return _ActivePlayerInfoTable; } }
		
		public NetworkConnection.State NetworkState { get; protected set; } = NetworkConnection.State.Disconnected;

        public DefaultPlayerInfo MyPlayerInfo { get; protected set; }

        public ushort MyPlayerId { get { return (MyPlayerInfo == null ? (ushort)0 : MyPlayerInfo.PlayerId); } }
        public bool IsLeader { get; protected set; }
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

		
		public virtual void OnApplicationPause (bool pause)
		{

		}
		
		public virtual void Stop()
        {
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

		protected virtual void StopComplete()
        {
            if (NetworkState == NetworkConnection.State.Disconnected)
            {
                Debug.LogError("Start Failed  currentState = " + NetworkState);
                return;
			}
			MyPlayerInfo.PlayerId = 0;
			IsStopRequest = false;

			JobHandle.Complete();
            _SinglePacketBuffer.Clear();
            _BroadcastRudpChunkedPacketManager.Clear();
            _BroadcastUdpChunkedPacketManager.Clear();

			_PlayerIdUniqueIdList.Clear();
			_ActivePlayerIdList.Clear();
			_ActivePlayerInfoTable.Clear();
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

		public bool IsRegisteredPlayer (ushort playerId, ulong uniqueId)
		{
			if (_PlayerIdUniqueIdList.Count <= playerId) return false;
			if (_PlayerIdUniqueIdList[playerId] != uniqueId) return false;
			return true;
		}

		protected static void SetValueSafetyForList<T>(ref List<T> list, int index, T value)
		{
			while (index >= list.Count) list.Add(default);
			list[index] = value;
		}

		protected static void SetValueSafetyForNativeList<T>(ref NativeList<T> list, int index, T value) where T : struct, System.IEquatable<T>
		{
			while (index >= list.Length) list.Add(default);
			list[index] = value;
		}

		public bool GetUniqueIdByPlayerId(ushort playerId, out ulong uniqueId)
		{
			uniqueId = default;
			if (playerId < _PlayerIdUniqueIdList.Count)
			{
				uniqueId = _PlayerIdUniqueIdList[playerId];
			}
			return uniqueId != default;
		}
		public bool GetPlayerIdByUniqueId(ulong uniqueId, out ushort playerId)
		{
			playerId = NetworkLinkerConstants.InvailedId;
			var playerInfo = GetPlayerInfoByUniqueId(uniqueId);
			if (playerInfo != null)
			{
				playerId = playerInfo.PlayerId;
			}
			return playerId != NetworkLinkerConstants.InvailedId;
		}
		public DefaultPlayerInfo GetPlayerInfoByPlayerId(ushort playerId)
		{
			if (GetUniqueIdByPlayerId(playerId, out var uniqueId))
			{
				return GetPlayerInfoByUniqueId(uniqueId);
			}
			return null;
		}
		public DefaultPlayerInfo GetPlayerInfoByUniqueId(ulong uniqueId)
		{
			if (_ActivePlayerInfoTable.TryGetValue(uniqueId, out var playerInfo))
			{
				return playerInfo;
			}
			return null;
		}
		
        protected void RegisterPlayerId(ushort playerId, ulong uniqueId)
        {
			SetValueSafetyForList(ref _PlayerIdUniqueIdList, playerId, uniqueId);

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

        protected void UnregisterPlayerId(ushort playerId)
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

        public abstract void OnFirstUpdate();
        public abstract void OnLastUpdate();

        protected virtual unsafe void SendSingle(ushort targetPlayerId, DataStreamWriter data, QosType qos)
        {
            byte* dataPtr = DataStreamUnsafeUtility.GetUnsafeReadOnlyPtr(data);

			//Debug.Log("sendsingle " + targetPlayerId + " : Len=" + data.Length);

            if (_SinglePacketBuffer.Length + data.Length + 5 >= _SinglePacketBuffer.Capacity)
            {
                _SinglePacketBuffer.Capacity *= 2;
            }
            _SinglePacketBuffer.Write((byte)qos);
            _SinglePacketBuffer.Write(targetPlayerId);
            _SinglePacketBuffer.Write((ushort)data.Length);
            _SinglePacketBuffer.WriteBytes(dataPtr, data.Length);
        }

        public virtual void Send(ulong targetUniqueId, DataStreamWriter data, QosType qos)
        {
            if (GetPlayerIdByUniqueId(targetUniqueId, out ushort playerId))
            {
                Send(playerId, data, qos);
            }
        }

        /// <summary>
        /// Player1人にパケットを送信
        /// </summary>
        public virtual void Send(ushort targetPlayerId, DataStreamWriter data, QosType qos)
        {
            //Debug.Log($"Sennd {targetPlayerId} Len={data.Length} qos={qos}");
            if (NetworkState == NetworkConnection.State.Disconnected)
            {
                Debug.LogError("Send Failed : NetworkConnection.State.Disconnected");
                return;
            }

            SendSingle(targetPlayerId, data, qos);
        }

        /// <summary>
        /// 複数のPlayerにパケットを送信
        /// </summary>
        public virtual unsafe void Multicast(NativeList<ushort> playerIdList, DataStreamWriter data, QosType qos)
        {
            //Debug.Log($"Sennd {playerIdList.Length} Len={data.Length} qos={qos}");
            if (NetworkState == NetworkConnection.State.Disconnected)
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
        public virtual unsafe void Multicast(NativeList<ulong> uniqueIdList, DataStreamWriter data, QosType qos)
        {
            //Debug.Log($"Multicast {uniqueIdList.Length} Len={data.Length} qos={qos}");
            if (NetworkState == NetworkConnection.State.Disconnected)
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
                if (GetPlayerIdByUniqueId(uniqueIdList[i], out ushort playerId))
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
        public virtual void Broadcast(DataStreamWriter data, QosType qos, bool noChunk = false)
        {
			//if (qos == QosType.Reliable)
			//{
			//	Debug.Log($"Brodcast Len={data.Length} qos={qos}");
			//}
			if (NetworkState == NetworkConnection.State.Disconnected)
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
                if (qos == QosType.Unreliable)
                {
                    _BroadcastUdpChunkedPacketManager.AddChunk(data);
                }
                if (qos == QosType.Reliable)
                {
                    _BroadcastRudpChunkedPacketManager.AddChunk(data);
                }
            }
        }
        //public abstract void Send(ushort targetPlayerId, DataStreamWriter data, QosType qos);
        //public abstract void Send(ulong targetUniqueId, DataStreamWriter data, QosType qos);
        //public abstract void Multicast(NativeList<ushort> playerIdList, DataStreamWriter data, QosType qos);
        //public abstract void Multicast(NativeList<ulong> uniqueIdList, DataStreamWriter data, QosType qos);
        //public abstract void Broadcast(DataStreamWriter data, QosType qos, bool noChunk = false);
    }
		
	public abstract class GenericNetworkManagerBase<ConnIdType, PlayerInfo> : NetworkManagerBase
			where ConnIdType : struct, System.IEquatable<ConnIdType> where PlayerInfo : DefaultPlayerInfo, new()
	{
		public class ConnectionInfo
		{
			public ConnIdType ConnId;
			public NetworkConnection.State State;
			public float DisconnectTime;

			public ConnectionInfo(ConnIdType connId, NetworkConnection.State state)
			{
				this.ConnId = connId;
				this.State = state;
				this.DisconnectTime = 0.0f;
			}
		}
		
		protected Dictionary<ConnIdType, ulong> _ConnIdUniqueIdable = new Dictionary<ConnIdType, ulong>();
		public IReadOnlyDictionary<ConnIdType, ulong> ConnIdUniqueIdable { get { return _ConnIdUniqueIdable; } }

		protected Dictionary<ulong, ConnectionInfo> _ActiveConnectionInfoTable = new Dictionary<ulong, ConnectionInfo>();
		public IReadOnlyDictionary<ulong, ConnectionInfo> ActiveConnectionInfoTable { get { return _ActiveConnectionInfoTable; } }

		protected abstract ConnIdType EmptyConnId { get; }

		protected static bool IsEquals(ConnIdType a, ConnIdType b)
		{
			return EqualityComparer<ConnIdType>.Default.Equals(a, b);
		}

		public GenericNetworkManagerBase(PlayerInfo playerInfo) : base()
		{
			MyPlayerInfo = playerInfo;
		}

		public override void Dispose()
		{
			if (_IsDispose) return;
			JobHandle.Complete();

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
				Debug.LogError("Stop Failed  currentState = " + NetworkState);
				return;
			}
			JobHandle.Complete();

			NetworkState = NetworkConnection.State.AwaitingResponse;
			IsStopRequest = true;
		}

		// サーバーから切断されたらLinkerを破棄して停止
		protected override void StopComplete()
		{
			if (NetworkState != NetworkConnection.State.Disconnected)
			{
				for (ushort i = 0; i < _PlayerIdUniqueIdList.Count; i++)
				{
					ExecOnUnregisterPlayer(i, _PlayerIdUniqueIdList[i]);
				}
			}

			base.StopComplete();

			if (NetworkState == NetworkConnection.State.Disconnected)
			{
				Debug.LogError("CompleteStop Failed  currentState = " + NetworkState);
				return;
			}
			JobHandle.Complete();
			
			_ConnIdUniqueIdable.Clear();
			_ActiveConnectionInfoTable.Clear();

			MyPlayerInfo.PlayerId = 0;
			IsStopRequest = false;
		}

		protected void SetConnState(ulong uniqueId, NetworkConnection.State state)
		{
			if (_ActiveConnectionInfoTable.TryGetValue(uniqueId, out var connInfo))
			{
				connInfo.State = NetworkConnection.State.Connected;
			}
		}

		protected void SetActiveConnInfo(ulong uniqueId, ConnIdType connId, NetworkConnection.State state)
		{
			_ConnIdUniqueIdable[connId] = uniqueId;

			if (_ActiveConnectionInfoTable.TryGetValue(uniqueId, out var connInfo))
			{
				connInfo.ConnId = connId;
				connInfo.State = NetworkConnection.State.Connected;
			}
			else
			{
				_ActiveConnectionInfoTable[uniqueId] = new ConnectionInfo(connId, NetworkConnection.State.Connected);
			}
		}

		protected void SetActivePlayerInfo(PlayerInfo playerInfo)
		{
			if (_ActivePlayerInfoTable.TryGetValue(playerInfo.UniqueId, out var prevPlayerInfo))
			{
				playerInfo.CopyTo(prevPlayerInfo);
			}
			else
			{
				_ActivePlayerInfoTable[playerInfo.UniqueId] = playerInfo;
			}
		}

		//新しいPlayerを登録する処理
		protected virtual void RegisterPlayer(PlayerInfo playerInfo, ConnIdType connId, bool isReconnect)
		{
			if (IsRegisteredPlayer(playerInfo.PlayerId, playerInfo.UniqueId))
			{
				SetActiveConnInfo(playerInfo.UniqueId, connId, NetworkConnection.State.Connected);
				//イベント通知
				if (isReconnect)
				{
					Debug.Log("Reconnect " + playerInfo.PlayerId + " : " + playerInfo.UniqueId);
					ExecOnReconnectPlayer(playerInfo.PlayerId, playerInfo.UniqueId);
				}
				return;
			}

			SetActiveConnInfo(playerInfo.UniqueId, connId, NetworkConnection.State.Connected);
			SetActivePlayerInfo(playerInfo);

			Debug.Log($"RegisterPlayer newID={playerInfo.PlayerId} UniqueId={playerInfo.UniqueId} IsRec={isReconnect}");

			RegisterPlayerId(playerInfo.PlayerId, playerInfo.UniqueId);

			_PlayerIdUniqueIdList[playerInfo.PlayerId] = playerInfo.UniqueId;

			ExecOnRegisterPlayer(playerInfo.PlayerId, playerInfo.UniqueId);
			//イベント通知
			if (isReconnect)
			{
				Debug.Log("Reconnect " + playerInfo.PlayerId + " : " + playerInfo.UniqueId);
				ExecOnReconnectPlayer(playerInfo.PlayerId, playerInfo.UniqueId);
			}
		}

		//Playerを登録解除する処理
		protected virtual void UnregisterPlayer(ulong uniqueId)
		{
			if (!GetPlayerIdByUniqueId(uniqueId, out ushort playerId))
			{
				return;
			}
			var connInfo = GetConnectionInfoByUniqueId(uniqueId);

			Debug.Log($"UnregisterPlayer playerId={playerId} UniqueId={uniqueId}");

			if (connInfo != null)
			{
				_PlayerIdUniqueIdList[playerId] = default;
				_ConnIdUniqueIdable.Remove(connInfo.ConnId);

				_ActiveConnectionInfoTable.Remove(uniqueId);
				_ActivePlayerInfoTable.Remove(uniqueId);

				base.UnregisterPlayerId(playerId);

				ExecOnUnregisterPlayer(playerId, uniqueId);
			}
		}

		protected virtual void DisconnectPlayer(ulong uniqueId)
		{
			if (!GetPlayerIdByUniqueId(uniqueId, out ushort playerId))
			{
				return;
			}
			Debug.Log($"DisconnectPlayer playerId={playerId} UniqueId={uniqueId}");

			if (IsActivePlayerId(playerId))
			{
				var info = GetConnectionInfoByPlayerId(playerId);
				if (info != null)
				{
					_ConnIdUniqueIdable.Remove(info.ConnId);

					info.ConnId = EmptyConnId;
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

		public bool GetPlayerIdByConnId(ConnIdType connId, out ushort playerId)
		{
			playerId = NetworkLinkerConstants.InvailedId;
			if (_ConnIdUniqueIdable.TryGetValue(connId, out var uniqueId))
			{
				return GetPlayerIdByUniqueId(uniqueId, out playerId);
			}
			return false;
		}
		public bool GetUniqueIdByConnId(ConnIdType connId, out ulong uniqueId)
		{
			return _ConnIdUniqueIdable.TryGetValue(connId, out uniqueId);
		}
		public ConnectionInfo GetConnectionInfoByPlayerId(ushort playerId)
		{
			if (GetUniqueIdByPlayerId(playerId, out ulong uniqueId))
			{
				return GetConnectionInfoByUniqueId(uniqueId);
			}
			return null;
		}
		public ConnectionInfo GetConnectionInfoByUniqueId(ulong uniqueId)
		{
			if (_ActiveConnectionInfoTable.TryGetValue(uniqueId, out var info))
			{
				return info;
			}
			return null;
		}
		public ConnectionInfo GetConnectionInfoByConnId (ConnIdType connId)
		{
			if (_ConnIdUniqueIdable.TryGetValue(connId, out var uniqueId))
			{
				return GetConnectionInfoByUniqueId(uniqueId);
			}
			return null;
		}

		public DefaultPlayerInfo GetPlayerInfoByConnId(ConnIdType connId)
		{
			if (_ConnIdUniqueIdable.TryGetValue(connId, out var uniqueId))
			{
				return GetPlayerInfoByUniqueId(uniqueId);
			}
			return null;
		}

		protected abstract void SendToConnIdImmediately(ConnIdType connId, DataStreamWriter packet, bool reliable);

		protected void BroadcastImmediately(DataStreamWriter packet, bool reliable)
		{
			foreach (var connId in _ConnIdUniqueIdable.Keys)
			{
				SendToConnIdImmediately(connId, packet, reliable);
			}
		}
		
		/// <summary>
		/// 切断後再接続してこないPlayerの登録を解除する
		/// </summary>
		/// <param name="timeOut"></param>
		protected void CheckTimeOut(float timeOut)
		{
			//一定時間切断したままのplayerの登録を解除
			for (ushort i = 0; i < _PlayerIdUniqueIdList.Count; i++)
			{
				var conInfo = GetConnectionInfoByPlayerId(i);
				var playerInfo = GetPlayerInfoByPlayerId(i);
				if (conInfo != null && playerInfo != null)
				{
					if (conInfo.State == NetworkConnection.State.Connecting)
					{
						if (Time.realtimeSinceStartup - conInfo.DisconnectTime > timeOut)
						{
							UnregisterPlayerId(i);
						}
					}
				}
			}
		}

		/// <summary>
		/// プレイヤー登録・解除などのパケット解析を行う
		/// </summary>
		/// <param name="senderId"></param>
		/// <param name="type"></param>
		/// <param name="chunk"></param>
		/// <param name="ctx2"></param>
		/// <returns>接続終了でパケット解析を止める場合はtrue</returns>
		protected abstract bool DeserializePacket(ConnIdType senderId, ulong uniqueId, byte type, ref DataStreamReader chunk, ref DataStreamReader.Context ctx2);
	}
}
