using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Networking.Transport;
using UnityEngine;

namespace ICKX.Radome {
    public static class BuiltInPacket {
        public enum Type : byte {
            MeasureRtt = 200,
            RegisterPlayer,
            UnregisterPlayer,
			NotifyRegisterPlayer,
			NotifyUnegisterPlayer,
			NotifyReconnectPlayer,
			NotifyDisconnectPlayer,
			UpdatePlayerInfo,
			StopNetwork,

            ReserveNetId,
            ChangeAuthor,
            SyncTransform,
            BehaviourRpc,

			DataTransporter,
        }
    }
}
