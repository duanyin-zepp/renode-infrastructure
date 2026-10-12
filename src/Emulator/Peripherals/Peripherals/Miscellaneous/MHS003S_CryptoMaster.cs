//
// Copyright (c) 2010-2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.Security.Cryptography;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // Minimal CryptoMaster model for the MHS003S boot firmware. The bootloader
    // uses its BA413 SHA-256 engine to verify the signed app partition.
    public sealed class MHS003S_CryptoMaster : IDoubleWordPeripheral, IKnownSize
    {
        public MHS003S_CryptoMaster(IMachine machine)
        {
            this.bus = machine.SystemBus;
            Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            if(registers.TryGetValue(offset, out var value))
            {
                return value;
            }
            return 0;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            registers[offset] = value;
            if(offset == Start && (value & StartAll) == StartAll)
            {
                ExecuteHash();
            }
            else if(offset == InterruptStatusClear)
            {
                registers[InterruptStatusRaw] = 0;
            }
        }

        public void Reset()
        {
            registers.Clear();
            hashMessage.Clear();
            registers[HardwarePresence] = HardwarePresentBA413;
            registers[BA413Capabilities] = 0x7F | HardwarePadding;
        }

        public long Size => 0x4000;

        private void ExecuteHash()
        {
            var inputAddress = registers.TryGetValue(FetchAddress, out var input) ? input : 0;
            var inputDescriptors = ReadDescriptors(inputAddress);
            var resumesHash = inputDescriptors.Exists(descriptor =>
                (descriptor.Tag & TagComponentMask) == Ba413Tag &&
                ((descriptor.Tag & TagDatatypeMask) >> 6) == HashStateDatatype);
            if(!resumesHash)
            {
                hashMessage.Clear();
            }
            foreach(var descriptor in inputDescriptors)
            {
                var tag = descriptor.Tag;
                if((tag & TagConfig) != 0 || (tag & TagDatatypeMask) != 0 || (tag & TagComponentMask) != Ba413Tag)
                {
                    continue;
                }
                var length = (int)(descriptor.Size & DescriptorLengthMask);
                var ignoredBytes = (int)((tag & TagIgnoredBytesMask) >> TagIgnoredBytesOffset);
                length = Math.Max(0, length - ignoredBytes);
                if(length > 0)
                {
                    var data = bus.ReadBytes(descriptor.Address, length);
                    if(!IsSha256Padding(data, hashMessage.Count))
                    {
                        hashMessage.AddRange(data);
                    }
                }
            }

            var digest = SHA256.HashData(hashMessage.ToArray());
            var outputAddress = registers.TryGetValue(PushAddress, out var output) ? output : 0;
            foreach(var descriptor in ReadDescriptors(outputAddress))
            {
                if((descriptor.Size & DescriptorDiscard) != 0)
                {
                    continue;
                }
                var length = Math.Min((int)(descriptor.Size & DescriptorLengthMask), digest.Length);
                if(length > 0)
                {
                    bus.WriteBytes(digest, descriptor.Address, 0, length);
                }
            }
            registers[Status] = 0;
            registers[InterruptStatusRaw] = 0;
        }

        private List<Descriptor> ReadDescriptors(uint address)
        {
            // Descriptors are 16-byte RV32 structures: address, next, size, tag.
            var result = new List<Descriptor>();
            for(var index = 0; index < MaximumDescriptors && address > 1; index++)
            {
                var bytes = bus.ReadBytes(address, DescriptorSize);
                var descriptor = new Descriptor(
                    ReadUInt32(bytes, 0),
                    ReadUInt32(bytes, 4),
                    ReadUInt32(bytes, 8),
                    ReadUInt32(bytes, 12));
                result.Add(descriptor);
                if(descriptor.Next <= 1)
                {
                    break;
                }
                address = descriptor.Next;
            }
            return result;
        }

        private static uint ReadUInt32(byte[] data, int offset)
        {
            return (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);
        }

        private static bool IsSha256Padding(byte[] data, int messageLength)
        {
            if(data.Length < Sha256MinimumPaddingSize || data.Length > Sha256MaximumPaddingSize || data[0] != 0x80)
            {
                return false;
            }
            for(var index = 1; index < data.Length - Sha256LengthFieldSize; index++)
            {
                if(data[index] != 0)
                {
                    return false;
                }
            }
            var messageBits = (ulong)messageLength * 8;
            for(var index = 0; index < Sha256LengthFieldSize; index++)
            {
                var shift = (Sha256LengthFieldSize - index - 1) * 8;
                if(data[data.Length - Sha256LengthFieldSize + index] != (byte)(messageBits >> shift))
                {
                    return false;
                }
            }
            return true;
        }

        private struct Descriptor
        {
            public Descriptor(uint address, uint next, uint size, uint tag)
            {
                Address = address;
                Next = next;
                Size = size;
                Tag = tag;
            }

            public uint Address { get; }
            public uint Next { get; }
            public uint Size { get; }
            public uint Tag { get; }
        }

        private readonly IBusController bus;
        private readonly Dictionary<long, uint> registers = new Dictionary<long, uint>();
        private readonly List<byte> hashMessage = new List<byte>();

        private const long FetchAddress = 0x00;
        private const long PushAddress = 0x10;
        private const long InterruptStatusRaw = 0x28;
        private const long InterruptStatusClear = 0x30;
        private const long Start = 0x38;
        private const long Status = 0x3C;
        private const long HardwarePresence = 0x400;
        private const long BA413Capabilities = 0x40C;
        private const uint StartAll = 0x3;
        private const uint HardwarePresentBA413 = 1 << 4;
        private const uint HardwarePadding = 1 << 16;
        private const uint Ba413Tag = 3;
        private const uint TagComponentMask = 0xF;
        private const uint TagDatatypeMask = 3 << 6;
        private const uint HashStateDatatype = 1;
        private const uint TagConfig = 1 << 4;
        private const uint TagIgnoredBytesMask = 0x1F << 8;
        private const int TagIgnoredBytesOffset = 8;
        private const uint DescriptorLengthMask = 0x0FFFFFFF;
        private const uint DescriptorDiscard = 1 << 30;
        private const int DescriptorSize = 16;
        private const int MaximumDescriptors = 64;
        private const int Sha256MinimumPaddingSize = 9;
        private const int Sha256MaximumPaddingSize = 72;
        private const int Sha256LengthFieldSize = 8;
    }
}
