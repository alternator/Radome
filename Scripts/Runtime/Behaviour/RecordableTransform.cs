using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Networking.Transport;
using UnityEngine;

namespace ICKX.Radome {
    [RequireComponent (typeof (RecordableIdentity))]
    public class RecordableTransform : MonoBehaviour {

        public enum Mode {
            Default,
            Prediction,
        }

        [SerializeField]
        private Mode mode = Mode.Default;
        [SerializeField]
        private QosType qosType = QosType.Unreliable;
        [SerializeField]
        private bool useTranslate = false;

        [SerializeField]
        private float minInterval = 0.05f;
        [SerializeField]
        private float maxInterval = 1.0f;
        [SerializeField]
        private float positionLerp = 0.5f;
        [SerializeField]
        private float rotationLerp = 0.5f;
        [SerializeField]
        private float deltaPosSendThreshold = 0.1f;
        [SerializeField]
        private float deltaRotSendThreshold = 1.0f;

        private uint prevSendTime = 0;
        private uint prevReceiveTime = 0;

        private bool defaultIsKinematic;

        private bool forceUpdateTransfromFrag = false;

        private Vector3 recPosition;
        private Quaternion recRotation;
        private Vector3 recVelocity;
        private Vector3 recvAnglerVelicity;

        public Transform CacheTransform { get; private set; }
        public Rigidbody CacheRigidbody { get; private set; }
        public RecordableIdentity CacheRecordableIdentity { get; private set; }


        private void Awake () {
            CacheTransform = transform;
            CacheRigidbody = GetComponent<Rigidbody> ();
            CacheRecordableIdentity = GetComponent<RecordableIdentity> ();

            if (CacheRigidbody) {
                defaultIsKinematic = CacheRigidbody.isKinematic;
            }
        }

        private void OnEnable () {
            prevSendTime = 0;
            prevReceiveTime = 0;
            SetRecTransformValue ();
            CacheRecordableIdentity.OnChangeAuthor += OnChangeAuthor;
        }

        private void OnDisable () {
            if (CacheRecordableIdentity == null) return;
            CacheRecordableIdentity.OnChangeAuthor -= OnChangeAuthor;
        }
        
        private void Update () {

            if (!CacheRecordableIdentity.isSyncComplete) return;

            if (CacheRecordableIdentity.hasAuthority) {
                SendTransform ();
            } else {
                LerpTransform ();
            }
        }

        private void SetRecTransformValue () {
            recPosition = CacheTransform.position;
            recRotation = CacheTransform.rotation;
            if (CacheRigidbody != null) {
                recVelocity = CacheRigidbody.velocity;
                recvAnglerVelicity = CacheRigidbody.angularVelocity;
            } else {
                recVelocity = Vector3.zero;
                recvAnglerVelicity = Vector3.zero;
            }
        }

        private void OnChangeAuthor (RecordableIdentity identity, ushort author, bool hasAuthority) {
//            if (!CacheRecordableIdentity.isSyncComplete) return;

            if (CacheRigidbody && !defaultIsKinematic) {
                if (!hasAuthority) {
                    CacheRigidbody.isKinematic = true;
                } else {
                    CacheRigidbody.isKinematic = false;
                }
            }
            if (hasAuthority) {
                //受け取った速度を設定
                if (CacheRigidbody) {
                    CacheRigidbody.velocity = recVelocity;
                    CacheRigidbody.angularVelocity = recvAnglerVelicity;
                }
                //所有権変更後はすぐパケットを送る
                ForceUpdateTransfrom ();
            }
        }

        /// <summary>
        /// 次の位置の同期パケットは条件を無視して必ず送る
        /// </summary>
        public void ForceUpdateTransfrom () {
            forceUpdateTransfromFrag = true;
        }

        /// 所有者側は現在のTransform情報を送信する
        private void SendTransform () {

            if (forceUpdateTransfromFrag) {
                forceUpdateTransfromFrag = false;
            } else {
                float elapsedTime = (float)(RecordableIdentityManager.progressTimeSinceStartup - prevSendTime) / 1000.0f;
                if (elapsedTime <= minInterval) return;
                //定期更新.
                if (elapsedTime <= maxInterval) {
                    //一定以上動いたら更新
                    float sqrDistance = Vector3.SqrMagnitude (recPosition - CacheTransform.position);
                    float diffAngle = Quaternion.Angle (recRotation, CacheTransform.rotation);
                    if (diffAngle < deltaRotSendThreshold && sqrDistance < deltaPosSendThreshold * deltaPosSendThreshold) return;
                }
                //Debug.Log ("SendTransform elapsedTime = " + elapsedTime);
            }

			//TODO できればScene単位でパケットをまとめて、type(1byte) sceneHash(4byte)の5byteのデータを削減したい
			using (var packet = new DataStreamWriter (65, Allocator.Temp)) {
                packet.Write ((byte)BuiltInPacket.Type.SyncTransform);
				packet.Write (CacheRecordableIdentity.sceneHash);
                packet.Write (CacheRecordableIdentity.netId);
                packet.Write (RecordableIdentityManager.progressTimeSinceStartup);
                packet.Write (CacheTransform.position);
                packet.Write (CacheTransform.rotation);

                if (CacheRigidbody != null) {
                    packet.Write (CacheRigidbody.velocity);
                    packet.Write (CacheRigidbody.angularVelocity);
                } else {
                    packet.Write (Vector3.zero);
                    packet.Write (Vector3.zero);
                }

                GamePacketManager.Brodcast (packet, qosType);
            }

            SetRecTransformValue ();

            prevSendTime = RecordableIdentityManager.progressTimeSinceStartup;
        }

        //受け取った情報をもとに補間する
        private void LerpTransform () {
            Vector3 currentPos = CacheTransform.position;
            Vector3 targetPosition = recPosition;
            Quaternion targetRotation = recRotation;

            if(mode == Mode.Prediction) {
                float delay = (RecordableIdentityManager.progressTimeSinceStartup - prevReceiveTime) / 1000.0f;
                if (delay < 0.0f) delay = 0.0f;
                //通信遅延を想定した予測位置.
                targetPosition = recPosition + recVelocity * delay;
            }

            if (useTranslate) {
                CacheTransform.Translate (Vector3.Lerp (currentPos, targetPosition, positionLerp) - currentPos, Space.World);
            } else {
                CacheTransform.position = Vector3.Lerp (currentPos, targetPosition, positionLerp);
            }
            CacheTransform.rotation = Quaternion.Slerp (CacheTransform.rotation, recRotation, rotationLerp);
        }

        /// <summary>
        /// NetowrkIdendtity経由でデータを送ってもらう
        /// </summary>
        /// <param name="packet"></param>
        internal void OnRecieveSyncTransformPacket (ushort senderPlayerId, ref DataStreamReader packet, ref DataStreamReader.Context ctx) {

            if (CacheRecordableIdentity.hasAuthority) return;

            var progressTime = packet.ReadUInt (ref ctx);
            var position = packet.ReadVector3 (ref ctx);
            var rotation = packet.ReadQuaternion (ref ctx);
            var velocity = packet.ReadVector3 (ref ctx);
            var anglerVelocity = packet.ReadVector3 (ref ctx);

			//受信パケットに記録した時間が古いものなら使わない.
			if (progressTime < prevReceiveTime) {
                Debug.Log ("eject");
                return;
            }

            //Rigidbodyの値はすぐに適用
            if (CacheRigidbody != null) {
                CacheRigidbody.velocity = velocity;
                CacheRigidbody.angularVelocity = anglerVelocity;
            }

            prevReceiveTime = progressTime;
            recPosition = position;
            recRotation = rotation;
            recVelocity = velocity;
            recvAnglerVelicity = anglerVelocity;
            //Debug.Log ("OnRecieveSyncTransformPacket");
        }
    }
}
