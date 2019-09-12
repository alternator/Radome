using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Networking.Transport.LowLevel.Unsafe;
using UnityEngine;

namespace ICKX.Radome
{
	
	/*
	public abstract class SCNetowrkManagerBase<ConnIdType, PlayerInfo> : GenericNetworkManagerBase<ConnIdType, PlayerInfo>
			where ConnIdType : struct, System.IEquatable<ConnIdType> where PlayerInfo : DefaultPlayerInfo, new()
	{
		public override bool IsFullMesh => false;

		public ConnIdType HostConnId { get; protected set; }

		protected NativeList<ConnIdType> _ConnectionIdList;
		protected NativeHashMap<ConnIdType, ushort> _ConnectionIdPlayerIdTable;

		public SCNetowrkManagerBase(PlayerInfo playerInfo) : base(playerInfo)
		{
			MyPlayerInfo = playerInfo;

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

			NetworkState = NetworkConnection.State.AwaitingResponse;
			IsStopRequest = true;

			if (IsLeader)
			{
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
			else
			{
				//Playerリストから削除するリクエストを送る
				SendUnregisterPlayerPacket();
			}
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

			_ConnectionIdList.Clear();
			_ConnectionIdPlayerIdTable.Clear();

			MyPlayerInfo.PlayerId = 0;
			IsStopRequest = false;
		}

		//新しいPlayerを登録する処理
		protected virtual void RegisterPlayer(PlayerInfo playerInfo, ConnIdType connId = default)
		{
			base.RegisterPlayerId(playerInfo.PlayerId, playerInfo.UniqueId);

			//_ConnectionIdPlayerIdTable更新
			if (!IsEquals(connId, default))
			{
				_ConnectionIdPlayerIdTable.TryAdd(connId, playerInfo.PlayerId);
			}

			//_ConnectionIdList更新
			while (playerInfo.PlayerId >= _ConnectionIdList.Length) _ConnectionIdList.Add(default);
			_ConnectionIdList[playerInfo.PlayerId] = connId;

			//ConnectionInfo更新
			while (playerInfo.PlayerId >= _ActiveConnectionInfoList.Count) _ActiveConnectionInfoList.Add(null);
			if (_ActiveConnectionInfoList[playerInfo.PlayerId] == null)
			{
				_ActiveConnectionInfoList[playerInfo.PlayerId] = new ConnectionInfo(NetworkConnection.State.Connected);
			}
			else
			{
				var connInfo = _ActiveConnectionInfoList[playerInfo.PlayerId] as ConnectionInfo;
				connInfo.State = NetworkConnection.State.Connected;
			}

			//playerInfo領域確保
			while (playerInfo.PlayerId >= _ActivePlayerInfoList.Count) _ActivePlayerInfoList.Add(null);
			_ActivePlayerInfoList[playerInfo.PlayerId] = playerInfo;

			_UniquePlayerIdTable[playerInfo.UniqueId] = playerInfo.PlayerId;

			//イベント通知
			ExecOnRegisterPlayer(playerInfo.PlayerId, playerInfo.UniqueId);
		}

		//Playerを登録解除する処理
		protected virtual void UnregisterPlayer(ulong uniqueId)
		{
			if (!GetPlayerId(uniqueId, out ushort playerId))
			{
				return;
			}

			var connId = _ConnectionIdList[playerId];

			if (IsActivePlayerId(playerId))
			{
				if (IsEquals(connId, default))
				{
					_ConnectionIdPlayerIdTable.Remove(connId);
				}
				_UniquePlayerIdTable.Remove(uniqueId);
				_ActiveConnectionInfoList[playerId] = default;
				_ActivePlayerInfoList[playerId] = null;
				_ConnectionIdList[playerId] = default;

				base.UnregisterPlayerId(playerId);

				ExecOnUnregisterPlayer(playerId, uniqueId);
			}
		}

		protected virtual void ReconnectPlayer(PlayerInfo playerInfo, ConnIdType connId)
		{
			bool isFirst = false;

			base.RegisterPlayerId(playerInfo.PlayerId, playerInfo.UniqueId);

			//_ConnectionIdPlayerIdTable更新
			if (!IsEquals(connId, default))
			{
				_ConnectionIdPlayerIdTable.TryAdd(connId, playerInfo.PlayerId);
			}

			//_ConnectionIdList更新
			while (playerInfo.PlayerId >= _ConnectionIdList.Length) _ConnectionIdList.Add(default);
			_ConnectionIdList[playerInfo.PlayerId] = connId;

			//ConnectionInfo更新
			while (playerInfo.PlayerId >= _ActiveConnectionInfoList.Count) _ActiveConnectionInfoList.Add(null);
			if (_ActiveConnectionInfoList[playerInfo.PlayerId] == null)
			{
				isFirst = true;
				_ActiveConnectionInfoList[playerInfo.PlayerId] = new ConnectionInfo(NetworkConnection.State.Connected);
			}
			else
			{
				var connInfo = _ActiveConnectionInfoList[playerInfo.PlayerId] as ConnectionInfo;
				connInfo.State = NetworkConnection.State.Connected;
			}

			//playerInfo領域確保
			while (playerInfo.PlayerId >= _ActivePlayerInfoList.Count) _ActivePlayerInfoList.Add(null);
			_ActivePlayerInfoList[playerInfo.PlayerId] = playerInfo;

			_UniquePlayerIdTable[playerInfo.UniqueId] = playerInfo.PlayerId;

			//イベント通知
			if (isFirst)
			{
				ExecOnRegisterPlayer(playerInfo.PlayerId, playerInfo.UniqueId);
			}
			ExecOnReconnectPlayer(playerInfo.PlayerId, playerInfo.UniqueId);
		}

		protected virtual void DisconnectPlayer(ulong uniqueId)
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

		protected void SendRegisterPlayerPacket(ushort targetPlayerId)
		{
			var addPlayerPacket = MyPlayerInfo.CreateUpdatePlayerInfoPacket((byte)BuiltInPacket.Type.RegisterPlayer);
			Send(targetPlayerId, addPlayerPacket, QosType.Reliable);
			addPlayerPacket.Dispose();
		}

		protected void SendReconnectPlayerPacket(ushort targetPlayerId)
		{
			var recconectPlayerPacket = MyPlayerInfo.CreateUpdatePlayerInfoPacket((byte)BuiltInPacket.Type.ReconnectPlayer);
			Send(targetPlayerId, recconectPlayerPacket, QosType.Reliable);
			recconectPlayerPacket.Dispose();
		}

		protected void SendUpdatePlayerPacket(ushort targetPlayerId)
		{
			var addPlayerPacket = MyPlayerInfo.CreateUpdatePlayerInfoPacket((byte)BuiltInPacket.Type.UpdatePlayerInfo);
			Send(targetPlayerId, addPlayerPacket, QosType.Reliable);
			addPlayerPacket.Dispose();
		}

		protected void SendUpdateAllPlayerPacket(ushort targetPlayerId)
		{
			var packet = new DataStreamWriter(NetworkLinkerConstants.MaxPacketSize, Allocator.Temp);

			packet.Write((byte)BuiltInPacket.Type.UpdatePlayerInfo);
			for (ushort i = 0; i < _ActivePlayerInfoList.Count; i++)
			{
				if (i == ServerPlayerId) continue;

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

				if (packet.Length > NetworkLinkerConstants.MaxPacketSize - playerInfo.PacketSize - 1)
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
				playerInfo = new PlayerInfo { PlayerId = playerId };
				var packet = new DataStreamWriter(2 + playerInfo.PacketSize, Allocator.Temp);
				packet.Write((byte)BuiltInPacket.Type.UpdatePlayerInfo);
				packet.Write((byte)NetworkConnection.State.Disconnected);
				playerInfo.AppendPlayerInfoPacket(ref packet);
				Broadcast(packet, QosType.Reliable);
				packet.Dispose();
			}
		}

		protected void SendRegisterPlayerPacket(ushort id, bool isReconnect)
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

		protected void SendUnregisterPlayerPacket()
		{
			using (var unregisterPlayerPacket = new DataStreamWriter(9, Allocator.Temp))
			{
				unregisterPlayerPacket.Write((byte)BuiltInPacket.Type.UnregisterPlayer);
				unregisterPlayerPacket.Write(MyPlayerInfo.UniqueId);
				Send(ServerPlayerId, unregisterPlayerPacket, QosType.Reliable);
			}
		}

		protected void BroadcastStopNetworkPacket()
		{
			using (var stopNetworkPacket = new DataStreamWriter(2, Allocator.Temp))
			{
				stopNetworkPacket.Write((byte)BuiltInPacket.Type.StopNetwork);
				stopNetworkPacket.Write((byte)0);    //TODO error code

				Broadcast(stopNetworkPacket, QosType.Reliable, true);
			}
		}
		


		protected virtual void DeserializePacketForClient(ushort senderPlayerId, byte type, ref DataStreamReader chunk, DataStreamReader.Context ctx2)
		{
			var playerInfo = (senderPlayerId < _ActivePlayerInfoList.Count) ? _ActivePlayerInfoList[senderPlayerId] : null;
			ulong senderUniqueId = (playerInfo == null) ? 0 : playerInfo.UniqueId;

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
							_ActiveConnectionInfoList[i] = new ConnectionInfo(NetworkConnection.State.Connecting);
						}
					}

					var serverPlayerInfo = new PlayerInfo();
					serverPlayerInfo.Deserialize(ref chunk, ref ctx2);

					//サーバーを登録
					RegisterPlayer(serverPlayerInfo);
					//自分を登録
					RegisterPlayer(MyPlayerInfo as PlayerInfo);

					ExecOnConnect();

					//recconect
					if (isRecconnect)
					{
						ReconnectPlayer(MyPlayerInfo as PlayerInfo, default);
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
											ReconnectPlayer(updatePlayerInfo, default);
										}
										else if (!(connInfo != null && connInfo.State == NetworkConnection.State.Connected))
										{
											Debug.Log("RegisterPlayerId : " + state + " : " + updatePlayerInfo.PlayerId);
											RegisterPlayer(updatePlayerInfo);
										}
										break;
									case NetworkConnection.State.Disconnected:
										if (connInfo != null && connInfo.State != NetworkConnection.State.Disconnected)
										{
											Debug.Log("UnregisterPlayerId : " + state + " : " + updatePlayerInfo.PlayerId);
											if (GetUniqueId(updatePlayerInfo.PlayerId, out ulong uniqueId))
											{
												UnregisterPlayer(uniqueId);
											}
										}
										break;
									case NetworkConnection.State.AwaitingResponse:
										if (connInfo != null && connInfo.State != NetworkConnection.State.AwaitingResponse)
										{
											Debug.Log("DisconnectPlayerId : " + state + " : " + updatePlayerInfo.PlayerId);
											DisconnectPlayer(updatePlayerInfo.UniqueId);
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
	}
	*/
}