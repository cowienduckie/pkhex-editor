﻿using System;
using System.Diagnostics;
using System.IO;
using static System.Buffers.Binary.BinaryPrimitives;

namespace PKHeX.Core
{
    /// <summary>
    /// Block of <see cref="Data"/> obtained from a <see cref="SwishCrypto"/> encrypted block storage binary.
    /// </summary>
    public sealed class SCBlock
    {
        /// <summary>
        /// Used to encrypt the rest of the block.
        /// </summary>
        public readonly uint Key;

        /// <summary>
        /// What kind of block is it?
        /// </summary>
        public SCTypeCode Type { get; private set; }

        /// <summary>
        /// For <see cref="SCTypeCode.Array"/>: What kind of array is it?
        /// </summary>
        public readonly SCTypeCode SubType;

        /// <summary>
        /// Decrypted data for this block.
        /// </summary>
        public readonly byte[] Data;

        /// <summary>
        /// Changes the block's Boolean type. Will throw if the old / new <see cref="Type"/> is not boolean.
        /// </summary>
        /// <param name="value">New boolean type to set.</param>
        /// <remarks>Will throw if the requested block state changes are incorrect.</remarks>
        public void ChangeBooleanType(SCTypeCode value)
        {
            if (Type is not (SCTypeCode.Bool1 or SCTypeCode.Bool2) || value is not (SCTypeCode.Bool1 or SCTypeCode.Bool2))
                throw new InvalidOperationException($"Cannot change {Type} to {value}.");
            Type = value;
        }

        /// <summary>
        /// Replaces the current <see cref="Data"/> with a same-sized array <see cref="value"/>.
        /// </summary>
        /// <param name="value">New array to load (copy from).</param>
        /// <remarks>Will throw if the requested block state changes are incorrect.</remarks>
        public void ChangeData(ReadOnlySpan<byte> value)
        {
            if (value.Length != Data.Length)
                throw new InvalidOperationException($"Cannot change size of {Type} block from {Data.Length} to {value.Length}.");
            value.CopyTo(Data);
        }

        /// <summary>
        /// Creates a new block reference to indicate a boolean value via the <see cref="type"/> (no data).
        /// </summary>
        /// <param name="key">Hash key</param>
        /// <param name="type">Value the block has</param>
        internal SCBlock(uint key, SCTypeCode type) : this(key, type, Array.Empty<byte>())
        {
        }

        /// <summary>
        /// Creates a new block reference to indicate an object or single primitive value.
        /// </summary>
        /// <param name="key">Hash key</param>
        /// <param name="type">Type of data that can be read</param>
        /// <param name="arr">Backing byte array to interpret as a typed value</param>
        internal SCBlock(uint key, SCTypeCode type, byte[] arr)
        {
            Key = key;
            Type = type;
            Data = arr;
        }

        /// <summary>
        /// Creates a new block reference to indicate an array of primitive values.
        /// </summary>
        /// <param name="key">Hash key</param>
        /// <param name="arr">Backing byte array to read primitives from</param>
        /// <param name="subType">Primitive value type</param>
        internal SCBlock(uint key, byte[] arr, SCTypeCode subType)
        {
            Key = key;
            Type = SCTypeCode.Array;
            Data = arr;
            SubType = subType;
        }

        /// <summary> Indiciates if the block represents a single primitive value. </summary>
        public bool HasValue() => Type > SCTypeCode.Array;

        /// <summary> Returns a boxed reference to a single primitive value. Throws an exception if the block does not represent a single primitive value. </summary>
        public object GetValue() => Type.GetValue(Data);

        /// <summary> Sets a boxed primitive value to the block data. Throws an exception if the block does not represent a single primitive value, or if the primitive type does not match. </summary>
        /// <param name="value">Boxed primitive value to be set to the block</param>
        public void SetValue(object value) => Type.SetValue(Data, value);

        /// <summary>
        /// Creates a deep copy of the block.
        /// </summary>
        public SCBlock Clone()
        {
            if (Data.Length == 0)
                return new SCBlock(Key, Type);
            var clone = Data.AsSpan().ToArray();
            if (SubType == 0)
                return new SCBlock(Key, Type, clone);
            return new SCBlock(Key, clone, SubType);
        }

        /// <summary>
        /// Encrypts the <see cref="Data"/> according to the <see cref="Type"/> and <see cref="SubType"/>.
        /// </summary>
        public void WriteBlock(BinaryWriter bw)
        {
            var xk = new SCXorShift32(Key);
            bw.Write(Key);
            bw.Write((byte)((byte)Type ^ xk.Next()));

            if (Type == SCTypeCode.Object)
            {
                bw.Write((uint)Data.Length ^ xk.Next32());
            }
            else if (Type == SCTypeCode.Array)
            {
                var entries = (uint)(Data.Length / SubType.GetTypeSize());
                bw.Write(entries ^ xk.Next32());
                bw.Write((byte)((byte)SubType ^ xk.Next()));
            }

            foreach (var b in Data)
                bw.Write((byte)(b ^ xk.Next()));
        }

        /// <summary>
        /// Reads a new <see cref="SCBlock"/> object from the <see cref="data"/>, determining the <see cref="Type"/> and <see cref="SubType"/> during read.
        /// </summary>
        /// <param name="data">Decrypted data</param>
        /// <param name="offset">Offset the block is to be read from (modified to offset by the amount of bytes consumed).</param>
        /// <returns>New object containing all info for the block.</returns>
        public static SCBlock ReadFromOffset(ReadOnlySpan<byte> data, ref int offset)
        {
            // Get key, initialize xorshift to decrypt
            var key = ReadUInt32LittleEndian(data[offset..]);
            offset += 4;
            var xk = new SCXorShift32(key);

            // Parse the block's type
            var type = (SCTypeCode)(data[offset++] ^ xk.Next());

            switch (type)
            {
                case SCTypeCode.Bool1:
                case SCTypeCode.Bool2:
                case SCTypeCode.Bool3:
                    // Block types are empty, and have no extra data.
                    Debug.Assert(type != SCTypeCode.Bool3); // invalid type, haven't seen it used yet
                    return new SCBlock(key, type);

                case SCTypeCode.Object: // Cast raw bytes to Object
                {
                    var num_bytes = ReadInt32LittleEndian(data[offset..]) ^ (int)xk.Next32();
                    offset += 4;
                    var arr = data.Slice(offset, num_bytes).ToArray();
                    offset += num_bytes;
                    for (int i = 0; i < arr.Length; i++)
                        arr[i] ^= (byte)xk.Next();

                    return new SCBlock(key, type, arr);
                }

                case SCTypeCode.Array: // Cast raw bytes to SubType[]
                {
                    var num_entries = ReadInt32LittleEndian(data[offset..]) ^ (int)xk.Next32();
                    offset += 4;
                    var sub = (SCTypeCode)(data[offset++] ^ xk.Next());

                    var num_bytes = num_entries * sub.GetTypeSize();
                    var arr = data.Slice(offset, num_bytes).ToArray();
                    offset += num_bytes;
                    for (int i = 0; i < arr.Length; i++)
                        arr[i] ^= (byte)xk.Next();
#if DEBUG
                    Debug.Assert(sub > SCTypeCode.Array || (sub == SCTypeCode.Bool3 && Array.TrueForAll(arr, z => z <= 2)) || Array.TrueForAll(arr, z => z <= 1));
#endif
                    return new SCBlock(key, arr, sub);
                }

                default: // Single Value Storage
                {
                    var num_bytes = type.GetTypeSize();
                    var arr = data.Slice(offset, num_bytes).ToArray();
                    offset += num_bytes;
                    for (int i = 0; i < arr.Length; i++)
                        arr[i] ^= (byte)xk.Next();
                    return new SCBlock(key, type, arr);
                }
            }
        }

        public void CopyFrom(SCBlock other)
        {
            if (Type.IsBoolean())
                ChangeBooleanType(other.Type);
            else
                ChangeData(other.Data);
        }
    }
}
