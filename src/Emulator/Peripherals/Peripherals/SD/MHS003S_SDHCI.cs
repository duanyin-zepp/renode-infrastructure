//
// Copyright (c) 2010-2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.SD
{
    // Minimal MHS003S SDHCI host model for boot-time eMMC probing. It advertises
    // the host capabilities used by the firmware and completes commands with an
    // error because no card is attached.
    public sealed class MHS003S_SDHCI : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral, IKnownSize
    {
        public MHS003S_SDHCI(IMachine machine)
        {
            Reset();
        }

        public byte ReadByte(long offset) => (byte)(ReadRegister(offset & ~3) >> (int)((offset & 3) * 8));
        public ushort ReadWord(long offset)
        {
            if(offset == HostVersion)
            {
                return 3; // SDHCI 4.0
            }
            return (ushort)(ReadRegister(offset & ~3) >> (int)((offset & 2) * 8));
        }
        public uint ReadDoubleWord(long offset) => ReadRegister(offset);

        public void WriteByte(long offset, byte value) => WriteRegister(offset & ~3, (uint)value << (int)((offset & 3) * 8), 0xFFu << (int)((offset & 3) * 8));
        public void WriteWord(long offset, ushort value)
        {
            if(offset == Command)
            {
                CompleteCommandWithoutCard();
                return;
            }
            WriteRegister(offset & ~3, (uint)value << (int)((offset & 2) * 8), 0xFFFFu << (int)((offset & 2) * 8));
        }
        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset == InterruptStatus)
            {
                interruptStatus &= ~value; // write-one-to-clear
                return;
            }
            if(offset == Command)
            {
                CompleteCommandWithoutCard();
                return;
            }
            WriteRegister(offset, value, uint.MaxValue);
        }

        public void Reset()
        {
            registers.Clear();
            interruptStatus = 0;
        }

        public long Size => 0x1000;

        private uint ReadRegister(long offset)
        {
            if(offset == InterruptStatus)
            {
                return interruptStatus;
            }
            if(offset == Capabilities)
            {
                return HostCapabilities;
            }
            if(offset == Capabilities1)
            {
                return 0;
            }
            if(offset == PresentState)
            {
                return 0; // no card inserted
            }
            return registers.TryGetValue(offset, out var value) ? value : 0;
        }

        private void WriteRegister(long offset, uint value, uint mask)
        {
            var previous = registers.TryGetValue(offset, out var oldValue) ? oldValue : 0;
            registers[offset] = (previous & ~mask) | (value & mask);
        }

        private void CompleteCommandWithoutCard()
        {
            interruptStatus |= InterruptError;
        }

        private const long PresentState = 0x24;
        private const long InterruptStatus = 0x30;
        private const long Capabilities = 0x40;
        private const long Capabilities1 = 0x44;
        private const long Command = 0x0E;
        private const long HostVersion = 0xFE;
        private const uint HostCapabilities = 0x01286400;
        private const uint InterruptError = 0x8000;

        private readonly Dictionary<long, uint> registers = new Dictionary<long, uint>();
        private uint interruptStatus;
    }
}
