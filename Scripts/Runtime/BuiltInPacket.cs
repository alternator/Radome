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
            ReconnectPlayer,
            UnregisterPlayer,
            UpdatePlayerInfo,
			StopNetwork,

            ReserveNetId,
            ChangeAuthor,
			SyncAuthor,
            SyncTransform,
            BehaviourRpc,

			DataTransporter,
			SyncPhase,

			SpawnIdentity,
			DespawnIdentity,

		}
	}
}
