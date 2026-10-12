//
// Copyright (c) 2010-2026 Antmicro
// This file is licensed under the MIT License. Full license text is in licenses/MIT.txt.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.UART
{
    // Minimal MHS003S sensor UART used by the Dublin bootloader console.
    // Its FIFO status register is at 0x74 (unlike a standard 16550 UART).
    public class MHS003S_UART : UARTBase, IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral, IKnownSize
    {
        public MHS003S_UART(IMachine machine) : base(machine)
        {
            registers = new byte[Size];
            Reset();
        }

        public byte ReadByte(long offset)
        {
            return (byte)(ReadRegister(offset & ~3L) >> (int)((offset & 3) * 8));
        }

        public void WriteByte(long offset, byte value)
        {
            if(IsFifoStatus(offset & ~3L))
            {
                return;
            }
            if((offset & ~3L) == TransmitHoldingRegister)
            {
                TransmitCharacter(value);
                return;
            }

            var shift = (int)((offset & 3) * 8);
            var mask = 0xFFu << shift;
            WriteRegister(offset & ~3L, (ReadRegister(offset & ~3L) & ~mask) | ((uint)value << shift));
        }

        public ushort ReadWord(long offset)
        {
            return (ushort)(ReadRegister(offset & ~3L) >> (int)((offset & 2) * 8));
        }

        public void WriteWord(long offset, ushort value)
        {
            if(IsFifoStatus(offset & ~3L))
            {
                return;
            }
            if((offset & ~3L) == TransmitHoldingRegister)
            {
                TransmitCharacter((byte)value);
                return;
            }

            var shift = (int)((offset & 2) * 8);
            var mask = 0xFFFFu << shift;
            WriteRegister(offset & ~3L, (ReadRegister(offset & ~3L) & ~mask) | ((uint)value << shift));
        }

        public uint ReadDoubleWord(long offset)
        {
            return ReadRegister(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(IsFifoStatus(offset))
            {
                return;
            }
            if(offset == TransmitHoldingRegister)
            {
                TransmitCharacter((byte)value);
                return;
            }
            WriteRegister(offset, value);
        }

        public override void Reset()
        {
            base.Reset();
            if(registers != null)
            {
                System.Array.Clear(registers, 0, registers.Length);
            }
        }

        public override Bits StopBits => Bits.One;
        public override Parity ParityBit => Parity.None;
        public override uint BaudRate => 115200;
        public long Size => 0x1000;

        protected override void CharWritten()
        {
            // The bootloader only transmits diagnostics; receive support is not needed.
        }

        protected override void QueueEmptied()
        {
        }

        private uint ReadRegister(long offset)
        {
            if(IsFifoStatus(offset))
            {
                return FifoStatusTransmitEmpty | FifoStatusTransmitComplete;
            }
            if(offset < 0 || offset + 3 >= registers.Length)
            {
                return 0;
            }

            return (uint)(registers[offset] | registers[offset + 1] << 8 | registers[offset + 2] << 16 | registers[offset + 3] << 24);
        }

        private void WriteRegister(long offset, uint value)
        {
            if(offset < 0 || offset + 3 >= registers.Length)
            {
                return;
            }

            registers[offset] = (byte)value;
            registers[offset + 1] = (byte)(value >> 8);
            registers[offset + 2] = (byte)(value >> 16);
            registers[offset + 3] = (byte)(value >> 24);
        }

        private static bool IsFifoStatus(long offset)
        {
            return offset == FifoStatusRegister;
        }

        private const long TransmitHoldingRegister = 0x00;
        private const long FifoStatusRegister = 0x74;
        private const uint FifoStatusTransmitEmpty = 0x04;
        private const uint FifoStatusTransmitComplete = 0x08;

        private readonly byte[] registers;
    }
}
