using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Networking.Transport;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace ICKX.Radome {

	public interface IRecordableComponent
	{
		bool HasAuthor { get; }
		byte ComponentIndex { get; }

		RecordableIdentity CacheRecordableIdentity { get; }

		/// <summary>
		/// 初期化
		/// </summary>
		void Initialize();

		/// <summary>
		/// [内部処理専用] componentIndexをセットする
		/// </summary>
		void SetComponentIndex(byte index);

		/// <summary>
		/// Rpcパケットを受信する
		/// </summary>
		void OnRecieveRpcPacket(ushort senderPlayerId, byte methodId, DataStreamReader rpcPacket, DataStreamReader.Context ctx);

		/// <summary>
		/// 変数を同期するパケットに書き込む
		/// </summary>
		void WriteSyncVarPacket(ref DataStreamWriter syncPacket);

		/// <summary>
		/// 変数を同期するパケットを読み込み反映する
		/// </summary>
		void ReadSyncVarPacket(ref DataStreamReader syncPacket, ref DataStreamReader.Context ctx);
	}

	[RequireComponent (typeof (RecordableIdentity))]
    public abstract class RecordableBehaviour : MonoBehaviour, IRecordableComponent
	{
        [Disable]
        [SerializeField]
        private byte m_ComponentIndex = 255;

        public bool HasAuthor { get { return CacheRecordableIdentity.hasAuthority; } }
        public byte ComponentIndex { get { return m_ComponentIndex; } }

		protected bool _IsInitialized = false;

        public RecordableIdentity CacheRecordableIdentity { get; private set; }

#if UNITY_EDITOR
        protected void Reset () {
            ResetComponentIndex ();
        }
#endif

        protected void Awake () {
            Initialize ();
        }

        public virtual void Initialize () {
            if (_IsInitialized) return;
            _IsInitialized = true;

            CacheRecordableIdentity = GetComponent<RecordableIdentity> ();
			CacheRecordableIdentity.Initialize();
			if (m_ComponentIndex == 255) {
                m_ComponentIndex = CacheRecordableIdentity.AddRecordableComponent (this);
            }
        }

		public void SetComponentIndex (byte index)
		{
			m_ComponentIndex = index;
		}

#if UNITY_EDITOR
		private void ResetComponentIndex () {
            var components = GetComponents<IRecordableComponent> ();
            for (byte i = 0; i < components.Length; i++) {
                components[i].SetComponentIndex(i);
				var c = components[i] as MonoBehaviour;
				EditorUtility.SetDirty (c);
            }
            if (!Application.isPlaying) {
                EditorSceneManager.MarkSceneDirty (gameObject.scene);
            }
        }
#endif

        /// <summary>
        /// 指定したPlayerIDのリモートインスタンスにパケットを送信
        /// </summary>
        protected void SendRpc (ushort targetPlayerId, byte methodId, DataStreamWriter rpcPacket, QosType qosType, bool important = true) {
            CacheRecordableIdentity.SendRpc (targetPlayerId, ComponentIndex, methodId, rpcPacket, qosType, important);
        }

        /// <summary>
        /// 全クライアントのリモートインスタンスにパケットを送信
        /// </summary>
        protected void BrodcastRpc (byte methodId, DataStreamWriter rpcPacket, QosType qosType, bool important = true) {
            CacheRecordableIdentity.BrodcastRpc (ComponentIndex, methodId, rpcPacket, qosType, important);
        }

        /// <summary>
        /// Rpcパケットを受信する
        /// </summary>
        public abstract void OnRecieveRpcPacket (ushort senderPlayerId, byte methodId, DataStreamReader rpcPacket, DataStreamReader.Context ctx);

        /// <summary>
        /// 変数を同期するパケットに書き込む
        /// </summary>
        public abstract void WriteSyncVarPacket (ref DataStreamWriter syncPacket);

        /// <summary>
        /// 変数を同期するパケットを読み込み反映する
        /// </summary>
        public abstract void ReadSyncVarPacket (ref DataStreamReader syncPacket, ref DataStreamReader.Context ctx);
    }
}
