using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Networking.Transport;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace ICKX.Radome {

    [RequireComponent (typeof (RecordableIdentity))]
    public abstract class RecordableBehaviour : MonoBehaviour {

        [Disable]
        [SerializeField]
        private byte m_componentIndex = 255;

        public bool hasAuthor { get { return cacheRecordableIdentity.hasAuthority; } }
        public byte componentIndex { get { return m_componentIndex; } internal set { m_componentIndex = value; } }

        private bool isInitialized = false;

        public RecordableIdentity cacheRecordableIdentity { get; private set; }

#if UNITY_EDITOR
        protected void Reset () {
            ResetComponentIndex ();
        }
#endif

        protected void Awake () {
            Initialize ();
        }

        internal virtual void Initialize () {
            if (isInitialized) return;
            isInitialized = true;

            cacheRecordableIdentity = GetComponent<RecordableIdentity> ();
            if (m_componentIndex == 255) {
                m_componentIndex = cacheRecordableIdentity.AddRecordableBehaviour (this);
            }
        }

#if UNITY_EDITOR
        private void ResetComponentIndex () {
            var components = GetComponents<RecordableBehaviour> ();
            for (byte i = 0; i < components.Length; i++) {
                components[i].m_componentIndex = i;
                EditorUtility.SetDirty (components[i]);
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
            cacheRecordableIdentity.SendRpc (targetPlayerId, componentIndex, methodId, rpcPacket, qosType, important);
        }

        /// <summary>
        /// 全クライアントのリモートインスタンスにパケットを送信
        /// </summary>
        protected void BrodcastRpc (byte methodId, DataStreamWriter rpcPacket, QosType qosType, bool important = true) {
            cacheRecordableIdentity.BrodcastRpc (componentIndex, methodId, rpcPacket, qosType, important);
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
