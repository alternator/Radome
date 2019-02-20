using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Networking.Transport;
using Unity.Collections;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace ICKX.Radome {

	public class RecordableGroupIdentity : MonoBehaviour {
		[HideInInspector]
		[SerializeField]
		protected int m_groupHash;

		[HideInInspector]
		[SerializeField]
		protected List<RecordableIdentity> m_identityList;

		public IReadOnlyList<RecordableIdentity> identityList { get { return m_identityList; } }

		internal RecordableIdentity GetIdentityInGroup (ushort netId) {
			if (netId >= m_identityList.Count) return null;
			return m_identityList[netId];
		}

		internal void RequestChangeAuthorInGroup (ushort netId, ushort author) {
			if (netId >= m_identityList.Count) {
				Debug.LogError ($"netId={netId} is too large");
				return;
			}

			if (GamePacketManager.IsLeader) {
				var identity = GetIdentityInGroup (netId);
				if(identity) {
					identity.SetAuthor (author);
				} else {
					Debug.LogError ($"netId={netId} is not found in group");
				}
			} else {
				SendPacketChangeAuthor (netId, author);
			}
		}

		internal void RequestChangeAuthorInGroup (RecordableIdentity identity, ushort author) {
			if (identity == null) {
				Debug.LogError ($"identity={identity} is null");
				return;
			}
			if (!m_identityList.Contains (identity)) {
				Debug.LogError ($"identity={identity} is not found in group");
				return;
			}
			if (GamePacketManager.IsLeader) {
				identity.SetAuthor (author);
			} else {
				SendPacketChangeAuthor (identity.netId, author);
			}
		}

		private void SendPacketChangeAuthor (ushort netId, ushort author) {
			using (var packet = new DataStreamWriter (9, Allocator.Temp)) {
				packet.Write ((byte)BuiltInPacket.Type.ChangeAuthor);
				packet.Write (m_groupHash);
				packet.Write (netId);
				packet.Write (author);
				GamePacketManager.Send (0, packet, QosType.Reliable);
			}
		}

		internal void RecieveChangeAuthorInGroup (ushort netId, ushort author) {
			if (GamePacketManager.IsLeader) {
				//Hostではauthorの整合性を確認
				if (netId < m_identityList.Count) {
					m_identityList[netId].SetAuthor (author);
					//Clientに通達する
					using (var packet = new DataStreamWriter (9, Allocator.Temp)) {
						packet.Write ((byte)BuiltInPacket.Type.ChangeAuthor);
						packet.Write (m_groupHash);
						packet.Write (netId);
						packet.Write (author);
						GamePacketManager.Brodcast (packet, QosType.Reliable);
					}
				}
			} else {
				//確認されたauthorの変更を反映
				if (netId < m_identityList.Count) {
					m_identityList[netId].SetAuthor (author);
				}
			}
		}

		internal void RecieveSyncTransformInGroup (ushort senderPlayerId, ushort netId, ref DataStreamReader recievePacket, ref DataStreamReader.Context ctx) {
			if (netId < m_identityList.Count) {
				var identity = m_identityList[netId];
				if (identity != null) {
					identity.OnRecieveSyncTransformPacket (senderPlayerId, ref recievePacket, ref ctx);
				}
			}
		}

		internal void RecieveBehaviourRpcInGroup (ushort senderPlayerId, ushort netId, ref DataStreamReader recievePacket, ref DataStreamReader.Context ctx) {
			if (netId < m_identityList.Count) {
				var identity = m_identityList[netId];
				if (identity != null) {
					identity.OnRecieveRpcPacket (senderPlayerId, ref recievePacket, ref ctx);
				}
			}
		}
	}

	[ExecuteInEditMode]
	public class RecordableSceneIdentity : RecordableGroupIdentity {

		private List<RecordableIdentity> m_despawnIdentityList;

		public int sceneHash { get { return m_groupHash; } private set { m_groupHash = value; } }

		public IReadOnlyList<RecordableIdentity> despawnIdentityList { get { return m_despawnIdentityList; } }

#if UNITY_EDITOR
		private void Reset () {
			ResetSceneHash ();
		}
#endif

		private void Awake () {
#if UNITY_EDITOR
			ResetSceneHash ();
			ChaeckDuplicationNetIdInScene ();
#endif
			RecordableIdentityManager.AddRecordableSceneIdentity (sceneHash, this);
		}

		private void OnDestroy () {
			RecordableIdentityManager.RemoveRecordableSceneIdentity (sceneHash);
		}

		public void ChaeckDuplicationNetIdInScene () {
			var list = FindObjectsOfType<RecordableIdentity> ()
				.Where (i=>i.gameObject.scene == gameObject.scene)
				.ToList();

			foreach (var identity in list) {
				if(!m_identityList.Contains (identity)) {
					Debug.LogError ($"{identity.name} は未登録のRecordableIdentityです ", identity);
				}
			}
		}

		/// <summary>
		/// 
		/// </summary>
		public RecordableIdentity GetIdentityInScene (ushort netId) {
			return GetIdentityInGroup (netId);
		}

		/// <summary>
		/// Hostに問い合わせて問題なければAuthorを変更する
		/// </summary>
		public void RequestChangeAuthorInScene (ushort netId, ushort author) {
			RequestChangeAuthorInGroup (netId, author);
		}

		/// <summary>
		/// Hostに問い合わせて問題なければAuthorを変更する
		/// </summary>
		public void RequestChangeAuthorInScene (RecordableIdentity identity, ushort author) {
			RequestChangeAuthorInGroup (identity, author);
		}

#if UNITY_EDITOR
		private void ResetSceneHash () {

			sceneHash = gameObject.scene.path.GetHashCode();
		}

		private void ResetIdentitys () {
			m_identityList = Resources.FindObjectsOfTypeAll<RecordableIdentity> ()
				.Where (i => i.gameObject.scene == gameObject.scene)
				.ToList ();
			m_identityList.Insert (0, null);

			for (ushort i = 1; i < m_identityList.Count; i++) {
				var sobj = new SerializedObject (m_identityList[i]);
				sobj.Update ();
				sobj.FindProperty ("m_netId").intValue = i;
				sobj.FindProperty ("m_sceneIdentity").objectReferenceValue = this;
				sobj.FindProperty ("m_isSyncComplete").boolValue = true;
				sobj.ApplyModifiedPropertiesWithoutUndo ();
			}
			EditorSceneManager.MarkSceneDirty (gameObject.scene);
		}

		[MenuItem ("ICKX/Network/AssignNetIDAll")]
		private static void AssignNetIDAll () {
			foreach (var sceneIdentity in FindObjectsOfType<RecordableSceneIdentity>()) {
				sceneIdentity.ResetIdentitys ();
			}
		}

		[CustomEditor (typeof (RecordableSceneIdentity))]
		public class RecordableSceneIdentityEditor : Editor {

			public override void OnInspectorGUI () {
				if (GUILayout.Button ("Assign NetID", EditorStyles.miniButton)) {
					var sceneIdentity = target as RecordableSceneIdentity;
					sceneIdentity.ResetIdentitys ();
				}

				EditorGUILayout.IntField ("SceneHash", serializedObject.FindProperty("m_groupHash").intValue);
				EditorGUILayout.IntField ("Identity Count", serializedObject.FindProperty ("m_identityList").arraySize-1);
			}
		}
#endif
	}
}