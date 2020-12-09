using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Networking.Transport.LowLevel.Unsafe;
using UnityEngine;

namespace ICKX.Radome
{
	public abstract class P2PNetworkManagerBase<ConnIdType, PlayerInfo> : GenericNetworkManagerBase<ConnIdType, PlayerInfo>
			where ConnIdType : struct, System.IEquatable<ConnIdType> where PlayerInfo : DefaultPlayerInfo, new()
	{
		public override bool IsFullMesh => true;

		public ConnIdType HostConnId { get; protected set; }

		protected NativeList<ConnIdType> _ConnectionIdList;
		protected NativeHashMap<ConnIdType, ushort> _ConnectionIdPlayerIdTable;

		public P2PNetworkManagerBase(PlayerInfo playerInfo) : base(playerInfo)
		{
			_ConnectionIdList = new NativeList<ConnIdType>(16, Allocator.Persistent);
			_ConnectionIdPlayerIdTable = new NativeHashMap<ConnIdType, ushort>(16, Allocator.Persistent);
		}

		public override void Dispose()
		{
			if (_IsDispose) return;
			JobHandle.Complete();

			_ConnectionIdList.Dispose();
			_ConnectionIdPlayerIdTable.Dispose();

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
			
			if (GetPlayerCount() <= 1)
			{
				StopComplete();
			}
			else
			{
				//登録解除リクエストを送る
				SendHostUnregisterRequestPacket(MyPlayerInfo.UniqueId);
			}
		}

		// サーバーから切断されたらLinkerを破棄して停止
		protected override void StopComplete()
		{
			base.StopComplete();

			if (NetworkState == NetworkConnection.State.Disconnected)
			{
				Debug.LogError("CompleteStop Failed  currentState = " + NetworkState);
				return;
			}
			JobHandle.Complete();

			_ConnectionIdList.Clear();
			_ConnectionIdPlayerIdTable.Clear();
		}
		
		/// <summary>
		/// 他のClientと接続時に実行する
		/// </summary>
		/// <param name="connId"></param>
		protected void OnConnectMethod(ConnIdType connId)
		{
			//まずは一旦接続先に自分のPlayerInfoを教える
			SendConnectedPlayerInfoPacket(connId);
		}

		/// <summary>
		/// 他のClientと切断時に実行する
		/// </summary>
		/// <param name="connId"></param>
		protected void OnDisconnectMethod (ConnIdType connId)
		{
			if (GetUniqueIdByConnId(connId, out ulong uniqueId))
			{
				DisconnectPlayer(uniqueId);
			}
		}

		protected void OnStartHostMethod ()
		{
			if (IsLeader)
			{
				NetworkState = NetworkConnection.State.Connected;
				RegisterPlayer(MyPlayerInfo as PlayerInfo, EmptyConnId, false);
				ExecOnConnect();
			}
		}

		private unsafe void SendRegisterPlayerPacket(ConnIdType connId, ushort id, bool isReconnect)
		{
			var packet = new DataStreamWriter(14 + _ActivePlayerIdList.Count + MyPlayerInfo.PacketSize, Allocator.Temp);

			var defferredLen = packet.Write((ushort)0);
			packet.Write((byte)BuiltInPacket.Type.RegisterPlayer);
			packet.Write(id);
			packet.Write(LeaderStatTime);
			packet.Write((byte)(isReconnect ? 1 : 0));

			packet.Write((byte)_ActivePlayerIdList.Count);
			for (int i = 0; i < _ActivePlayerIdList.Count; i++)
			{
				packet.Write(_ActivePlayerIdList[i]);
			}
			MyPlayerInfo.AppendPlayerInfoPacket(ref packet);

			defferredLen.Update((ushort)(packet.Length - 2));

			SendToConnIdImmediately(connId, packet, true);
			packet.Dispose();
		}

		private unsafe void SendConnectedPlayerInfoPacket(ConnIdType connId)
		{
			var packet = new DataStreamWriter(14 + _ActivePlayerIdList.Count + MyPlayerInfo.PacketSize, Allocator.Temp);

			var defferredLen = packet.Write((ushort)0);
			packet.Write((byte)BuiltInPacket.Type.UpdatePlayerInfo);
			MyPlayerInfo.AppendPlayerInfoPacket(ref packet);

			defferredLen.Update((ushort)(packet.Length - 2));

			SendToConnIdImmediately(connId, packet, true);
			packet.Dispose();
		}

		private unsafe void BroadcastTempMyPlayerInfoPacket()
		{
			var packet = new DataStreamWriter(14 + _ActivePlayerIdList.Count + MyPlayerInfo.PacketSize, Allocator.Temp);

			var defferredLen = packet.Write((ushort)0);
			packet.Write((byte)BuiltInPacket.Type.UpdatePlayerInfo);
			MyPlayerInfo.AppendPlayerInfoPacket(ref packet);

			defferredLen.Update((ushort)(packet.Length - 2));

			BroadcastImmediately(packet, true);
			packet.Dispose();
		}

		private void BroadcastUpdatePlayerInfoPacket(PlayerInfo playerInfo)
		{
			var state = NetworkConnection.State.Disconnected;
			var connInfo = _ActiveConnectionInfoTable[playerInfo.UniqueId];
			if (connInfo != null)
			{
				state = connInfo.State;
			}
			var packet = new DataStreamWriter(2 + playerInfo.PacketSize, Allocator.Temp);
			packet.Write((byte)BuiltInPacket.Type.UpdatePlayerInfo);
			packet.Write((byte)state);
			playerInfo.AppendPlayerInfoPacket(ref packet);
			Broadcast(packet, QosType.Reliable);
			packet.Dispose();
		}

		private void SendHostUnregisterRequestPacket(ulong unregisterUniqueId)
		{
			var packet = new DataStreamWriter(11, Allocator.Temp);
			var defferredLen = packet.Write((ushort)0);
			packet.Write((byte)BuiltInPacket.Type.UnregisterPlayer);
			packet.Write(unregisterUniqueId);

			defferredLen.Update((ushort)(packet.Length - 2));
			SendToConnIdImmediately(HostConnId, packet, true);
			packet.Dispose();
		}

		private void BroadcastUnregisterRequestPacket(ulong unregisterUniqueId)
		{
			var packet = new DataStreamWriter(11, Allocator.Temp);
			var defferredLen = packet.Write((ushort)0);
			packet.Write((byte)BuiltInPacket.Type.UnregisterPlayer);
			packet.Write(unregisterUniqueId);

			defferredLen.Update((ushort)(packet.Length - 2));
			BroadcastImmediately(packet, true);
			packet.Dispose();
		}
		
		/// <summary>
		/// ConnIdをNativeListに集める
		/// </summary>
		protected void CollectConnIdTable()
		{
			_ConnectionIdList.Clear();
			_ConnectionIdPlayerIdTable.Clear();

			foreach(var pair in _ConnIdUniqueIdable)
			{
				ConnIdType connId = pair.Key;
				ConnectionInfo connInfo = GetConnectionInfoByUniqueId(pair.Value);
				if (connInfo != null 
					&& connInfo.State == NetworkConnection.State.Connected
					&& GetPlayerIdByConnId(connId, out ushort playerId))
				{
					while (playerId >= _ConnectionIdList.Length) _ConnectionIdList.Add(EmptyConnId);
					_ConnectionIdList[playerId] = connId;
					_ConnectionIdPlayerIdTable.TryAdd(connId, playerId);
				}
			}
		}

		/// <summary>
		/// プレイヤー登録・解除などのパケット解析を行う
		/// </summary>
		/// <param name="connId"></param>
		/// <param name="type"></param>
		/// <param name="chunk"></param>
		/// <param name="ctx2"></param>
		/// <returns>接続終了でパケット解析を止める場合はtrue</returns>
		protected override bool DeserializePacket(ConnIdType connId, ulong uniqueId, byte type, ref DataStreamReader chunk, ref DataStreamReader.Context ctx2)
		{
			//Debug.Log($"DeserializePacket : {uniqueId} : {(BuiltInPacket.Type)type} {chunk.Length}");
			bool isReconnect;
			switch (type)
			{
				case (byte)BuiltInPacket.Type.UpdatePlayerInfo:
					var UpdatePlayerInfo = new PlayerInfo();
					UpdatePlayerInfo.Deserialize(ref chunk, ref ctx2);

					isReconnect = GetPlayerIdByUniqueId(UpdatePlayerInfo.UniqueId, out ushort playerId);

					Debug.Log($"UpdatePlayerInfo playerId={playerId} IsRec={isReconnect}");
					if (isReconnect && playerId == UpdatePlayerInfo.PlayerId)
					{
						SetConnState(UpdatePlayerInfo.UniqueId, NetworkConnection.State.Connected);
						break;
					}

					if (IsLeader)
					{
						if (isReconnect)
						{
							UpdatePlayerInfo.PlayerId = playerId;
						}
						else
						{
							UpdatePlayerInfo.PlayerId = GetDeactivePlayerId();
						}
						RegisterPlayer(UpdatePlayerInfo, connId, isReconnect);
						SendRegisterPlayerPacket(connId, UpdatePlayerInfo.PlayerId, isReconnect);
					}
					else
					{
						//HostはType.RegisterPlayerの処理で登録するので無視
						if (UpdatePlayerInfo.PlayerId == 0) break;

						bool invailedPlayerId = UpdatePlayerInfo.PlayerId == NetworkLinkerConstants.InvailedId;
						Debug.Log(UpdatePlayerInfo.UniqueId + " : " + invailedPlayerId);
						if (!invailedPlayerId)
						{
							RegisterPlayer(UpdatePlayerInfo, connId, isReconnect);
						}
						else
						{
							//ID未登録ならConnInfoの更新だけ
							SetActiveConnInfo(UpdatePlayerInfo.UniqueId, connId, NetworkConnection.State.Connecting);
						}
					}
					break;
				case (byte)BuiltInPacket.Type.RegisterPlayer:
					if (!IsLeader)
					{
						//if (IsEquals( HostConnId, senderId))
						//{
						//	throw new System.NotImplementedException("接続開始とOwnerが変わってる");
						//}
						HostConnId = connId;

						NetworkState = NetworkConnection.State.Connected;

						MyPlayerInfo.PlayerId = chunk.ReadUShort(ref ctx2);
						LeaderStatTime = chunk.ReadLong(ref ctx2);
						isReconnect = chunk.ReadByte(ref ctx2) == 1;

						byte count = chunk.ReadByte(ref ctx2);
						_ActivePlayerIdList.Clear();
						for (int i = 0; i < count; i++)
						{
							_ActivePlayerIdList.Add(chunk.ReadByte(ref ctx2));
						}

						var HostPlayerInfo = new PlayerInfo();
						HostPlayerInfo.Deserialize(ref chunk, ref ctx2);

						//サーバーを登録
						RegisterPlayer(HostPlayerInfo, HostConnId, isReconnect);
						//自分を登録
						RegisterPlayer(MyPlayerInfo as PlayerInfo, EmptyConnId, isReconnect);

						//自分のPlayerInfoを登録してもらう
						BroadcastTempMyPlayerInfoPacket();

						ExecOnConnect();
					}
					break;
				case (byte)BuiltInPacket.Type.UnregisterPlayer:
					ulong unregisterUniqueId = chunk.ReadULong(ref ctx2);
					Debug.Log("UnregisterPlayer : " + unregisterUniqueId + " : " + IsStopRequest);
					if (IsLeader)
					{
						//他のClientに通達
						BroadcastUnregisterRequestPacket(unregisterUniqueId);
						UnregisterPlayer(unregisterUniqueId);
					}
					else
					{
						if(IsStopRequest && unregisterUniqueId == MyPlayerInfo.UniqueId)
						{
							StopComplete();
						}else
						{
							UnregisterPlayer(unregisterUniqueId);
						}
					}
					break;
				default:
					//自分宛パケットの解析
					if (GetPlayerIdByUniqueId(uniqueId, out var senderPlayerId))
					{
						RecieveData(senderPlayerId, uniqueId, type, chunk, ctx2);
					}
					else
					{
						//Debug.LogError("Not Found UserIDPlayerIDTable : " + senderId);
					}
					break;
			}
			return false;
		}

	}
}