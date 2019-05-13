using System.Collections;
using System.Collections.Generic;
using Unity.Networking.Transport;
using UnityEngine;
using UpdateLoop = UnityEngine.Experimental.PlayerLoop.Update;
using UnityEngine.Experimental.LowLevel;
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

			CustomPlayerLoopUtility.InsertLoopLast (typeof (UpdateLoop), new PlayerLoopSystem () {
				type = typeof (TransporterManagerUpdate),
				updateDelegate = Instance.Update
			});
		}

		public bool IsSending (int hash)
		{
			return _sendTransporterTable.ContainsKey(hash);
		}

		public bool SendCancel(int hash) {
			if (_sendTransporterTable.Remove(hash)) {
				using (var writer = new DataStreamWriter(7, Allocator.Temp)) {
					writer.Write((byte)BuiltInPacket.Type.DataTransporter);
					writer.Write((byte)TransporterType.File);
					writer.Write(hash);
					writer.Write((byte)FlagDef.Cancel);
					NetworkManager.Brodcast(writer, QosType.Reliable, true);
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

		private void OnRecievePacketMethod (ushort senderPlayerId, byte type, DataStreamReader stream, DataStreamReader.Context ctx) {
			if (type == (byte)BuiltInPacket.Type.DataTransporter) {
				var transType = stream.ReadByte (ref ctx);
				if (transType != Type) return;

				int hash = stream.ReadInt (ref ctx);
				FlagDef flag = (FlagDef)stream.ReadByte (ref ctx);

				bool isStart = (flag == FlagDef.Start);
				bool isComplete = (flag == FlagDef.Complete);
				bool isCancel = (flag == FlagDef.Cancel);

				Transporter transporter;
				if(isCancel) {
					transporter = _recieveTransporterTable[hash];
					_recieveTransporterTable.Remove (hash);
					ExecOnRecieveComplete(transporter, false);
				}else if (isStart) {
					transporter = RecieveStart (hash, stream, ref ctx);
					transporter.hash = hash;
					_recieveTransporterTable[hash] = transporter;
					OnRecieveStart?.Invoke (transporter);
				} else {
					transporter = _recieveTransporterTable[hash];
					RecieveFragmentData (hash, stream, ref ctx, transporter);
					if (isComplete) {
						_recieveTransporterTable.Remove (hash);
						RecieveComplete (hash, transporter);
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
		protected abstract Transporter RecieveStart (int hash, DataStreamReader stream, ref DataStreamReader.Context ctx);
		protected abstract void RecieveFragmentData (int hash, DataStreamReader stream, ref DataStreamReader.Context ctx, Transporter transporter);
		protected abstract void RecieveComplete (int hash, Transporter transporter);
	}
}