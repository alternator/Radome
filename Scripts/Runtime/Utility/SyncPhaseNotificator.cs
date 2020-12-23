using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ICKX.Radome;
using Unity.Networking.Transport;
using Unity.Collections;

public class SyncPhaseNotificator<PhaseDef> where PhaseDef : struct, System.Enum, System.IComparable
{
	public enum SyncMode
	{
		Default, 
		Force,
	}

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

	/// <summary>
	/// ローカル環境で任意のフェイズに到達したことを申告する
	/// 全クライアントが一致した場合に
	/// </summary>
	public void SetPhase (PhaseDef phase)
	{
		_SyncPhaseDefTable[GamePacketManager.UniqueId] = phase;

		using (var array = new NativeArray<byte>( 16, Unity.Collections.Allocator.Temp))
		{
			var packet = new NativeStreamWriter(array);
			packet.WriteByte((byte)BuiltInPacket.Type.SyncPhase);
			packet.WriteInt(TypeNameHash);
			packet.WriteByte((byte)SyncMode.Default);
			packet.WriteByte(_PhaseTable[phase]);
			GamePacketManager.Brodcast(packet, QosType.Reliable);
		}

		CheckComplate(phase);
	}

	/// <summary>
	/// サーバー専用 全クライアントを待たず強制的に指定のPhaseに遷移させる
	/// </summary>
	public void ForceSetPhase(PhaseDef phase)
	{
		if (!GamePacketManager.IsLeader) return;
		if (CurrentPhase.Equals(phase)) return;

		_SyncPhaseDefTable[GamePacketManager.UniqueId] = phase;

		using (var array = new NativeArray<byte>(16, Unity.Collections.Allocator.Temp))
		{
			var packet = new NativeStreamWriter(array);
			packet.WriteByte((byte)BuiltInPacket.Type.SyncPhase);
			packet.WriteInt(TypeNameHash);
			packet.WriteByte((byte)SyncMode.Force);
			packet.WriteByte(_PhaseTable[phase]);
			GamePacketManager.Brodcast(packet, QosType.Reliable);
		}

		PhaseDef prev = CurrentPhase;
		CurrentPhase = phase;
		OnChangePhase?.Invoke(prev, phase);
	}

	private void OnRecievePacket(ushort senderPlayerId, ulong uniqueId, byte type, NativeStreamReader stream)
	{
		if (type != (byte)BuiltInPacket.Type.SyncPhase) return;
		if (stream.ReadInt() != TypeNameHash) return;

		SyncMode mode = (SyncMode)stream.ReadByte();
		var phase = _PhaseDefs[stream.ReadByte()];

		if (mode == SyncMode.Default)
		{
			_SyncPhaseDefTable[uniqueId] = phase;
			CheckComplate(phase);
		}
		else
		{
			var keys = _SyncPhaseDefTable.Keys.ToArray();
			foreach (var key in keys)
			{
				_SyncPhaseDefTable[key] = phase;
			}

			PhaseDef prev = CurrentPhase;
			CurrentPhase = phase;
			OnChangePhase?.Invoke(prev, phase);
		}
	}

	private void CheckComplate (PhaseDef phase)
	{
		bool isComplete = true;
		foreach (var pair in _SyncPhaseDefTable)
		{
			if (!phase.Equals(pair.Value))
			{
				isComplete = false;
			}
		}

		if (isComplete && !CurrentPhase.Equals(phase))
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
