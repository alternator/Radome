#pragma warning disable 0649
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Networking.Transport;
#if !UNITY_2019_3_OR_NEWER
using Unity.Networking.Transport.LowLevel.Unsafe;
#endif
using UnityEngine;
using UnityEngine.Assertions;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
using System.Linq;
#endif

namespace ICKX.Radome
{
	public class RecordableIdentity : MonoBehaviour
	{

		public delegate void OnChangeAuthorEvent(RecordableIdentity identity, ushort author, bool hasAuthority);

		[SerializeField]
		private List<RecordableIdentity> m_childrenIdentity;

		[Disable]
		[SerializeField]
		internal ushort m_netId = 0;
		[Disable]
		[SerializeField]
		internal bool m_isSyncComplete;
		[Disable]
		[SerializeField]
		internal RecordableSceneIdentity m_sceneIdentity;

		public RecordableSceneIdentity sceneIdentity { get { return m_sceneIdentity; } }
		public IReadOnlyCollection<RecordableIdentity> childrenIdentity => m_childrenIdentity;

		//動的生成した場合は0
		public int sceneHash { get { return sceneIdentity ? sceneIdentity.sceneHash : 0; } }

		public ushort netId { get { return m_netId; } }

		public ushort author { get; private set; } = 0;

		public ushort gridId { get; private set; }

		public bool isSyncComplete { get { return m_isSyncComplete; } }

		public bool hasAuthority {
			get {
				return GamePacketManager.PlayerId == author;
			}
		}

		public event OnChangeAuthorEvent OnChangeAuthor = null;

		public Transform CacheTransform { get; private set; }
		public RecordableTransform CacheRecordableTransform { get; private set; }

		private List<IRecordableComponent> m_recordableComponentList;

		public IReadOnlyList<IRecordableComponent> RecordableComponentList => m_recordableComponentList;

		private bool _IsInit = false;

		internal void RegisterTransform(RecordableTransform trans)
		{
			CacheRecordableTransform = trans;
		}

		private void Awake()
		{
			Initialize();
		}

		public void Initialize()
		{
			if (_IsInit) return;
			_IsInit = true;
			CacheTransform = transform;

			var behaviours = GetComponents<IRecordableComponent>();
			m_recordableComponentList = new List<IRecordableComponent>(behaviours.Length);
			foreach (var component in behaviours)
			{
				while (component.ComponentIndex >= m_recordableComponentList.Count)
				{
					m_recordableComponentList.Add(null);
				}
				m_recordableComponentList[component.ComponentIndex] = component;
			}
			if (netId != 0)
			{
				if (GamePacketManager.PlayerId == 0)
				{
					GamePacketManager.OnRegisterPlayer += OnConnect;
					GamePacketManager.OnReconnectPlayer += OnConnect;
				}
			}
		}

		private void Start()
		{
			if (netId != 0)
			{
				if (GamePacketManager.PlayerId != 0 && isSyncComplete)
				{
					//プレイヤーIDが割り振った後ならすぐAuthorチェック
					RecordableIdentityManager.RequestSyncAuthor(this);
				}
			}
		}

		private void OnConnect(ushort playerId, ulong uniqueId)
		{
			if (playerId == GamePacketManager.PlayerId && isSyncComplete)
			{
				//プレイヤーIDが割り振られたときにAuthorチェック
				RecordableIdentityManager.RequestSyncAuthor(this);
			}
		}

		private void LateUpdate()
		{
			UpdateGridId();
		}

		//空間分割してパケットをフィルタするためのGridIDを計算する.
		private void UpdateGridId()
		{
			//あとで作る
		}

		internal void SetAuthor(ushort author)
		{
			this.author = author;
			OnChangeAuthor?.Invoke(this, author, hasAuthority);
		}

		internal void SyncComplete()
		{
			m_isSyncComplete = true;
		}

		public byte AddRecordableComponent(IRecordableComponent recordableComponent)
		{
			m_recordableComponentList.Add(recordableComponent);
			return (byte)m_recordableComponentList.Count;
		}

		public void SendRpc(ushort targetPlayerId, byte componentIndex, byte methodId, NativeStreamWriter rpcPacket, QosType qosType, bool important)
		{
			if (!isSyncComplete) return;

			using (var array = new NativeArray<byte>(rpcPacket.Length + 9, Allocator.Temp))
			{
				var writer = new NativeStreamWriter(array);
				CreateRpcPacket(writer, rpcPacket, componentIndex, methodId);
				GamePacketManager.Send(targetPlayerId, writer, qosType);

				//if (important) {
				//    GamePacketManager.Send (playerId, writer, qosType);
				//} else {
				//    GamePacketManager.Send (playerId, writer, qosType, gridId);
				//}
			}
		}

		public void BrodcastRpc(byte componentIndex, byte methodId, NativeStreamWriter rpcPacket, QosType qosType, bool important)
		{
			if (!isSyncComplete) return;

			using (var array = new NativeArray<byte>(rpcPacket.Length + 9, Allocator.Temp))
			{
				var writer = new NativeStreamWriter(array);
				CreateRpcPacket(writer, rpcPacket, componentIndex, methodId);
				GamePacketManager.Brodcast(writer, qosType);

				//if (important) {
				//    GamePacketManager.Brodcast (writer, qosType);
				//} else {
				//    GamePacketManager.Brodcast (writer, qosType, gridId);
				//}
			}
		}

		//TODO できればScene単位でパケットをまとめて、type(1byte) sceneHash(4byte)の5byteのデータを削減したい
		private void CreateRpcPacket(NativeStreamWriter writer, NativeStreamWriter rpcPacket, byte componentIndex, byte methodId)
		{
			unsafe
			{
				byte* dataPtr = rpcPacket.GetUnsafePtr();
				writer.WriteByte((byte)BuiltInPacket.Type.BehaviourRpc);
				writer.WriteInt(sceneHash);
				writer.WriteUShort(netId);
				writer.WriteByte(componentIndex);
				writer.WriteByte(methodId);
				writer.WriteBytes(dataPtr, rpcPacket.Length);
			}
		}

		internal void OnRecieveSyncTransformPacket(ushort senderPlayerId, NativeStreamReader packet)
		{
			if (!isSyncComplete) return;

			if (CacheRecordableTransform)
			{
				CacheRecordableTransform.OnRecieveSyncTransformPacket(senderPlayerId, packet);
			}
		}


		internal void OnRecieveRpcPacket(ushort senderPlayerId, NativeStreamReader rpcPacket)
		{
			if (!isSyncComplete) return;

			byte componentIndex = rpcPacket.ReadByte();
			byte methodId = rpcPacket.ReadByte();

			if (componentIndex < m_recordableComponentList.Count)
			{
				var behaviour = m_recordableComponentList[componentIndex];
				if (behaviour == null) return;
				behaviour.OnRecieveRpcPacket(senderPlayerId, methodId, rpcPacket);
			}
		}

		/// <summary>
		/// 
		/// </summary>
		/// <param name="syncPacket"></param>
		internal void CollectSyncVarPacket(ref NativeStreamWriter syncPacket)
		{
			//あとで作る
		}

		internal void ApplySyncVarPacket(ref NativeStreamReader syncPacket)
		{
			//あとで作る
		}

#if UNITY_EDITOR
		[CustomEditor(typeof(RecordableIdentity)), CanEditMultipleObjects]
		public class RecordableIdentityEditor : Editor
		{

			public override void OnInspectorGUI()
			{
				base.OnInspectorGUI();

				if (GUILayout.Button("Register Children"))
				{
					var identity = target as RecordableIdentity;
					identity.m_childrenIdentity = identity.GetComponentsInChildren<RecordableIdentity>()
						.Where(i => identity != i)
						.ToList();
				}

				if (Application.isPlaying && targets.Length == 1)
				{
					var identity = target as RecordableIdentity;

					EditorGUILayout.IntField("author", identity.author);
					EditorGUILayout.Toggle("HasAuthority", identity.hasAuthority);
					EditorGUILayout.Toggle("IsSyncComplete", identity.isSyncComplete);
				}
			}
		}
#endif
	}
}
