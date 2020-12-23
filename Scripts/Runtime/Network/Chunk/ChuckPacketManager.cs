using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Networking.Transport;
using UnityEngine;

namespace ICKX.Radome
{

    public class ChuckPacketManager : System.IDisposable
    {
		public NativeStreamWriter ChunkedPacketBufferWriter;

		private NativeStreamWriter _CurrentPacketBufferWriter;

		private NativeArray<byte> _ChunkedPacketBuffer;
		private NativeArray<byte> _CurrentPacketBuffer;

		public ChuckPacketManager (ushort maxChunkSize)
        {
			_ChunkedPacketBuffer = new NativeArray<byte>(ushort.MaxValue, Allocator.Persistent);
			_CurrentPacketBuffer = new NativeArray<byte>(ushort.MaxValue, Allocator.Persistent);

			ChunkedPacketBufferWriter = new NativeStreamWriter(_ChunkedPacketBuffer);
            _CurrentPacketBufferWriter = new NativeStreamWriter(_CurrentPacketBuffer);
        }

        public unsafe void AddChunk (NativeStreamWriter data)
        {
			if (data.Length == 0) return;
            //MTU以下になるようにまとめ終ったChunkを書き込み
            if(data.Length + _CurrentPacketBufferWriter.Length >= _CurrentPacketBufferWriter.Capacity)
            {
                WriteCurrentBuffer();
                _CurrentPacketBufferWriter.Clear();
            }

			byte* dataPtr = data.GetUnsafePtr();
            _CurrentPacketBufferWriter.WriteUShort((ushort)data.Length);
            _CurrentPacketBufferWriter.WriteBytes(dataPtr, data.Length);
			//Debug.Log("AddChunk ; " + data.Length + " : " + _CurrentPacketBufferWriter.Length);
		}

        public unsafe void WriteCurrentBuffer ()
		{
			//if (_CurrentPacketBufferWriter.Length == 0) return;
			if (ChunkedPacketBufferWriter.Length + _CurrentPacketBufferWriter.Length + 2 >= ChunkedPacketBufferWriter.Capacity)
			{
				int newSize = _ChunkedPacketBuffer.Length * 2;
				_ChunkedPacketBuffer.Dispose();
				_ChunkedPacketBuffer = new NativeArray<byte>(newSize, Allocator.Persistent);
				ChunkedPacketBufferWriter = new NativeStreamWriter(_ChunkedPacketBuffer);
			}

            byte* tempPtr = _CurrentPacketBufferWriter.GetUnsafePtr();
            ChunkedPacketBufferWriter.WriteUShort((ushort)_CurrentPacketBufferWriter.Length);
            ChunkedPacketBufferWriter.WriteBytes(tempPtr, _CurrentPacketBufferWriter.Length);
        }

        public void Clear ()
        {
            _CurrentPacketBufferWriter.Clear();
            ChunkedPacketBufferWriter.Clear();
        }

#region IDisposable Support
        private bool disposedValue = false; // 重複する呼び出しを検出するには

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _CurrentPacketBuffer.Dispose();
                    _ChunkedPacketBuffer.Dispose();
                }
                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
#endregion

    }
}