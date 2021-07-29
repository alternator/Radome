using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Networking.Transport;
using UnityEngine;
using static ICKX.Radome.NetworkManagerBase;

namespace ICKX.Radome
{
	/// <summary>
	/// 記録モードでは送るパケットと受け取ったパケットをリプレイに記録
	/// 再生モードでは記録したパケットを代わりに流すことでリプレイを実現する
	/// </summary>
	public static class RecordablePacketManager
	{
		/// <summary>
		/// 送信されたパケットを受け取る
		/// </summary>
		public static event OnRecievePacketEvent OnRecieveOrReplayPacket = null;

		public static bool IsLeader => GamePacketManager.IsLeader;
		public static ushort PlayerId => GamePacketManager.PlayerId;
		public static ulong UniqueId => GamePacketManager.UniqueId;
		public static long LeaderStartTime => GamePacketManager.LeaderStartTime;
		public static long CurrentUnixTime => GamePacketManager.CurrentUnixTime;

		public static uint ProgressTimeSinceStartup => GamePacketManager.ProgressTimeSinceStartup;

		public static NetworkConnection.State NetworkState => GamePacketManager.NetworkState;

		private static bool[] _RecordablePacketTypes = new bool[byte.MaxValue];

		public static IReadOnlyList<bool> RecordablePacketTypes => _RecordablePacketTypes;

		[RuntimeInitializeOnLoadMethod]
		static void Initialize()
		{
			GamePacketManager.OnRecievePacket += ExecOnRecievePacket;
		}

		/// <summary>
		/// 指定したPacketTypeを記録する
		/// </summary>
		/// <param name="packetType"></param>
		/// <param name="enable"></param>
		public static void SetRecordPacketType (byte packetType, bool enable)
		{
			_RecordablePacketTypes[packetType] = enable;
		}

		//ReplayFileから読み込んだパケットを展開する
		internal static void ExecOnReplayPacket(ushort senderPlayerId, ulong senderUniqueId, byte type, NativeStreamReader stream)
		{
			OnRecieveOrReplayPacket?.Invoke(senderPlayerId, senderUniqueId, type, stream);
		}


		private static void ExecOnRecievePacket(ushort senderPlayerId, ulong senderUniqueId, byte type, NativeStreamReader stream)
		{
			//記録
			if(_RecordablePacketTypes[type])
			{

			}

			OnRecieveOrReplayPacket?.Invoke(senderPlayerId, senderUniqueId, type, stream);
		}

		public static unsafe void Send(ushort playerId, NativeStreamWriter data, QosType qos)
		{
			//記録
			if (_RecordablePacketTypes[data.GetUnsafePtr()[0]])
			{

			}

			GamePacketManager.Send(playerId, data, qos);
		}

		public static unsafe void Multicast(NativeList<ushort> playerIdList, NativeStreamWriter data, QosType qos)
		{
			//記録
			if (_RecordablePacketTypes[data.GetUnsafePtr()[0]])
			{

			}

			GamePacketManager.Multicast(playerIdList, data, qos);
		}

		public static unsafe void Brodcast(NativeStreamWriter data, QosType qos, bool noChunk = false)
		{
			//記録
			if (_RecordablePacketTypes[data.GetUnsafePtr()[0]])
			{

			}

			GamePacketManager.Brodcast(data, qos, noChunk);
		}
	}
}