using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Networking.Transport;
using UnityEngine;
using UnityEngine.Assertions;
using System.Threading.Tasks;
using Unity.Collections.LowLevel.Unsafe;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace ICKX.Radome {

	public class RecordableIdentityManager : RecordableGroupIdentity {

		public enum SpawnState
		{
			Empty,
			Reserve,
			Spawned,
		}

		private static RecordableIdentityManager s_instance = null;
		public static RecordableIdentityManager Instance {
			get {
				if (s_instance == null) {
					s_instance = FindObjectOfType<RecordableIdentityManager> ();
					if (s_instance == null) {
						s_instance = new GameObject (nameof (RecordableIdentityManager)).AddComponent<RecordableIdentityManager> ();
					}
					if (!s_instance.isInitialized) {
						s_instance.Initialize ();
					}
				}
				return s_instance;
			}
		}
		
		public delegate void OnSpawnIdentityEvent(RecordableIdentity spawnObj);
		public delegate void OnDespawnIdentityEvent(RecordableIdentity despawnObj);

		private static ushort m_DespawnedNetId;
		private static List<SpawnState> m_SpawnStateList;

		private static List<System.Action<ushort>> uncheckReserveNetIdCallbacks;

		private static Dictionary<int, RecordableSceneIdentity> m_recordableSceneIdentitTable = new Dictionary<int, RecordableSceneIdentity>();

		public static event OnSpawnIdentityEvent OnSpawnIdentity = null;
		public static event OnDespawnIdentityEvent OnDespawnIdentity = null;


		public static IReadOnlyDictionary<int, RecordableSceneIdentity> recordableSceneIdentityTable {
			get { return m_recordableSceneIdentitTable; }
		}

		public static uint progressTimeSinceStartup {
			get { return GamePacketManager.ProgressTimeSinceStartup; }
		}

		protected void Awake () {
			if (s_instance == null) {
				s_instance = this as RecordableIdentityManager;
			} else {
				if (s_instance != this) {
					Destroy (this);
				}
			}

			if (!s_instance.isInitialized) {
				Initialize ();
			}
		}

		protected bool isInitialized = false;

		public void Initialize () {
			if (isInitialized) return;
			isInitialized = true;
			DontDestroyOnLoad (s_instance.gameObject);
			m_SpawnStateList = new List<SpawnState>();
			uncheckReserveNetIdCallbacks = new List<System.Action<ushort>> (4);

			if (m_identityList == null) {
				m_identityList = new List<RecordableIdentity> ();
			}else {
				m_identityList.Clear ();
			}

			m_SpawnStateList.Add( SpawnState.Reserve);
			m_identityList.Add(null);

			m_groupHash = 0;

			GamePacketManager.OnRecievePacket += OnRecievePacket;
		}

		private void OnDestroy () {
			GamePacketManager.OnRecievePacket -= OnRecievePacket;
		}

		public static void AddRecordableSceneIdentity (int sceneHash, RecordableSceneIdentity sceneIdentity) {
			m_recordableSceneIdentitTable[sceneHash] = sceneIdentity;
		}

		public static void RemoveRecordableSceneIdentity (int sceneHash) {
			m_recordableSceneIdentitTable.Remove (sceneHash);
		}

		/// <summary>
		/// RecordableIdentityを探す sceneHash=0なら動的生成したもの
		/// </summary>
		public static RecordableIdentity GetIdentity (int sceneHash, ushort netId) {
			if (sceneHash == 0) {
				return Instance.GetIdentityInGroup (netId);
			} else {
				if (m_recordableSceneIdentitTable.TryGetValue (sceneHash, out RecordableSceneIdentity sceneIdentity)) {
					return sceneIdentity.GetIdentityInGroup (netId);
				} else {
					Debug.LogError ($"sceneHash = {sceneHash} is not found");
					return null;
				}
			}
		}

		public delegate void CustomInstantiateDelegate(NativeArray<byte> spawnInfo, System.Action<RecordableIdentity, bool> callback);

		public static event CustomInstantiateDelegate CustomSpawnMethod;

		/// <summary>
		/// 
		/// </summary>
		/// <param name="spawnInfo">Allocator.Temp以外で確保する(処理後にメソッド内でDisposeする)</param>
		/// <param name="hasAuthor"></param>
		/// <param name="callback"></param>
		public static unsafe void RpcSpawn(NativeArray<byte> spawnInfo, bool hasAuthor, System.Action<RecordableIdentity, bool> callback)
		{
			if (CustomSpawnMethod == null) throw new System.InvalidOperationException("CustomSpawnMethodを登録してください");

			if(GamePacketManager.IsLeader)
			{
				//クライアントに通知
				CustomSpawnMethod(spawnInfo, (RecordableIdentity identity, bool success) =>
				{
					var netIdList = new NativeArray<ushort>(1 + identity.childrenIdentity.Count, Allocator.Temp);

					//クライアントに送信



					LocalSpawn(identity, netIdList, 0, callback);
				});
			}
			else
			{
				//サーバーにリクエストを送り、返答後に生成

			}

			ReserveNetId((ushort netId) =>
			{
				using (var array = new NativeArray<byte>(spawnInfo.Length + 6, Allocator.Temp))
				{
					var writer = new NativeStreamWriter(array);
					writer.WriteByte((byte)BuiltInPacket.Type.SpawnIdentity);
					writer.WriteByte((byte)(hasAuthor ? 1 : 0));
					writer.WriteUShort(netId);
					writer.WriteUShort((ushort)spawnInfo.Length);
					writer.WriteBytes((byte*)spawnInfo.GetUnsafeReadOnlyPtr(), spawnInfo.Length);
					RecordablePacketManager.Brodcast(writer, QosType.Reliable);
				}
			});
		}

		private static unsafe void RecieveSpawnRequest (NativeArray<byte> spawnInfo, ushort netId, ushort author)
		{
			//
			CustomSpawnMethod(spawnInfo, (RecordableIdentity identity, bool success) =>
			{
				var needNetIdCount = 1 + identity.childrenIdentity.Count;


				//クライアントに送信



				//LocalSpawn(identity, );
			});
		}

		private static unsafe void LocalSpawn (RecordableIdentity identity, NativeArray<ushort> netIds, ushort author, System.Action<RecordableIdentity, bool> callback)
		{
			//Debug.Log($"LocalSpawn {identity} {netId} {author}");
			//RegisterIdentity(identity, netId, author);

			//callback?.Invoke(identity, success);
			//spawnInfo.Dispose();

			//OnSpawnIdentity?.Invoke(identity);
		}

		public static void DespawnRequest(ushort netId, System.Action<bool> result)
		{
			if (netId >= m_SpawnStateList.Count) result?.Invoke(false);
			if (m_SpawnStateList[netId] != SpawnState.Spawned) result?.Invoke(false);

			var identity = Instance.m_identityList[netId];
			if (identity == null) return;

			//自分の管理権限がない場合はDespawnできない
			if (!GamePacketManager.IsLeader && identity.author != GamePacketManager.PlayerId) return;

			using (var array = new NativeArray<byte>(4, Allocator.Temp))
			{
				var writer = new NativeStreamWriter(array);
				writer.WriteByte((byte)BuiltInPacket.Type.DespawnIdentity);
				writer.WriteUShort(netId);
				RecordablePacketManager.Brodcast(writer, QosType.Reliable);
			}
			LocalDespawn(netId, result);
		}

		private static void LocalDespawn (ushort netId, System.Action<bool> result)
		{
			if (netId >= m_SpawnStateList.Count) result?.Invoke(false);
			if (m_SpawnStateList[netId] != SpawnState.Spawned) result?.Invoke(false);

			m_SpawnStateList[netId] = SpawnState.Empty;
			var identity = Instance.m_identityList[netId];

			if (identity != null)
			{
				Instance.m_identityList[netId] = null;
				OnDespawnIdentity?.Invoke(identity);
				Destroy(identity);
			}

			m_DespawnedNetId = netId;   //次の検索を早くするため 
		}
		
		/// <summary>
		/// Hostに問い合わせて重複しないNetIDを取得する.
		/// </summary>
		public static void ReserveNetId (System.Action<ushort> onReserveNetId) {
			if (GamePacketManager.IsLeader)
			{
				onReserveNetId (ReserveNetIdForHost());
			} else {
				uncheckReserveNetIdCallbacks.Add (onReserveNetId);

				using (var array = new NativeArray<byte>(1, Allocator.Temp))
				{
					var packet = new NativeStreamWriter(array);
					packet.WriteByte ((byte)BuiltInPacket.Type.ReserveNetId);
					GamePacketManager.Send (0, packet, QosType.Reliable);
				}
			}
		}

		public static ushort ReserveNetIdForHost ()
		{
			if (GamePacketManager.IsLeader)
			{
				for (ushort i = 0; i < m_SpawnStateList.Count; i++)
				{
					if (m_SpawnStateList[i] == SpawnState.Empty)
					{
						m_SpawnStateList[i] = SpawnState.Reserve;
						return i;
					}
				}

				//最後に追加
				m_SpawnStateList.Add( SpawnState.Reserve);
				Instance.m_identityList.Add(null);

				return (ushort)(m_SpawnStateList.Count - 1);
			}
			else
			{
				throw new System.NotImplementedException();
			}
		}

		/// <summary>
		/// ReserveNetIdで確保したNetIDでidentityを登録する
		/// このメソッド単体でSpawn処理は行わないので、それぞれのclientでIdentityを生成した後で実行すること
		/// </summary>
		public static void RegisterIdentity (RecordableIdentity identity, ushort netId, ushort author) {
			if (identity == null) return;
			if (netId < m_SpawnStateList.Count && m_SpawnStateList[netId] == SpawnState.Spawned) return;

			while (m_SpawnStateList.Count <= netId) m_SpawnStateList.Add( SpawnState.Empty);
			while (Instance.m_identityList.Count <= netId) Instance.m_identityList.Add(null);

			m_SpawnStateList[netId] = SpawnState.Spawned;
			Instance.m_identityList[netId] = identity;
			identity.m_netId = netId;
			identity.SetAuthor (author);
			identity.SyncComplete ();
		}

		/// <summary>
		/// Hostに問い合わせて問題なければAuthorを変更する
		/// </summary>
		public static void RequestChangeAuthor (int sceneHash, ushort netId, ushort author) {
			if (sceneHash == 0) {
				Instance.RequestChangeAuthorInGroup (netId, author);
			} else {
				if (m_recordableSceneIdentitTable.TryGetValue (sceneHash, out RecordableSceneIdentity sceneIdentity)) {
					sceneIdentity.RequestChangeAuthorInGroup (netId, author);
				} else {
					Debug.LogError ($"sceneHash = {sceneHash} is not found");
					return;
				}
			}
		}

		/// <summary>
		/// Hostに問い合わせて問題なければAuthorを変更する
		/// </summary>
		public static void RequestChangeAuthor (RecordableIdentity identity, ushort author) {
			if (identity == null) return;
			RequestChangeAuthor (identity.sceneHash, identity.netId, author);
		}

		/// <summary>
		/// HostのAuthor情報でIDを同期してもらう
		/// </summary>
		public static void RequestSyncAuthor (int sceneHash, ushort netId) {
			if (sceneHash == 0) {
				Instance.RequestSyncAuthorInGroup (netId);
			} else {
				if (m_recordableSceneIdentitTable.TryGetValue (sceneHash, out RecordableSceneIdentity sceneIdentity)) {
					sceneIdentity.RequestSyncAuthorInGroup (netId);
				} else {
					Debug.LogError ($"sceneHash = {sceneHash} is not found");
					return;
				}
			}
		}

		/// <summary>
		/// HostのAuthor情報でIDを同期してもらう
		/// </summary>
		public static void RequestSyncAuthor (RecordableIdentity identity) {
			if (identity == null) return;
			RequestSyncAuthor (identity.sceneHash, identity.netId);
		}

		private static unsafe void OnRecievePacket (ushort senderPlayerId, ulong senderUniqueId, byte type, NativeStreamReader recievePacket) {
			ushort netId;
			ushort author;
			switch ((BuiltInPacket.Type)type) {
				case BuiltInPacket.Type.ReserveNetId:
					RecieveReserveNetId (senderPlayerId, recievePacket);
					break;
				case BuiltInPacket.Type.ChangeAuthor:
					int sceneHash = recievePacket.ReadInt ();
					netId = recievePacket.ReadUShort ();
					author = recievePacket.ReadUShort ();
					RecieveChangeAuthor (sceneHash, netId, author);
					break;
				case BuiltInPacket.Type.SyncAuthor:
					int sceneHash2 = recievePacket.ReadInt ();
					netId = recievePacket.ReadUShort ();
					RecieveSyncAuthor (sceneHash2, netId);
					break;
				case BuiltInPacket.Type.SyncTransform:
					RecieveSyncTransform (senderPlayerId, recievePacket);
					break;
				case BuiltInPacket.Type.BehaviourRpc:
					RecieveBehaviourRpc (senderPlayerId, recievePacket);
					break;
				case BuiltInPacket.Type.SpawnIdentity:
					author = recievePacket.ReadByte() == 1 ? senderPlayerId : (ushort)0;
					netId = recievePacket.ReadUShort();
					ushort dataLen = recievePacket.ReadUShort();
					NativeArray<byte> spawnInfo = new NativeArray<byte>(dataLen, Allocator.Persistent);
					recievePacket.ReadBytes((byte*)spawnInfo.GetUnsafePtr(), dataLen);
					//LocalSpawn(spawnInfo, netId, author, null);
					break;
				case BuiltInPacket.Type.DespawnIdentity:
					netId = recievePacket.ReadUShort();
					if (netId >= Instance.m_identityList.Count) return;
					var identity = Instance.m_identityList[netId];

					//サーバーか所有権持っている人しかdespawnできない
					if(identity != null && (senderPlayerId == 0 || identity.author == senderPlayerId))
					{
						LocalDespawn(netId, null);
					}
					break;
			}
		}

		private static void RecieveReserveNetId (ushort senderPlayerId, NativeStreamReader recievePacket) {

			if (GamePacketManager.IsLeader) {
				//HostではNetIDの整合性を確認
				ushort reserveNetId = (ushort)Instance.m_identityList.Count;
				Instance.m_identityList.Add (null);

				//Clientに通達する
				using (var array = new NativeArray<byte> (3, Allocator.Temp)) {
					var packet = new NativeStreamWriter(array);
					packet.WriteByte ((byte)BuiltInPacket.Type.ReserveNetId);
					packet.WriteUShort (reserveNetId);
					GamePacketManager.Send (senderPlayerId, packet, QosType.Reliable);
				}
			} else {
				//確認されたauthorの変更を反映
				ushort reserveNetId = recievePacket.ReadUShort ();
				if (uncheckReserveNetIdCallbacks.Count > 0) {
					uncheckReserveNetIdCallbacks[0] (reserveNetId);
					uncheckReserveNetIdCallbacks.RemoveAt (0);
				} else {
					Debug.LogError ("uncheckReserveNetIdCallbacks is 0");
				}
			}
		}

		private static void RecieveChangeAuthor (int sceneHash, ushort netId, ushort author) {
			if (sceneHash == 0) {
				Instance.RecieveChangeAuthorInGroup (netId, author);
			} else {
				RecordableSceneIdentity sceneIdentity;
				if (m_recordableSceneIdentitTable.TryGetValue (sceneHash, out sceneIdentity)) {
					sceneIdentity.RecieveChangeAuthorInGroup (netId, author);
				} else {
					Debug.LogError ($"{sceneHash} is not found in recordableSceneIdentitTable");
				}
			}
		}
		
		private static void RecieveSyncAuthor (int sceneHash, ushort netId) {
			if (sceneHash == 0) {
				Instance.RecieveSyncAuthorInGroup (netId);
			} else {
				RecordableSceneIdentity sceneIdentity;
				if (m_recordableSceneIdentitTable.TryGetValue (sceneHash, out sceneIdentity)) {
					sceneIdentity.RecieveSyncAuthorInGroup (netId);
				} else {
					Debug.LogError ($"{sceneHash} is not found in recordableSceneIdentitTable");
				}
			}
		}

		private static void RecieveSyncTransform (ushort senderPlayerId, NativeStreamReader recievePacket) {
			int sceneHash = recievePacket.ReadInt ();
			ushort netId = recievePacket.ReadUShort ();

			if (sceneHash == 0) {
				Instance.RecieveSyncTransformInGroup (senderPlayerId, netId, recievePacket);
			} else {
				RecordableSceneIdentity sceneIdentity;
				if (m_recordableSceneIdentitTable.TryGetValue (sceneHash, out sceneIdentity)) {
					sceneIdentity.RecieveSyncTransformInGroup (senderPlayerId, netId, recievePacket);
				} else {
					Debug.LogError ($"{sceneHash} is not found in recordableSceneIdentitTable");
				}
			}
		}

		private static void RecieveBehaviourRpc (ushort senderPlayerId, NativeStreamReader recievePacket) {
			int sceneHash = recievePacket.ReadInt ();
			ushort netId = recievePacket.ReadUShort ();

			if (sceneHash == 0) {
				Instance.RecieveBehaviourRpcInGroup (senderPlayerId, netId, recievePacket);
			} else {
				RecordableSceneIdentity sceneIdentity;
				if (m_recordableSceneIdentitTable.TryGetValue (sceneHash, out sceneIdentity)) {
					sceneIdentity.RecieveBehaviourRpcInGroup (senderPlayerId, netId, recievePacket);
				} else {
					Debug.LogError ($"{sceneHash} is not found in recordableSceneIdentitTable");
				}
			}
		}
	}
}