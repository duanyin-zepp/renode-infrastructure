//
// Copyright (c) 2010-2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.SPI
{
    // Minimal indirect-mode model of the MHS003S QSPI block used by the Dublin
    // bootloader to identify and configure its sensor NOR flash.
    public sealed class MHS003S_QSPI : IDoubleWordPeripheral, IWordPeripheral, IBytePeripheral, IKnownSize
    {
        public MHS003S_QSPI(IMachine machine)
        {
            Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            if(offset == DataPort)
            {
                return ReadData(4);
            }
            if(offset == Status)
            {
                return receiveFifo.Count > 0 ? 2u : 0u;
            }
            if(offset == InterruptStatus)
            {
                return interruptStatus;
            }
            if(offset == XipStatus)
            {
                return (uint)receiveFifo.Count << 24;
            }
            if(offset == Feature)
            {
                return 16; // TX FIFO depth, in 32-bit words.
            }
            return registers.TryGetValue(offset, out var value) ? value : 0;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset == InterruptStatus)
            {
                // Command-complete and FIFO status bits are write-one-to-clear.
                interruptStatus &= ~value;
                return;
            }
            if(offset == DataPort)
            {
                for(var i = 0; i < 4; i++)
                {
                    HandleTransmit((byte)(value >> (8 * i)));
                }
                return;
            }
            registers[offset] = value;
            if(offset == Command3)
            {
                StartCommand((byte)(value >> 24), registers.TryGetValue(Command2, out var count) ? count : 0);
            }
        }

        public ushort ReadWord(long offset) => offset == DataPort ? (ushort)ReadData(2) : (ushort)ReadDoubleWord(offset);
        public void WriteWord(long offset, ushort value)
        {
            if(offset == DataPort)
            {
                HandleTransmit((byte)value);
                HandleTransmit((byte)(value >> 8));
            }
            else
            {
                WriteDoubleWord(offset, value);
            }
        }
        public byte ReadByte(long offset) => offset == DataPort ? (byte)ReadData(1) : (byte)ReadDoubleWord(offset);
        public void WriteByte(long offset, byte value)
        {
            if(offset == DataPort) HandleTransmit(value);
            else WriteDoubleWord(offset, value);
        }

        public void Reset()
        {
            registers.Clear();
            receiveFifo.Clear();
            interruptStatus = 2; // TX FIFO empty
            currentCommand = 0;
            remainingTransmitBytes = 0;
        }

        public long Size => 0x1000;

        private void StartCommand(byte command, uint dataCount)
        {
            receiveFifo.Clear();
            currentCommand = command;
            remainingTransmitBytes = dataCount;
            interruptStatus |= 3; // command complete and TX FIFO empty
            switch(command)
            {
            case 0x9F: // JEDEC ID
                Enqueue(0xEF, 0x40, 0x18, 0, 0);
                break;
            case 0x05: // status register 1
                Enqueue(0);
                break;
            case 0x35: // status register 2
                Enqueue(0);
                break;
            case 0x15: // status register 3
                Enqueue(0);
                break;
            case 0xAB: // release from deep power down
            case 0x06: // write enable
            case 0x04: // write disable
            case 0x66: // reset enable
            case 0x99: // reset
            case 0xB9: // deep power down
                break;
            }
        }

        private void HandleTransmit(byte value)
        {
            if(remainingTransmitBytes > 0)
            {
                remainingTransmitBytes--;
                if(currentCommand == 0x01 && value != 0)
                {
                    // Sensor NOR QE is stored in status register 2 bit 1.
                }
            }
        }

        private uint ReadData(int count)
        {
            uint result = 0;
            for(var i = 0; i < count; i++)
            {
                result |= (uint)(receiveFifo.Count > 0 ? receiveFifo.Dequeue() : 0) << (8 * i);
            }
            return result;
        }

        private void Enqueue(params byte[] data)
        {
            foreach(var value in data) receiveFifo.Enqueue(value);
        }

        private const long Command2 = 0x08;
        private const long Command3 = 0x0C;
        private const long Status = 0x18;
        private const long InterruptStatus = 0x24;
        private const long Feature = 0x54;
        private const long XipStatus = 0x5C;
        private const long DataPort = 0x100;
        private readonly Dictionary<long, uint> registers = new Dictionary<long, uint>();
        private readonly Queue<byte> receiveFifo = new Queue<byte>();
        private uint interruptStatus;
        private uint remainingTransmitBytes;
        private byte currentCommand;
    }
}
