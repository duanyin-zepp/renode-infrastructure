//
// Copyright (c) 2010-2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.SD
{
    // Minimal MHS003S SDHCI host model for boot-time eMMC probing. When enabled,
    // it responds to basic eMMC commands and returns an empty EXT_CSD/data area.
    public sealed class MHS003S_SDHCI : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral, IKnownSize
    {
        public MHS003S_SDHCI(IMachine machine)
        {
            this.machine = machine;
            IRQ = new GPIO();
            Reset();
        }

        public bool CardPresent { get; set; }
        public GPIO IRQ { get; }

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

        public void WriteByte(long offset, byte value)
        {
            if(offset == SoftwareReset)
            {
                // SDHCI reset bits clear as soon as the controller completes
                // the reset. The boot firmware polls this register.
                var resetRegister = SoftwareReset & ~3;
                var previous = registers.TryGetValue(resetRegister, out var current) ? current : 0;
                registers[resetRegister] = previous & ~(0xFFu << (int)((offset & 3) * 8));
                return;
            }
            WriteRegister(offset & ~3, (uint)value << (int)((offset & 3) * 8), 0xFFu << (int)((offset & 3) * 8));
        }
        public void WriteWord(long offset, ushort value)
        {
            if(offset == Command)
            {
                CompleteCommand(value);
                return;
            }
            WriteRegister(offset & ~3, (uint)value << (int)((offset & 2) * 8), 0xFFFFu << (int)((offset & 2) * 8));
        }
        public void WriteDoubleWord(long offset, uint value)
        {
            if(offset == InterruptStatus)
            {
                interruptStatus &= ~value; // write-one-to-clear
                UpdateInterrupts();
                return;
            }
            if(offset == Command)
            {
                CompleteCommand(value);
                return;
            }
            WriteRegister(offset, value, uint.MaxValue);
        }

        public void Reset()
        {
            registers.Clear();
            interruptStatus = 0;
            cardState = StateIdle;
            UpdateInterrupts();
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
                return CardPresent ? CardInsertedAndReady : 0;
            }
            return registers.TryGetValue(offset, out var value) ? value : 0;
        }

        private void WriteRegister(long offset, uint value, uint mask)
        {
            var previous = registers.TryGetValue(offset, out var oldValue) ? oldValue : 0;
            registers[offset] = (previous & ~mask) | (value & mask);
            if(offset == InterruptEnable || offset == SignalEnable)
            {
                UpdateInterrupts();
            }
        }

        private void CompleteCommand(uint commandValue)
        {
            if(!CardPresent)
            {
                interruptStatus |= InterruptError;
                UpdateInterrupts();
                return;
            }

            var command = (int)((commandValue >> 8) & 0x3F);
            var argument = registers.TryGetValue(Argument, out var commandArgument) ? commandArgument : 0;
            if(command == GoIdle)
            {
                cardState = StateIdle;
            }
            else if(command == SendOperationCondition)
            {
                cardState = StateReady;
            }
            else if(command == SetRelativeAddress || (command == SelectCard && argument == 0) || (command == SleepAwake && (argument & SleepRequest) != 0))
            {
                cardState = StateStandby;
            }
            else if(command == SelectCard || (command == SleepAwake && (argument & SleepRequest) == 0))
            {
                cardState = StateTransfer;
            }

            var response = command switch
            {
                SendOperationCondition => OperationCondition,
                SendStatus => ReadyForData | (cardState << CardStateShift),
                _ => ReadyForData | (cardState << CardStateShift)
            };

            registers[Response] = response;
            registers[Response + 4] = command == AllSendCid ? 0x12345678u : 0;
            registers[Response + 8] = command == AllSendCid ? 0x9ABCDEF0u : 0;
            registers[Response + 12] = command == AllSendCid ? 0x13579BDFu : 0;
            if(command == SendCsd)
            {
                // CSD v4, 512-byte read blocks. The firmware then requests
                // EXT_CSD, where the actual high-capacity size is reported.
                registers[Response] = 0;
                registers[Response + 4] = 0;
                registers[Response + 8] = 0x45000900;
                registers[Response + 12] = 0x00100000;
            }
            interruptStatus |= InterruptResponse;

            if((commandValue & CommandHasData) != 0)
            {
                TransferData(command);
                interruptStatus |= InterruptDataEnd;
            }
            if((commandValue & CommandResponseMask) == CommandResponseBusy)
            {
                interruptStatus |= InterruptDataEnd;
            }
            UpdateInterrupts();
        }

        private void UpdateInterrupts()
        {
            var interruptEnable = registers.TryGetValue(InterruptEnable, out var enabled) ? enabled : 0;
            var signalEnable = registers.TryGetValue(SignalEnable, out var signaled) ? signaled : 0;
            IRQ.Set((interruptStatus & interruptEnable & signalEnable) != 0);
        }

        private void TransferData(int command)
        {
            // The boot firmware reads EXT_CSD with ADMA. A zero-filled payload
            // is enough to let initialization identify an eMMC without relying
            // on uninitialized guest RAM; advertise Dublin's 64 GiB capacity.
            if((registers.TryGetValue(TransferMode, out var transferMode) && (transferMode & TransferRead) != 0) == false)
            {
                return;
            }

            var payload = new byte[512];
            if(command == SendExtendedCsd)
            {
                payload[192] = 8; // EXT_CSD_REV
                payload[196] = 1; // CARD_TYPE: legacy high-speed support
                const uint sectorCount = 0x08000000; // 64 GiB in 512-byte sectors
                payload[212] = unchecked((byte)sectorCount);
                payload[213] = unchecked((byte)(sectorCount >> 8));
                payload[214] = unchecked((byte)(sectorCount >> 16));
                payload[215] = unchecked((byte)(sectorCount >> 24));
            }

            if(!registers.TryGetValue(AdmaAddress, out var descriptorAddress))
            {
                return;
            }

            var payloadOffset = 0;
            for(var descriptorIndex = 0; descriptorIndex < MaxAdmaDescriptors; descriptorIndex++)
            {
                var descriptor = descriptorAddress + (uint)(descriptorIndex * AdmaDescriptorSize);
                var attributes = machine.SystemBus.ReadByte(descriptor);
                if((attributes & AdmaValid) == 0)
                {
                    break;
                }

                var length = (int)machine.SystemBus.ReadWord(descriptor + 2);
                if(length == 0)
                {
                    length = ushort.MaxValue + 1;
                }
                var destination = machine.SystemBus.ReadDoubleWord(descriptor + 4);
                var count = Math.Min(length, payload.Length - payloadOffset);
                for(var i = 0; i < count; i++)
                {
                    machine.SystemBus.WriteByte((ulong)destination + (uint)i, payload[payloadOffset + i]);
                }
                payloadOffset += count;
                if((attributes & AdmaEnd) != 0)
                {
                    break;
                }
                if(count == payload.Length)
                {
                    break;
                }
            }
        }

        private const long PresentState = 0x24;
        private const long InterruptStatus = 0x30;
        private const long InterruptEnable = 0x34;
        private const long SignalEnable = 0x38;
        private const long Capabilities = 0x40;
        private const long Capabilities1 = 0x44;
        private const long Command = 0x0E;
        private const long Response = 0x10;
        private const long TransferMode = 0x0C;
        private const long AdmaAddress = 0x58;
        private const long SoftwareReset = 0x2F;
        private const long Argument = 0x08;
        private const long HostVersion = 0xFE;
        private const uint HostCapabilities = 0x01286400;
        private const uint InterruptError = 0x8000;
        private const uint InterruptResponse = 1;
        private const uint InterruptDataEnd = 1 << 1;
        private const uint CardInsertedAndReady = (1 << 16) | (1 << 17) | (1 << 18) | (1 << 20);
        private const uint CommandHasData = 1 << 5;
        private const uint CommandResponseMask = 0x3;
        private const uint CommandResponseBusy = 0x3;
        private const uint TransferRead = 1 << 4;
        private const uint OperationCondition = 0xC0FF8000;
        private const uint ReadyForData = 1 << 8;
        private const int CardStateShift = 9;
        private const uint SleepRequest = 1 << 15;
        private const uint StateIdle = 0;
        private const uint StateReady = 1;
        private const uint StateStandby = 3;
        private const uint StateTransfer = 4;
        private const int GoIdle = 0;
        private const int SendOperationCondition = 1;
        private const int AllSendCid = 2;
        private const int SetRelativeAddress = 3;
        private const int SelectCard = 7;
        private const int SendCsd = 9;
        private const int SleepAwake = 5;
        private const int SendStatus = 13;
        private const int SendExtendedCsd = 8;
        private const int AdmaDescriptorSize = 8;
        private const int MaxAdmaDescriptors = 64;
        private const byte AdmaValid = 1;
        private const byte AdmaEnd = 1 << 1;

        private readonly IMachine machine;
        private readonly Dictionary<long, uint> registers = new Dictionary<long, uint>();
        private uint interruptStatus;
        private uint cardState;
    }
}
