using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using System;
using Unity.Networking.Transport;
using UnityEngine;

namespace ICKX.Radome
{
	[StructLayout(LayoutKind.Explicit)]
	internal struct UIntFloat
	{
		[FieldOffset(0)] public float floatValue;

		[FieldOffset(0)] public uint intValue;

		[FieldOffset(0)] public double doubleValue;

		[FieldOffset(0)] public ulong longValue;
	}

	public static class NativeStreamUtility
	{
		public static unsafe void DumpReader(NativeStreamReader stream)
		{
			var data = new byte[stream.Length];
			fixed (byte* ptr = data)
			{
				UnsafeUtility.MemCpy(ptr, stream.GetUnsafePtr(), stream.Length);
				Debug.Log($"Dump : {string.Join(",", data)}");
			}
		}
		public static unsafe void DumpWriter(NativeStreamWriter stream)
		{
			var data = new byte[stream.Length];
			fixed (byte* ptr = data)
			{
				UnsafeUtility.MemCpy(ptr, stream.GetUnsafePtr(), stream.Length);
				Debug.Log($"Dump : {string.Join(",", data)}");
			}
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	public unsafe struct NativeStreamWriter
	{
		struct StreamData
		{
			[NativeDisableUnsafePtrRestriction] public byte* buffer;
			public int length;
			public int capacity;
			public ulong bitBuffer;
			public int bitIndex;
			public int failedWrites;
		}

		[NativeDisableUnsafePtrRestriction] StreamData m_Data;
		[NativeDisableUnsafePtrRestriction] internal IntPtr m_SendHandleData;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
		internal AtomicSafetyHandle m_Safety;
#endif
		public NativeStreamWriter(int length, Allocator allocator)
		{
#if ENABLE_UNITY_COLLECTIONS_CHECKS
			//if (allocator != Allocator.Temp)
			//	throw new InvalidOperationException("NativeStreamWriters can only be created with temp memory");
#endif
			Initialize(out this, new NativeArray<byte>(length, allocator));
		}
		public NativeStreamWriter(NativeArray<byte> data)
		{
			Initialize(out this, data);
		}
		public NativeArray<byte> AsNativeArray()
		{
			var na = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<byte>(m_Data.buffer, Length, Allocator.Invalid);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
			NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref na, m_Safety);
#endif
			return na;
		}
		private static void Initialize(out NativeStreamWriter self, NativeArray<byte> data)
		{
			self.m_SendHandleData = IntPtr.Zero;

			self.m_Data.capacity = data.Length;
			self.m_Data.length = 0;
			self.m_Data.buffer = (byte*)data.GetUnsafePtr();
			self.m_Data.bitBuffer = 0;
			self.m_Data.bitIndex = 0;
			self.m_Data.failedWrites = 0;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
			self.m_Safety = NativeArrayUnsafeUtility.GetAtomicSafetyHandle(data);
#endif
			uint test = 1;
			unsafe
			{
				byte* test_b = (byte*)&test;
				self.m_IsLittleEndian = test_b[0] == 1 ? 1 : 0;
			}
		}

		public static int GetByteSizeStr (string value)
		{
			if (string.IsNullOrEmpty(value)) return 0;
			return System.Text.Encoding.UTF8.GetByteCount(value);
		}

		private int m_IsLittleEndian;
		private bool IsLittleEndian => m_IsLittleEndian != 0;

		private static short ByteSwap(short val)
		{
			return (short)(((val & 0xff) << 8) | ((val >> 8) & 0xff));
		}
		private static int ByteSwap(int val)
		{
			return (int)(((val & 0xff) << 24) | ((val & 0xff00) << 8) | ((val >> 8) & 0xff00) | ((val >> 24) & 0xff));
		}

		/// <summary>
		/// True if there is a valid data buffer present. This would be false
		/// if the writer was created with no arguments.
		/// </summary>
		public bool IsCreated {
			get { return m_Data.buffer != null; }
		}

		public bool HasFailedWrites => m_Data.failedWrites > 0;

		/// <summary>
		/// The total size of the data buffer, see <see cref="Length"/> for
		/// the size of space used in the buffer.
		/// </summary>
		public int Capacity {
			get {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
				AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
				return m_Data.capacity;
			}
		}

		/// <summary>
		/// The size of the buffer used. See <see cref="Capacity"/> for the total size.
		/// </summary>
		public int Length {
			get {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
				AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
				SyncBitData();
				return m_Data.length + ((m_Data.bitIndex + 7) >> 3);
			}
		}
		/// <summary>
		/// The size of the buffer used in bits. See <see cref="Length"/> for the length in bytes.
		/// </summary>
		public int LengthInBits {
			get {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
				AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
				SyncBitData();
				return m_Data.length * 8 + m_Data.bitIndex;
			}
		}

		private void SyncBitData()
		{
			var bitIndex = m_Data.bitIndex;
			if (bitIndex <= 0)
				return;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
			AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
			var bitBuffer = m_Data.bitBuffer;
			int offset = 0;
			while (bitIndex > 0)
			{
				m_Data.buffer[m_Data.length + offset] = (byte)bitBuffer;
				bitIndex -= 8;
				bitBuffer >>= 8;
				++offset;
			}
		}
		public void Flush()
		{
			while (m_Data.bitIndex > 0)
			{
				m_Data.buffer[m_Data.length++] = (byte)m_Data.bitBuffer;
				m_Data.bitIndex -= 8;
				m_Data.bitBuffer >>= 8;
			}

			m_Data.bitIndex = 0;
		}
		
//		public void Resize (int capacity, Allocator allocator)
//		{
//			if (m_Data.capacity == capacity)
//				return;

//#if ENABLE_UNITY_COLLECTIONS_CHECKS
//			AtomicSafetyHandle.CheckWriteAndBumpSecondaryVersion(m_Safety);
//			if (m_Data.length > capacity)
//				throw new InvalidOperationException("Cannot shrink a data stream to be shorter than the current data in it");
//#endif
//			byte* newbuf = (byte*)UnsafeUtility.Malloc(capacity, UnsafeUtility.AlignOf<byte>(), allocator);
//			UnsafeUtility.MemCpy(newbuf, m_Data.buffer, m_Data.length);
//			UnsafeUtility.Free(m_Data.buffer, allocator);
//			m_Data.buffer = newbuf;
//			m_Data.capacity = capacity;
//		}

		public byte* GetUnsafePtr ()
		{
			return m_Data.buffer;
		}

		public bool WriteBytes(byte* data, int bytes)
		{
#if ENABLE_UNITY_COLLECTIONS_CHECKS
			AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
			if (m_Data.length + ((m_Data.bitIndex + 7) >> 3) + bytes > m_Data.capacity)
			{
				++m_Data.failedWrites;
				return false;
			}
			Flush();
			UnsafeUtility.MemCpy(m_Data.buffer + m_Data.length, data, bytes);
			m_Data.length += bytes;
			return true;
		}

		public bool WriteBytes(byte[] data, int bytes)
		{
#if ENABLE_UNITY_COLLECTIONS_CHECKS
			AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
			if (m_Data.length + ((m_Data.bitIndex + 7) >> 3) + bytes > m_Data.capacity)
			{
				++m_Data.failedWrites;
				return false;
			}
			Flush();

			fixed (byte* ptr = data)
			{
				UnsafeUtility.MemCpy(m_Data.buffer + m_Data.length, ptr, bytes);
			}
			m_Data.length += bytes;
			return true;
		}

		public bool WriteByte(byte value)
		{
			return WriteBytes((byte*)&value, sizeof(byte));
		}

		/// <summary>
		/// Copy NativeArray of bytes into the writers data buffer.
		/// </summary>
		/// <param name="value">Source byte array</param>
		public bool WriteBytes(NativeArray<byte> value)
		{
			return WriteBytes((byte*)value.GetUnsafeReadOnlyPtr(), value.Length);
		}

		public bool WriteShort(short value)
		{
			return WriteBytes((byte*)&value, sizeof(short));
		}

		public bool WriteUShort(ushort value)
		{
			return WriteBytes((byte*)&value, sizeof(ushort));
		}

		public bool WriteInt(int value)
		{
			return WriteBytes((byte*)&value, sizeof(int));
		}

		public bool WriteUInt(uint value)
		{
			return WriteBytes((byte*)&value, sizeof(uint));
		}
		public bool WriteLong(long value)
		{
			return WriteBytes((byte*)&value, sizeof(long));
		}
		public bool WriteULong(ulong value)
		{
			return WriteBytes((byte*)&value, sizeof(ulong));
		}
		public bool WriteVector3(Vector3 value)
		{
			return WriteFloat(value.x) && WriteFloat(value.y) && WriteFloat(value.z);
		}
		public bool WriteQuaternion(Quaternion value)
		{
			return WriteFloat(value.x) && WriteFloat(value.y) && WriteFloat(value.z) && WriteFloat(value.w);
		}

		public bool WriteShortNetworkByteOrder(short value)
		{
			short netValue = IsLittleEndian ? ByteSwap(value) : value;
			return WriteBytes((byte*)&netValue, sizeof(short));
		}

		public bool WriteUShortNetworkByteOrder(ushort value)
		{
			return WriteShortNetworkByteOrder((short)value);
		}

		public bool WriteIntNetworkByteOrder(int value)
		{
			int netValue = IsLittleEndian ? ByteSwap(value) : value;
			return WriteBytes((byte*)&netValue, sizeof(int));
		}

		public bool WriteUIntNetworkByteOrder(uint value)
		{
			return WriteIntNetworkByteOrder((int)value);
		}

		public bool WriteFloat(float value)
		{
			UIntFloat uf = new UIntFloat();
			uf.floatValue = value;
			return WriteInt((int)uf.intValue);
		}

		private void FlushBits()
		{
			while (m_Data.bitIndex >= 8)
			{
				m_Data.buffer[m_Data.length++] = (byte)m_Data.bitBuffer;
				m_Data.bitIndex -= 8;
				m_Data.bitBuffer >>= 8;
			}
		}
		void WriteRawBitsInternal(uint value, int numbits)
		{
#if ENABLE_UNITY_COLLECTIONS_CHECKS
			if (numbits < 0 || numbits > 32)
				throw new ArgumentOutOfRangeException("Invalid number of bits");
			if (value >= (1UL << numbits))
				throw new ArgumentOutOfRangeException("Value does not fit in the specified number of bits");
#endif

			m_Data.bitBuffer |= ((ulong)value << m_Data.bitIndex);
			m_Data.bitIndex += numbits;
		}

		public bool WritePackedUInt(uint value, NetworkCompressionModel model)
		{
			int bucket = model.CalculateBucket(value);
			uint offset = model.bucketOffsets[bucket];
			int bits = model.bucketSizes[bucket];
			ushort encodeEntry = model.encodeTable[bucket];
#if ENABLE_UNITY_COLLECTIONS_CHECKS
			AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
			if (m_Data.length + ((m_Data.bitIndex + encodeEntry & 0xff + bits + 7) >> 3) > m_Data.capacity)
			{
				++m_Data.failedWrites;
				return false;
			}
			WriteRawBitsInternal((uint)(encodeEntry >> 8), encodeEntry & 0xFF);
			WriteRawBitsInternal(value - offset, bits);
			FlushBits();
			return true;
		}
		public bool WritePackedInt(int value, NetworkCompressionModel model)
		{
			uint interleaved = (uint)((value >> 31) ^ (value << 1));      // interleave negative values between positive values: 0, -1, 1, -2, 2
			return WritePackedUInt(interleaved, model);
		}
		public bool WritePackedFloat(float value, NetworkCompressionModel model)
		{
			return WritePackedFloatDelta(value, 0, model);
		}
		public bool WritePackedUIntDelta(uint value, uint baseline, NetworkCompressionModel model)
		{
			int diff = (int)(baseline - value);
			return WritePackedInt(diff, model);
		}
		public bool WritePackedIntDelta(int value, int baseline, NetworkCompressionModel model)
		{
			int diff = (int)(baseline - value);
			return WritePackedInt(diff, model);
		}
		public bool WritePackedFloatDelta(float value, float baseline, NetworkCompressionModel model)
		{
			var bits = 0;
			if (value != baseline)
				bits = 32;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
			AtomicSafetyHandle.CheckWriteAndThrow(m_Safety);
#endif
			if (m_Data.length + ((m_Data.bitIndex + 1 + bits + 7) >> 3) > m_Data.capacity)
			{
				++m_Data.failedWrites;
				return false;
			}
			if (bits == 0)
				WriteRawBitsInternal(0, 1);
			else
			{
				WriteRawBitsInternal(1, 1);
				UIntFloat uf = new UIntFloat();
				uf.floatValue = value;
				WriteRawBitsInternal(uf.intValue, bits);
			}
			FlushBits();
			return true;
		}

		public unsafe bool WriteString(string value)
		{
			if (string.IsNullOrEmpty(value))
			{
				return WriteUShort((ushort)0);
			}
			else
			{
				var data = System.Text.Encoding.UTF8.GetBytes(value);
				WriteUShort((ushort)data.Length);
				fixed (byte* dataPtr = data)
				{
					return WriteBytes(dataPtr, data.Length);
				}
			}
		}

		public unsafe bool WriteNativeString(NativeString64 str)
		{
			int length = (int)*((ushort*)&str) + 2;
			byte* data = ((byte*)&str);
			return WriteBytes(data, length);
		}

		public unsafe bool WritePackedStringDelta(NativeString64 str, NativeString64 baseline, NetworkCompressionModel model)
		{
			ushort length = *((ushort*)&str);
			byte* data = ((byte*)&str) + 2;
			ushort baseLength = *((ushort*)&baseline);
			byte* baseData = ((byte*)&baseline) + 2;
			var oldData = m_Data;
			if (!WritePackedUIntDelta(length, baseLength, model))
				return false;
			bool didFailWrite = false;
			if (length <= baseLength)
			{
				for (int i = 0; i < length; ++i)
					didFailWrite |= !WritePackedUIntDelta(data[i], baseData[i], model);
			}
			else
			{
				for (int i = 0; i < baseLength; ++i)
					didFailWrite |= !WritePackedUIntDelta(data[i], baseData[i], model);
				for (int i = baseLength; i < length; ++i)
					didFailWrite |= !WritePackedUInt(data[i], model);
			}
			// If anything was not written, rewind to the previous position
			if (didFailWrite)
			{
				m_Data = oldData;
				++m_Data.failedWrites;
			}
			return !didFailWrite;
		}

		/// <summary>
		/// Moves the write position to the start of the data buffer used.
		/// </summary>
		public void Clear()
		{
			m_Data.length = 0;
			m_Data.bitIndex = 0;
			m_Data.bitBuffer = 0;
			m_Data.failedWrites = 0;
		}
	}

	public unsafe struct NativeStreamReader
	{
		struct Context
		{
			public int m_ReadByteIndex;
			public int m_BitIndex;
			public ulong m_BitBuffer;
			public int m_FailedReads;
		}

		byte* m_bufferPtr;
		Context m_Context;
		int m_Length;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
		AtomicSafetyHandle m_Safety;
#endif

		public NativeStreamReader(NativeStreamWriter writer, int startIndex, int length)
		{
#if ENABLE_UNITY_COLLECTIONS_CHECKS
			m_Safety = writer.m_Safety;
#endif
			m_bufferPtr = writer.GetUnsafePtr() + startIndex;
			m_Length = length;
			m_Context = default;

			uint test = 1;
			unsafe
			{
				byte* test_b = (byte*)&test;
				m_IsLittleEndian = test_b[0] == 1 ? 1 : 0;
			}
		}

		public NativeStreamReader(NativeArray<byte> array)
		{
#if ENABLE_UNITY_COLLECTIONS_CHECKS
			m_Safety = NativeArrayUnsafeUtility.GetAtomicSafetyHandle(array);
#endif
			m_bufferPtr = (byte*)array.GetUnsafeReadOnlyPtr();
			m_Length = array.Length;
			m_Context = default;

			uint test = 1;
			unsafe
			{
				byte* test_b = (byte*)&test;
				m_IsLittleEndian = test_b[0] == 1 ? 1 : 0;
			}
		}

		public NativeStreamReader(NativeArray<byte> array, int startIndex, int length)
		{
#if ENABLE_UNITY_COLLECTIONS_CHECKS
			m_Safety = NativeArrayUnsafeUtility.GetAtomicSafetyHandle(array);
#endif
			m_bufferPtr = (byte*)array.GetUnsafeReadOnlyPtr() + startIndex;
			m_Length = length;
			m_Context = default;

			uint test = 1;
			unsafe
			{
				byte* test_b = (byte*)&test;
				m_IsLittleEndian = test_b[0] == 1 ? 1 : 0;
			}
		}

		private int m_IsLittleEndian;
		private bool IsLittleEndian => m_IsLittleEndian != 0;

		private static short ByteSwap(short val)
		{
			return (short)(((val & 0xff) << 8) | ((val >> 8) & 0xff));
		}
		private static int ByteSwap(int val)
		{
			return (int)(((val & 0xff) << 24) | ((val & 0xff00) << 8) | ((val >> 8) & 0xff00) | ((val >> 24) & 0xff));
		}

		public bool HasFailedReads => m_Context.m_FailedReads > 0;
		/// <summary>
		/// The total size of the buffer space this reader is working with.
		/// </summary>
		public int Length {
			get {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
				AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
				return m_Length;
			}
		}

		/// <summary>
		/// True if the reader has been pointed to a valid buffer space. This
		/// would be false if the reader was created with no arguments.
		/// </summary>
		public bool IsCreated {
			get { return m_bufferPtr != null; }
		}

		public NativeStreamReader ReadChunk (int length)
		{
			if (m_Context.m_BitIndex != 0) throw new System.NotImplementedException();

			NativeStreamReader chunk = default;
			chunk.m_bufferPtr = m_bufferPtr + m_Context.m_ReadByteIndex;
			chunk.m_Length = length;
			chunk.m_Context = default;

			m_Context.m_ReadByteIndex += length;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
			chunk.m_Safety = m_Safety;
#endif
			return chunk;
		}
		
		public void SetPosition (int pos)
		{
			if (m_Context.m_BitIndex != 0) throw new System.NotImplementedException();
			if (pos >= m_Length) throw new System.ArgumentOutOfRangeException();
			m_Context.m_ReadByteIndex = pos;
		}

		/// <summary>
		/// Read and copy data to the memory location pointed to, an exception will
		/// be thrown if it does not fit.
		/// </summary>
		/// <param name="data"></param>
		/// <param name="length"></param>
		/// <exception cref="ArgumentOutOfRangeException">Thrown if the length
		/// will put the reader out of bounds based on the current read pointer
		/// position.</exception>
		public void ReadBytes(byte* data, int length)
		{
#if ENABLE_UNITY_COLLECTIONS_CHECKS
			AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
			if (GetBytesRead() + length > m_Length)
			{
				++m_Context.m_FailedReads;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
				throw new System.ArgumentOutOfRangeException();
#else
                UnsafeUtility.MemClear(data, length);
                return;
#endif
			}
			// Restore the full bytes moved to the bit buffer but no consumed
			m_Context.m_ReadByteIndex -= (m_Context.m_BitIndex >> 3);
			m_Context.m_BitIndex = 0;
			m_Context.m_BitBuffer = 0;
			UnsafeUtility.MemCpy(data, m_bufferPtr + m_Context.m_ReadByteIndex, length);
			m_Context.m_ReadByteIndex += length;
		}

		/// <summary>
		/// Read and copy data into the given NativeArray of bytes, an exception will
		/// be thrown if not enough bytes are available.
		/// </summary>
		/// <param name="array"></param>
		public void ReadBytes(NativeArray<byte> array)
		{
			ReadBytes((byte*)array.GetUnsafePtr(), array.Length);
		}

		public int GetBytesRead()
		{
			return m_Context.m_ReadByteIndex - (m_Context.m_BitIndex >> 3);
		}
		public int GetBitsRead()
		{
			return (m_Context.m_ReadByteIndex << 3) - m_Context.m_BitIndex;
		}

		public byte* GetUnsafePtr()
		{
			return m_bufferPtr;
		}

		public byte ReadByte()
		{
			byte data;
			ReadBytes((byte*)&data, sizeof(byte));
			return data;
		}

		public short ReadShort()
		{
			short data;
			ReadBytes((byte*)&data, sizeof(short));
			return data;
		}

		public ushort ReadUShort()
		{
			ushort data;
			ReadBytes((byte*)&data, sizeof(ushort));
			return data;
		}

		public int ReadInt()
		{
			int data;
			ReadBytes((byte*)&data, sizeof(int));
			return data;
		}

		public uint ReadUInt()
		{
			uint data;
			ReadBytes((byte*)&data, sizeof(uint));
			return data;
		}
		public long ReadLong()
		{
			long data;
			ReadBytes((byte*)&data, sizeof(long));
			return data;
		}

		public ulong ReadULong()
		{
			ulong data;
			ReadBytes((byte*)&data, sizeof(ulong));
			return data;
		}

		public Vector3 ReadVector3()
		{
			return new Vector3(ReadFloat(), ReadFloat(), ReadFloat());
		}
		public Quaternion ReadQuaternion()
		{
			return new Quaternion(ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat());
		}

		public short ReadShortNetworkByteOrder()
		{
			short data;
			ReadBytes((byte*)&data, sizeof(short));
			return IsLittleEndian ? ByteSwap(data) : data;
		}

		public ushort ReadUShortNetworkByteOrder()
		{
			return (ushort)ReadShortNetworkByteOrder();
		}

		public int ReadIntNetworkByteOrder()
		{
			int data;
			ReadBytes((byte*)&data, sizeof(int));
			return IsLittleEndian ? ByteSwap(data) : data;
		}

		public uint ReadUIntNetworkByteOrder()
		{
			return (uint)ReadIntNetworkByteOrder();
		}

		public float ReadFloat()
		{
			UIntFloat uf = new UIntFloat();
			uf.intValue = (uint)ReadInt();
			return uf.floatValue;
		}

		internal const int k_MaxHuffmanSymbolLength = 6;
		public uint ReadPackedUInt(NetworkCompressionModel model)
		{
#if ENABLE_UNITY_COLLECTIONS_CHECKS
			AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
			FillBitBuffer();
			uint peekMask = (1u << k_MaxHuffmanSymbolLength) - 1u;
			uint peekBits = (uint)m_Context.m_BitBuffer & peekMask;
			ushort huffmanEntry = model.decodeTable[(int)peekBits];
			int symbol = huffmanEntry >> 8;
			int length = huffmanEntry & 0xFF;

			if (m_Context.m_BitIndex < length)
			{
				++m_Context.m_FailedReads;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
				throw new System.ArgumentOutOfRangeException();
#else
                return 0;
#endif
			}

			// Skip Huffman bits
			m_Context.m_BitBuffer >>= length;
			m_Context.m_BitIndex -= length;

			uint offset = model.bucketOffsets[symbol];
			int bits = model.bucketSizes[symbol];
			return ReadRawBitsInternal(bits) + offset;
		}
		void FillBitBuffer()
		{
			while (m_Context.m_BitIndex <= 56 && m_Context.m_ReadByteIndex < m_Length)
			{
				m_Context.m_BitBuffer |= (ulong)m_bufferPtr[m_Context.m_ReadByteIndex++] << m_Context.m_BitIndex;
				m_Context.m_BitIndex += 8;
			}
		}
		uint ReadRawBitsInternal(int numbits)
		{
#if ENABLE_UNITY_COLLECTIONS_CHECKS
			if (numbits < 0 || numbits > 32)
				throw new ArgumentOutOfRangeException("Invalid number of bits");
#endif
			if (m_Context.m_BitIndex < numbits)
			{
				++m_Context.m_FailedReads;
#if ENABLE_UNITY_COLLECTIONS_CHECKS
				throw new System.ArgumentOutOfRangeException("Not enough bits to read");
#else
                return 0;
#endif
			}
			uint res = (uint)(m_Context.m_BitBuffer & ((1UL << numbits) - 1UL));
			m_Context.m_BitBuffer >>= numbits;
			m_Context.m_BitIndex -= numbits;
			return res;
		}

		public int ReadPackedInt(NetworkCompressionModel model)
		{
			uint folded = ReadPackedUInt(model);
			return (int)(folded >> 1) ^ -(int)(folded & 1);    // Deinterleave values from [0, -1, 1, -2, 2...] to [..., -2, -1, -0, 1, 2, ...]
		}
		public float ReadPackedFloat(NetworkCompressionModel model)
		{
			return ReadPackedFloatDelta(0, model);
		}
		public int ReadPackedIntDelta(int baseline, NetworkCompressionModel model)
		{
			int delta = ReadPackedInt(model);
			return baseline - delta;
		}

		public uint ReadPackedUIntDelta(uint baseline, NetworkCompressionModel model)
		{
			uint delta = (uint)ReadPackedInt(model);
			return baseline - delta;
		}
		public float ReadPackedFloatDelta(float baseline, NetworkCompressionModel model)
		{
#if ENABLE_UNITY_COLLECTIONS_CHECKS
			AtomicSafetyHandle.CheckReadAndThrow(m_Safety);
#endif
			FillBitBuffer();
			if (ReadRawBitsInternal(1) == 0)
				return baseline;

			var bits = 32;
			UIntFloat uf = new UIntFloat();
			uf.intValue = ReadRawBitsInternal(bits);
			return uf.floatValue;
		}

		public unsafe string ReadString()
		{
			var byteLen = ReadUShort();

			if (byteLen == 0) return string.Empty;
			fixed (byte* data = new byte[byteLen])
			{
				ReadBytes( data, byteLen);
				return System.Text.Encoding.UTF8.GetString(data, byteLen);
			}
		}

		public unsafe NativeString64 ReadNativeString()
		{
			ushort length = ReadUShort();
			if (length > NativeString64.MaxLength)
#if ENABLE_UNITY_COLLECTIONS_CHECKS
				throw new InvalidOperationException("Invalid string length");
#else
                return default;
#endif
			NativeString64 str;
			byte* data = ((byte*)&str) + 2;
			ReadBytes(data, length);
			*(ushort*)&str = length;
			return str;
		}

		public unsafe NativeString64 ReadPackedStringDelta(NativeString64 baseline, NetworkCompressionModel model)
		{
			NativeString64 str;
			byte* data = ((byte*)&str) + 2;
			ushort baseLength = *((ushort*)&baseline);
			byte* baseData = ((byte*)&baseline) + 2;
			uint length = ReadPackedUIntDelta(baseLength, model);
			if (length > NativeString64.MaxLength)
#if ENABLE_UNITY_COLLECTIONS_CHECKS
				throw new InvalidOperationException("Invalid string length");
#else
                return default;
#endif
			if (length <= baseLength)
			{
				for (int i = 0; i < length; ++i)
					data[i] = (byte)ReadPackedUIntDelta(baseData[i], model);
			}
			else
			{
				for (int i = 0; i < baseLength; ++i)
					data[i] = (byte)ReadPackedUIntDelta(baseData[i], model);
				for (int i = baseLength; i < length; ++i)
					data[i] = (byte)ReadPackedUInt(model);
			}
			*(ushort*)&str = (ushort)length;
			return str;
		}
	}
}
