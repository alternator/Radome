using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Networking.Transport.LowLevel.Unsafe;
using UnityEngine;

namespace ICKX.Radome
{

    public class ChuckPacketManager : System.IDisposable
    {
        public DataStreamWriter ChunkedPacketBuffer;

        private DataStreamWriter _CurrentPacketBuffer;

        public ChuckPacketManager (ushort maxChunkSize)
        {
            ChunkedPacketBuffer = new DataStreamWriter(ushort.MaxValue, Allocator.Persistent);
            _CurrentPacketBuffer = new DataStreamWriter(maxChunkSize, Allocator.Persistent);
        }

        public unsafe void AddChunk (DataStreamWriter data)
        {
            //MTU以下になるようにまとめ終ったChunkを書き込み
            if(data.Length + _CurrentPacketBuffer.Length >= _CurrentPacketBuffer.Capacity)
            {
                WriteCurrentBuffer();
                _CurrentPacketBuffer.Clear();
            }

            byte* dataPtr = DataStreamUnsafeUtility.GetUnsafeReadOnlyPtr(data);
            _CurrentPacketBuffer.Write((ushort)data.Length);
            _CurrentPacketBuffer.WriteBytes(dataPtr, data.Length);
        }

        public unsafe void WriteCurrentBuffer ()
        {
            if(ChunkedPacketBuffer.Length + _CurrentPacketBuffer.Length + 2 > ChunkedPacketBuffer.Capacity)
            {
                ChunkedPacketBuffer.Capacity *= 2;
            }
            byte* tempPtr = DataStreamUnsafeUtility.GetUnsafeReadOnlyPtr(_CurrentPacketBuffer);
            ChunkedPacketBuffer.Write((ushort)_CurrentPacketBuffer.Length);
            ChunkedPacketBuffer.WriteBytes(tempPtr, _CurrentPacketBuffer.Length);
        }

        public void Clear ()
        {
            _CurrentPacketBuffer.Clear();
            ChunkedPacketBuffer.Clear();
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
                    ChunkedPacketBuffer.Dispose();
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