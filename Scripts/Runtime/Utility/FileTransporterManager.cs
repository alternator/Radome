using System.Collections;
using System.Collections.Generic;
using Unity.Networking.Transport;
using UnityEngine;
using UpdateLoop = UnityEngine.Experimental.PlayerLoop.Update;
using UnityEngine.Experimental.LowLevel;
using System.Security.Cryptography;
using Unity.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ICKX.Radome {

	public class FileTransporter : TransporterBase {
		public string path { get; internal set; }

		internal FileStream fileStream;
		internal byte[] bufferA = null;
		internal byte[] bufferB = null;
		internal int bufferPosA = 0;
		internal int bufferPosB = 0;
		internal bool useBufferA = true;

		internal bool isAwait = false;

		internal byte[] buffer {
			get { return useBufferA ? bufferA : bufferB; }
		}
		internal byte[] writeBuffer {
			get { return !useBufferA ? bufferA : bufferB; }
		}

		internal int bufferPos {
			get { return useBufferA ? bufferPosA : bufferPosB; }
			set { if (useBufferA) bufferPosA = value; else bufferPosB = value; }
		}
		internal int writeBufferPos {
			get { return !useBufferA ? bufferPosA : bufferPosB; }
			set { if (!useBufferA) bufferPosA = value; else bufferPosB = value; }
		}

		public FileTransporter (int hash, string path, FileStream fs, int bufferSizeA, int bufferSizeB) : base(hash) {
			this.path = path;
			this.fileStream = fs;
			bufferA = new byte[bufferSizeA];
			bufferB = new byte[bufferSizeB];
		}

		internal void Resize (int newSize) {
			if(useBufferA) {
				System.Array.Resize (ref bufferA, bufferA.Length * 2);
			} else {
				System.Array.Resize (ref bufferB, bufferB.Length * 2);
			}
		}
	}

	public class FileTransporterManager : TransporterBaseManager<FileTransporterManager, FileTransporter> {

		public static string SaveDirectory;

		public override byte Type => (byte)TransporterType.File;

		//private byte[] buffer = null;

		public async void Send (ushort playerId, string fileName, System.IO.FileStream fileStream) {

			if (fileStream.Length <= NetworkLinkerPool.MTU - HeaderSize) {
				Debug.LogError ("MTU以下のサイズのデータは送れません");
				return;
			}

			int hash = FileToHash (fileStream);
			var transporter = new FileTransporter (hash, fileName, fileStream, SendBytePerFrame, 0);

			int nameByteCount = DataStreamWriter.GetByteSizeStr (fileName);
			int dataSize = NetworkLinkerPool.MTU - HeaderSize - 15 - nameByteCount;
			fileStream.Seek (0, SeekOrigin.Begin);
			int readSize = await fileStream.ReadAsync(transporter.buffer, 0, dataSize);
			//Debug.Log ("Start : " + string.Join ("", transporter.buffer));

			unsafe {
				fixed (byte* dataPtr = transporter.buffer) {
					using (var writer = new DataStreamWriter (dataSize + 15 + nameByteCount, Allocator.Temp)) {
						writer.Write ((byte)BuiltInPacket.Type.DataTransporter);
						writer.Write ((byte)TransporterType.File);
						writer.Write (hash);
						writer.Write ((byte)FlagDef.Start);
						writer.Write (fileName);
						writer.Write ((int)fileStream.Length);
						writer.Write ((ushort)dataSize);
						writer.WriteBytes (dataPtr, dataSize);
						NetworkManager.Brodcast (writer, QosType.Reliable, true);
					}
				}
			}
			transporter.pos += dataSize;
			sendTransporterTable[hash] = transporter;
		}

		public void Broadcast (string fileName, FileStream fileStream) {
			Send (ushort.MaxValue, fileName, fileStream);
		}

		List<int> removeTransporterList = new List<int> ();

		protected override async void SendFragmentData () {
			if (sendTransporterTable == null) return;

			if (!sendTransporterTable.Any (p => p.Value.isAwait)) {
				foreach (int hash in removeTransporterList) {
					sendTransporterTable.Remove (hash);
				}
				removeTransporterList.Clear ();

				foreach (var pair in sendTransporterTable) {
					var transporter = pair.Value;
					if (transporter.isAwait) continue;

					transporter.isAwait = true;
					transporter.fileStream.Seek (transporter.pos, SeekOrigin.Begin);
					int readSize = await transporter.fileStream.ReadAsync (transporter.buffer, 0, SendBytePerFrame);

					int sendAmount = 0;
					while (sendAmount < SendBytePerFrame) {
						//Debug.Log ("sendAmount=" + sendAmount + ", readSize" + readSize + ", pos" + transporter.pos);
						FlagDef flag = FlagDef.None;
						int dataSize = Mathf.Min (SendBytePerFrame - sendAmount, NetworkLinkerPool.MTU - HeaderSize - 7);

						if (transporter.pos + dataSize > transporter.fileStream.Length) {
							flag = FlagDef.Complete;
							dataSize = (int)transporter.fileStream.Length - transporter.pos;
						}

						unsafe {
							fixed (byte* dataPtr = &transporter.buffer[sendAmount]) {
								using (var writer = new DataStreamWriter (dataSize + 7, Allocator.Temp)) {
									writer.Write ((byte)BuiltInPacket.Type.DataTransporter);
									writer.Write ((byte)TransporterType.File);
									writer.Write (transporter.hash);
									writer.Write ((byte)flag);
									//writer.Write ((ushort)dataSize);
									writer.WriteBytes (dataPtr, dataSize);
									NetworkManager.Brodcast (writer, QosType.Reliable, true);
								}
							}
						}
						transporter.pos += dataSize;
						sendAmount += dataSize;
						if (flag == FlagDef.Complete) {
							transporter.fileStream.Dispose ();
							removeTransporterList.Add (transporter.hash);
							ExeceOnSendComplete (transporter, true);
							break;
						}
					}
					transporter.isAwait = false;
				}
			}
		}
		
		protected async Task WriteBufferToFile (FileTransporter transporter) {
			transporter.useBufferA = !transporter.useBufferA;
			int count = transporter.writeBufferPos;
			transporter.writeBufferPos = 0;
			if (count == 0) return;
			transporter.fileStream.Seek (transporter.pos, SeekOrigin.Begin);
			transporter.pos += count;
			transporter.isAwait = true;
			await transporter.fileStream.WriteAsync (transporter.writeBuffer, 0, count);
			transporter.isAwait = false;
		}

		protected override FileTransporter RecieveStart (int hash, DataStreamReader stream, ref DataStreamReader.Context ctx) {
			if(string.IsNullOrEmpty(SaveDirectory)) {
				SaveDirectory = Application.persistentDataPath + "/RecieveFile";
				System.IO.Directory.CreateDirectory (SaveDirectory);
			}

			string fileName = stream.ReadString (ref ctx);
			int fileSize = stream.ReadInt (ref ctx);
			ushort dataSize = stream.ReadUShort (ref ctx);

			string path = SaveDirectory + "/" + hash + "_" + fileName;
			FileStream fs = new FileStream (path, FileMode.Create, FileAccess.ReadWrite);

			var transporter = new FileTransporter (hash, fileName, fs, 8092, 8092);

			unsafe {
				fixed (byte* data = transporter.buffer) {
					stream.ReadBytes (ref ctx, data, dataSize);
					transporter.bufferPos += dataSize;
				}
			}
			return transporter;
		}

		protected override void RecieveFragmentData (int hash, DataStreamReader stream, ref DataStreamReader.Context ctx, FileTransporter transporter) {
			int fragmentSize = stream.Length - stream.GetBytesRead (ref ctx);

			while(transporter.bufferPos + fragmentSize > transporter.buffer.Length) {
				transporter.Resize (transporter.buffer.Length * 2);
				//Debug.Log ("Resize Buffer " + transporter.buffer.Length);
			}

			unsafe {
				fixed (byte* data = &transporter.buffer[transporter.bufferPos]) {
					stream.ReadBytes (ref ctx, data, fragmentSize);
					transporter.bufferPos += fragmentSize;
				}
			}
			if(!transporter.isAwait) { 
				var task = WriteBufferToFile (transporter);
			}
		}

		protected override async void RecieveComplete (int hash, FileTransporter transporter) {
			while (transporter.isAwait) await Task.Delay (10);
			await WriteBufferToFile (transporter);

			transporter.fileStream.Seek (0, SeekOrigin.Begin);
			var calcHash = FileToHash (transporter.fileStream);
			transporter.fileStream.Close ();

			if (transporter.hash != calcHash) {
				Debug.LogError ("ファイル送信失敗 : " + transporter.hash + "/" + calcHash + ":" + transporter.pos);
				ExecOnRecieveComplete (transporter, false);
			} else {
				//Debug.Log ("RecieveComplete FileData : " + transporter.hash);
				ExecOnRecieveComplete (transporter, true);
			}
		}
	}
}