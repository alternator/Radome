using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ICKX.Radome;
using Unity.Networking.Transport;

public class SyncPhaseNotificator<PhaseDef> where PhaseDef : struct, System.Enum, System.IComparable
{
	public delegate void OnChangePhaseEvent(PhaseDef prev, PhaseDef next);

	public PhaseDef CurrentPhase;
	public event OnChangePhaseEvent OnChangePhase = null;

	protected int TypeNameHash { get; private set; }
	
	private PhaseDef[] _PhaseDefs;
	private Dictionary<PhaseDef, byte> _PhaseTable;

	private Dictionary<ulong, PhaseDef> _SyncPhaseDefTable = new Dictionary<ulong, PhaseDef>();

	public IReadOnlyDictionary<ulong, PhaseDef> SyncPhaseDefTable => _SyncPhaseDefTable;

	public SyncPhaseNotificator()
	{
		_PhaseTable = new Dictionary<PhaseDef, byte>();
		var array = System.Enum.GetValues(typeof(PhaseDef));
		_PhaseDefs = new PhaseDef[array.Length];
		for(byte i=0;i<array.Length;i++)
		{
			_PhaseDefs[i] = (PhaseDef)array.GetValue(i);
			_PhaseTable[(PhaseDef)array.GetValue(i)] = i;
		}

		TypeNameHash = typeof(PhaseDef).FullName.GetHashCode();

		GamePacketManager.OnRecievePacket += OnRecievePacket;
	}

	public void SetPhase (PhaseDef phase)
	{
		_SyncPhaseDefTable[GamePacketManager.UniqueId] = phase;

		using (var packet = new DataStreamWriter (6, Unity.Collections.Allocator.Temp))
		{
			packet.Write((byte)BuiltInPacket.Type.SyncPhase);
			packet.Write(TypeNameHash);
			packet.Write(_PhaseTable[phase]);
			GamePacketManager.Brodcast(packet, QosType.Reliable);
		}
	}

	private void OnRecievePacket(ushort senderPlayerId, ulong uniqueId, byte type, Unity.Networking.Transport.DataStreamReader stream, Unity.Networking.Transport.DataStreamReader.Context ctx)
	{
		if (type != (byte)BuiltInPacket.Type.SyncPhase) return;
		if (stream.ReadInt(ref ctx) != TypeNameHash) return;

		var phase = _PhaseDefs[stream.ReadByte(ref ctx)];
		_SyncPhaseDefTable[uniqueId] = phase;

		bool isComplete = true;
		foreach (var pair in _SyncPhaseDefTable)
		{
			if (phase.Equals(pair.Value))
			{
				isComplete = false;
			}
		}

		if(isComplete)
		{
			PhaseDef prev = CurrentPhase;
			CurrentPhase = phase;
			OnChangePhase?.Invoke(prev, phase);
		}
	}

	public void Reset ()
	{
		_SyncPhaseDefTable.Clear();
	}
}
