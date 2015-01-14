﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Text;
using Npgsql.Messages;
using NpgsqlTypes;
using System.Data;

namespace Npgsql.TypeHandlers
{
    /// <summary>
    /// Handler for the PostgreSQL bit string type.
    /// Note that for BIT(1), this handler will return a bool by default, to align with SQLClient
    /// (see discussion https://github.com/npgsql/npgsql/pull/362#issuecomment-59622101).
    /// </summary>
    /// <remarks>
    /// http://www.postgresql.org/docs/9.4/static/datatype-bit.html
    /// </remarks>
    [TypeMapping("varbit", NpgsqlDbType.Varbit, typeof(BitArray))]
    [TypeMapping("bit", NpgsqlDbType.Bit)]
    internal class BitStringHandler : TypeHandler,
        IChunkingTypeReader<BitArray>, IChunkingTypeWriter,
        ISimpleTypeReader<bool>
    {
        NpgsqlBuffer _buf;
        int _len;
        bool _bool;
        BitArray _bitArray;
        int _pos;

        internal override Type GetFieldType(FieldDescription fieldDescription)
        {
            return fieldDescription != null && fieldDescription.TypeModifier == 1 ? typeof (bool) : typeof(BitArray);
        }

        internal override Type GetProviderSpecificFieldType(FieldDescription fieldDescription)
        {
            return GetFieldType(fieldDescription);
        }

        internal override object ReadValueAsObject(DataRowMessage row, FieldDescription fieldDescription)
        {
            var result = fieldDescription.TypeModifier == 1
                ? (object)((ISimpleTypeReader<bool>) this).Read(row.Buffer, fieldDescription, row.ColumnLen)
                : Read<BitArray>(row, fieldDescription, row.ColumnLen);

            row.PosInColumn += row.ColumnLen;
            return result;
        }

        internal override object ReadPsvAsObject(DataRowMessage row, FieldDescription fieldDescription)
        {
            return ReadValueAsObject(row, fieldDescription);
        }

        #region Read

        bool ISimpleTypeReader<bool>.Read(NpgsqlBuffer buf, FieldDescription fieldDescription, int len)
        {
            if (fieldDescription.TypeModifier != 1) {
                throw new InvalidCastException(String.Format("Can't convert a BIT({0}) type to bool, only BIT(1)", fieldDescription.TypeModifier));
            }

            var bitLen = buf.ReadInt32();
            Contract.Assume(bitLen == 1);
            var b = buf.ReadByte();
            return (b & 128) != 0;
        }

        public void PrepareRead(NpgsqlBuffer buf, FieldDescription fieldDescription, int len)
        {
            _buf = buf;
            _pos = -1;
            _len = len - 4;   // Subtract leading bit length field
        }

        /// <summary>
        /// Reads a BitArray from a binary PostgreSQL value. First 32-bit big endian length,
        /// then the data in big-endian. Zero-padded low bits in the end if length is not multiple of 8.
        /// </summary>
        public bool Read(out BitArray result)
        {
            if (_pos == -1)
            {
                if (_buf.ReadBytesLeft < 4)
                {
                    result = null;
                    return false;
                }
                var numBits = _buf.ReadInt32();
                _bitArray = new BitArray(numBits);
                _pos = 0;
            }

            var bitNo = _pos * 8;
            var maxBytes = _pos + Math.Min(_len - _pos - 1, _buf.ReadBytesLeft);
            for (; _pos < maxBytes; _pos++)
            {
                var chunk = _buf.ReadByte();
                _bitArray[bitNo++] = (chunk & (1 << 7)) != 0;
                _bitArray[bitNo++] = (chunk & (1 << 6)) != 0;
                _bitArray[bitNo++] = (chunk & (1 << 5)) != 0;
                _bitArray[bitNo++] = (chunk & (1 << 4)) != 0;
                _bitArray[bitNo++] = (chunk & (1 << 3)) != 0;
                _bitArray[bitNo++] = (chunk & (1 << 2)) != 0;
                _bitArray[bitNo++] = (chunk & (1 << 1)) != 0;
                _bitArray[bitNo++] = (chunk & (1 << 0)) != 0;
            }

            if (_pos < _len - 1)
            {
                result = null;
                return false;
            }

            if (bitNo < _bitArray.Length)
            {
                var remainder = _bitArray.Length - bitNo;
                var lastChunk = _buf.ReadByte();
                for (var i = 7; i >= 8 - remainder; i--)
                {
                    _bitArray[bitNo++] = (lastChunk & (1 << i)) != 0;
                }
            }

            result = _bitArray;
            return true;
        }

        #endregion

        #region Write

        int IChunkingTypeWriter.GetLength(object value)
        {
            var asBitArray = value as BitArray;
            if (asBitArray != null)
                return 4 + (asBitArray.Length + 7) / 8;

            if (value is bool)
                return 5;

            var asString = value as string;
            if (asString != null)
                return 4 + (asString.Length + 7) / 8;

            throw new InvalidCastException("Expected BitArray, bool or string");
        }

        public void PrepareWrite(NpgsqlBuffer buf, object value)
        {
            _buf = buf;
            _pos = -1;

            var bitArray = value as BitArray;
            if (bitArray != null)
            {
                _bitArray = (BitArray)value;
            }
            else if (value is bool)
            {
                _bool = (bool) value;
            }
            else throw new InvalidCastException();
        }

        public bool Write(out byte[] directBuf)
        {
            directBuf = null;

            if (_bitArray != null)
            {
                if (_pos < 0)
                {
                    // Initial bitlength byte
                    if (_buf.WriteSpaceLeft < 4)
                        return false;
                    _buf.WriteInt32(_bitArray.Length);
                    _pos = 0;
                }
                var byteLen = (_bitArray.Length + 7) / 8;
                var endPos = _pos + Math.Min(byteLen - _pos, _buf.WriteSpaceLeft);
                for (; _pos < endPos; _pos++)
                {
                    var bitPos = _pos * 8;
                    var b = 0;
                    for (var i = 0; i < Math.Min(8, _bitArray.Length - bitPos); i++)
                        b += (_bitArray[bitPos + i] ? 1 : 0) << (8 - i -1);
                    _buf.WriteByte((byte)b);
                }

                if (_pos < byteLen) { return false; }

                _buf = null;
                _bitArray = null;
                return true;
            }

            // Bool
            if (_buf.WriteSpaceLeft < 5)
                return false;
            _buf.WriteInt32(1);
            _buf.WriteByte(_bool ? (byte)0x80 : (byte)0);
            return true;

            /*
            if (value is string)
            {
                string str = (string)value;
                if (!System.Text.RegularExpressions.Regex.IsMatch(str, "^[01]*$"))
                    throw new InvalidCastException("Cannot interpret as bitstring: " + str);

                buf.EnsuredWriteInt32((((string)value).Length + 7) / 8);
                for (var i = 0; i < str.Length / 8; i += 8)
                {
                    var b = 0;
                    b += (str[i + 0] - '0') << 7;
                    b += (str[i + 1] - '0') << 6;
                    b += (str[i + 2] - '0') << 5;
                    b += (str[i + 3] - '0') << 4;
                    b += (str[i + 4] - '0') << 3;
                    b += (str[i + 5] - '0') << 2;
                    b += (str[i + 6] - '0') << 1;
                    b += (str[i + 7] - '0');
                    buf.EnsuredWriteByte((byte)b);
                }
                int lastByte = 0;
                int mask = 0x80;
                for (int i = str.Length & ~7; i < str.Length; i++)
                {
                    if (str[i] == '1')
                        lastByte |= mask;
                    mask >>= 1;
                }
                if (mask != 0x80)
                    buf.EnsuredWriteByte((byte)lastByte);
            }
            */
        }

        #endregion
    }

    /// <summary>
    /// A special handler for arrays of bit strings.
    /// Differs from the standard array handlers in that it returns arrays of bool for BIT(1) and arrays
    /// of BitArray otherwise (just like the scalar BitStringHandler does).
    /// </summary>
    internal class BitStringArrayHandler : ArrayHandler
    {
        internal override Type GetElementFieldType(FieldDescription fieldDescription)
        {
            return fieldDescription.TypeModifier == 1 ? typeof(bool) : typeof(BitArray);
        }

        internal override Type GetElementPsvType(FieldDescription fieldDescription)
        {
            return GetElementFieldType(fieldDescription);
        }

        public BitStringArrayHandler(BitStringHandler elementHandler, char textDelimiter)
            : base(elementHandler, textDelimiter) {}

        internal override object ReadValueAsObject(DataRowMessage row, FieldDescription fieldDescription)
        {
            return fieldDescription.TypeModifier == 1
                ? (object)Read<bool>(row, fieldDescription, row.ColumnLen)
                :         Read<BitArray>(row, fieldDescription, row.ColumnLen);
        }

        internal override object ReadPsvAsObject(DataRowMessage row, FieldDescription fieldDescription)
        {
            return ReadValueAsObject(row, fieldDescription);
        }
    }
}
