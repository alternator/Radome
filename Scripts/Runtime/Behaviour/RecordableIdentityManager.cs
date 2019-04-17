using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Networking.Transport;
using UnityEngine;
using UnityEngine.Assertions;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace ICKX.Radome {

	public class RecordableIdentityManager : RecordableGroupIdentity {

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

		private static List<System.Action<ushort>> uncheckReserveNetIdCallbacks;

		private static Dictionary<int, RecordableSceneIdentity> m_recordableSceneIdentitTable = new Dictionary<int, RecordableSceneIdentity>();

		public static IReadOnlyDictionary<int, RecordableSceneIdentity> recordableSceneIdentityTable {
			get { return m_recordableSceneIdentitTable; }
		}

		public static uint progressTimeSinceStartup {
			get { return GamePacketManager.progressTimeSinceStartup; }
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
			uncheckReserveNetIdCallbacks = new List<System.Action<ushort>> (4);

			if (m_identityList == null) {
				m_identityList = new List<RecordableIdentity> ();
			}else {
				m_identityList.Clear ();
			}
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

		/// <summary>
		/// Hostに問い合わせて重複しないNetIDを取得する.
		/// </summary>
		public static void ReserveNetId (System.Action<ushort> onReserveNetId) {
			if (GamePacketManager.IsLeader)
			{
				ushort count = (ushort)Instance.m_identityList.Count;
				Instance.m_identityList.Add(null);
				onReserveNetId (count);
			} else {
				uncheckReserveNetIdCallbacks.Add (onReserveNetId);
				using (var packet = new DataStreamWriter (1, Allocator.Temp)) {
					packet.Write ((byte)BuiltInPacket.Type.ReserveNetId);
					GamePacketManager.Send (0, packet, QosType.Reliable);
				}
			}
		}

		/// <summary>
		/// ReserveNetIdで確保したNetIDでidentityを登録する
		/// このメソッド単体でSpawn処理は行わないので、それぞれのclientでIdentityを生成した後で実行すること
		/// </summary>
		public static void RegisterIdentity (RecordableIdentity identity, ushort netId, ushort author) {
			if (identity == null) return;

			while (netId >= Instance.m_identityList.Count) Instance.m_identityList.Add (null);
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

		private static void OnRecievePacket (ushort senderPlayerId, byte type, DataStreamReader recievePacket, DataStreamReader.Context ctx) {
			switch ((BuiltInPacket.Type)type) {
				case BuiltInPacket.Type.ReserveNetId:
					RecieveReserveNetId (senderPlayerId, ref recievePacket, ref ctx);
					break;
				case BuiltInPacket.Type.ChangeAuthor:
					int sceneHash = recievePacket.ReadInt (ref ctx);
					ushort netId = recievePacket.ReadUShort (ref ctx);
					ushort author = recievePacket.ReadUShort (ref ctx);
					RecieveChangeAuthor (sceneHash, netId, author);
					break;
				case BuiltInPacket.Type.SyncAuthor:
					int sceneHash2 = recievePacket.ReadInt (ref ctx);
					ushort netId2 = recievePacket.ReadUShort (ref ctx);
					RecieveSyncAuthor (sceneHash2, netId2);
					break;
				case BuiltInPacket.Type.SyncTransform:
					RecieveSyncTransform (senderPlayerId, ref recievePacket, ref ctx);
					break;
				case BuiltInPacket.Type.BehaviourRpc:
					RecieveBehaviourRpc (senderPlayerId, ref recievePacket, ref ctx);
					break;
			}
		}

		private static void RecieveReserveNetId (ushort senderPlayerId, ref DataStreamReader recievePacket, ref DataStreamReader.Context ctx) {

			if (GamePacketManager.IsLeader) {
				//HostではNetIDの整合性を確認
				ushort reserveNetId = (ushort)Instance.m_identityList.Count;
				Instance.m_identityList.Add (null);

				//Clientに通達する
				using (var packet = new DataStreamWriter (3, Allocator.Temp)) {
					packet.Write ((byte)BuiltInPacket.Type.ReserveNetId);
					packet.Write (reserveNetId);
					GamePacketManager.Send (senderPlayerId, packet, QosType.Reliable);
				}
			} else {
				//確認されたauthorの変更を反映
				ushort reserveNetId = recievePacket.ReadUShort (ref ctx);
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

		private static void RecieveSyncTransform (ushort senderPlayerId, ref DataStreamReader recievePacket, ref DataStreamReader.Context ctx) {
			int sceneHash = recievePacket.ReadInt (ref ctx);
			ushort netId = recievePacket.ReadUShort (ref ctx);

			if (sceneHash == 0) {
				Instance.RecieveSyncTransformInGroup (senderPlayerId, netId, ref recievePacket, ref ctx);
			} else {
				RecordableSceneIdentity sceneIdentity;
				if (m_recordableSceneIdentitTable.TryGetValue (sceneHash, out sceneIdentity)) {
					sceneIdentity.RecieveSyncTransformInGroup (senderPlayerId, netId, ref recievePacket, ref ctx);
				} else {
					Debug.LogError ($"{sceneHash} is not found in recordableSceneIdentitTable");
				}
			}
		}

		private static void RecieveBehaviourRpc (ushort senderPlayerId, ref DataStreamReader recievePacket, ref DataStreamReader.Context ctx) {
			int sceneHash = recievePacket.ReadInt (ref ctx);
			ushort netId = recievePacket.ReadUShort (ref ctx);

			if (sceneHash == 0) {
				Instance.RecieveBehaviourRpcInGroup (senderPlayerId, netId, ref recievePacket, ref ctx);
			} else {
				RecordableSceneIdentity sceneIdentity;
				if (m_recordableSceneIdentitTable.TryGetValue (sceneHash, out sceneIdentity)) {
					sceneIdentity.RecieveBehaviourRpcInGroup (senderPlayerId, netId, ref recievePacket, ref ctx);
				} else {
					Debug.LogError ($"{sceneHash} is not found in recordableSceneIdentitTable");
				}
			}
		}
	}
}