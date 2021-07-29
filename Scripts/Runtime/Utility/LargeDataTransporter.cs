﻿using System.Collections;
using System.Collections.Generic;
using Unity.Networking.Transport;
using UnityEngine;
using UpdateLoop = UnityEngine.PlayerLoop.Update;

using System.Security.Cryptography;
using Unity.Collections;
using System.IO;
using System.Threading.Tasks;

namespace ICKX.Radome {

	public enum TransporterType : byte {
		File = 0,
		LargeBytes,
	}

	public abstract class TransporterBase {
		public ushort targetPlayerId  { get; internal set; }
		public int hash { get; internal set; }
		internal int pos;

		public TransporterBase (int hash) {
			this.hash = hash;
			pos = 0;
		}
	}
	
	public abstract class TransporterBaseManager<Manager, Transporter> : ManagerBase<Manager> 
			where Manager : TransporterBaseManager<Manager, Transporter> where Transporter : TransporterBase {

		public enum FlagDef : byte {
			None = 0,
			Start,
			Complete,
			Cancel,
		}

		public delegate void OnSendCompleteEvent (Transporter transporter, bool isComplete);
		public delegate void OnRecieveStartEvent (Transporter transporter);
		public delegate void OnRecieveCompleteEvent(Transporter transporter, bool isComplete);

		public struct TransporterManagerUpdate {}

		public NetworkManagerBase NetworkManager { get; private set; } = null;

		protected Dictionary<int, Transporter> _sendTransporterTable;
		protected Dictionary<int, Transporter> _recieveTransporterTable;

		public event OnSendCompleteEvent OnSendComplete = null;
		public event OnRecieveStartEvent OnRecieveStart = null;
		public event OnRecieveCompleteEvent OnRecieveComplete = null;

		public abstract byte Type { get; }

		public IReadOnlyDictionary<int, Transporter> sendTransporterTable { get { return _sendTransporterTable; } }
		public IReadOnlyDictionary<int, Transporter> recieveTransporterTable { get { return _recieveTransporterTable; } }

		public int SendBytePerFrame = 16 * 1024;

		public const int HeaderSize = (NetworkLinkerConstants.HeaderSize + 2);

		public void SetNetworkManager (NetworkManagerBase networkManager) {
			if (networkManager != null) {
				networkManager.OnRecievePacket -= Instance.OnRecievePacketMethod;
			}
			NetworkManager = networkManager;

			networkManager.OnRecievePacket += Instance.OnRecievePacketMethod;

			_sendTransporterTable = new Dictionary<int, Transporter> ();
			_recieveTransporterTable = new Dictionary<int, Transporter> ();

			CustomPlayerLoopUtility.InsertLoopLast (typeof (UpdateLoop), new UnityEngine.LowLevel.PlayerLoopSystem () {
				type = typeof (TransporterManagerUpdate),
				updateDelegate = Instance.Update
			});
		}

		public bool IsSending (int hash)
		{
			return _sendTransporterTable.ContainsKey(hash);
		}

		public bool SendCancel(int hash, ushort playerId) {
			if (_sendTransporterTable.Remove(hash)) {
				using (var array = new NativeArray<byte>(7, Allocator.Temp))
				{
					var writer = new NativeStreamWriter(array);
					writer.WriteByte((byte)BuiltInPacket.Type.DataTransporter);
					writer.WriteByte((byte)TransporterType.File);
					writer.WriteInt(hash);
					writer.WriteByte((byte)FlagDef.Cancel);

					if (playerId == NetworkLinkerConstants.BroadcastId)
					{
						NetworkManager.Broadcast(writer, QosType.Reliable, true);
					}
					else
					{
						NetworkManager.Send(playerId, writer, QosType.Reliable);
					}
				}
				return true;
			} else {
				return false;
			}
		}

		protected void Update () {
			SendFragmentData ();
		}

		SHA256 crypto256 = new SHA256CryptoServiceProvider ();

		protected int ByteToHash (byte[] data) {
			return System.BitConverter.ToInt32 (crypto256.ComputeHash (data), 0);
		}

		protected int FileToHash (FileStream fs) {
			return System.BitConverter.ToInt32 (crypto256.ComputeHash (fs), 0);
		}

		private void OnRecievePacketMethod (ushort senderPlayerId, ulong senderUniqueId, byte type, NativeStreamReader stream) {
			if (type == (byte)BuiltInPacket.Type.DataTransporter) {
				var transType = stream.ReadByte ();
				if (transType != Type) return;

				int hash = stream.ReadInt ();
				FlagDef flag = (FlagDef)stream.ReadByte ();

				bool isStart = (flag == FlagDef.Start);
				bool isComplete = (flag == FlagDef.Complete);
				bool isCancel = (flag == FlagDef.Cancel);

				Transporter transporter;
				if(isCancel) {
                    if (_recieveTransporterTable.TryGetValue(hash, out transporter))
                    {
                        _recieveTransporterTable.Remove(hash);
                        ExecOnRecieveComplete(transporter, false);
                    }
                }
                else if (isStart) {
					transporter = RecieveStart (hash, stream);
					transporter.hash = hash;
					_recieveTransporterTable[hash] = transporter;
					OnRecieveStart?.Invoke (transporter);
				} else
                {
                    if (_recieveTransporterTable.TryGetValue(hash, out transporter))
                    {
                        RecieveFragmentData(hash, stream, transporter);
                        if (isComplete)
                        {
                            _recieveTransporterTable.Remove(hash);
                            RecieveComplete(hash, transporter);
                        }
                    }
				}
			}
		}

		protected void ExeceOnSendComplete (Transporter transporter, bool isComplete) {
			OnSendComplete?.Invoke (transporter, isComplete);
		}
		protected void ExecOnRecieveComplete (Transporter transporter, bool isComplete) {
			OnRecieveComplete?.Invoke (transporter, isComplete);
		}

		protected abstract void SendFragmentData ();
		protected abstract Transporter RecieveStart (int hash, NativeStreamReader stream);
		protected abstract void RecieveFragmentData (int hash, NativeStreamReader stream, Transporter transporter);
		protected abstract void RecieveComplete (int hash, Transporter transporter);
	}
}