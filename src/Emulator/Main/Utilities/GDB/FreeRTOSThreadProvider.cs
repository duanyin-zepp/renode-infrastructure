//
// Copyright (c) 2010-2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;

using Endianess = ELFSharp.ELF.Endianess;

namespace Antmicro.Renode.Utilities.GDB
{
    internal static class FreeRTOSThreadProvider
    {
        public static bool IsFreeRTOSAvailable(CommandsManager manager)
        {
            return TryGetSymbolAddress(manager, CurrentTcbSymbol, out _)
                && TryGetSymbolAddress(manager, ReadyTasksListsSymbol, out _);
        }

        public static IReadOnlyList<FreeRTOSTaskInfo> GetTasks(CommandsManager manager)
        {
            var tasksByTcb = new Dictionary<uint, FreeRTOSTaskInfo>();

            if(TryGetCurrentTcb(manager, out var currentTcb))
            {
                AddTask(tasksByTcb, CreateTask(manager, currentTcb));
            }

            foreach(var task in ReadReadyTasks(manager))
            {
                AddTask(tasksByTcb, task);
            }

            foreach(var symbolName in StaticListSymbols)
            {
                if(TryGetSymbolAddress(manager, symbolName, out var listAddress))
                {
                    foreach(var task in ReadTaskList(manager, listAddress))
                    {
                        AddTask(tasksByTcb, task);
                    }
                }
            }

            foreach(var symbolName in ListPointerSymbols)
            {
                if(TryGetSymbolAddress(manager, symbolName, out var pointerAddress)
                    && TryReadUInt32(manager, pointerAddress, out var listAddress)
                    && listAddress != 0)
                {
                    foreach(var task in ReadTaskList(manager, listAddress))
                    {
                        AddTask(tasksByTcb, task);
                    }
                }
            }

            var result = tasksByTcb.Values.OrderBy(x => x.TCBAddress).ToList();
            for(var i = 0; i < result.Count; i++)
            {
                result[i].ThreadId = CreateThreadId(result[i], i);
            }
            return result;
        }

        public static bool TryGetCurrentThreadId(CommandsManager manager, out uint threadId)
        {
            threadId = 0;
            if(!TryGetCurrentTcb(manager, out var currentTcb))
            {
                return false;
            }

            var currentTask = GetTasks(manager).FirstOrDefault(x => x.TCBAddress == currentTcb);
            if(currentTask == null)
            {
                return false;
            }

            threadId = currentTask.ThreadId;
            return true;
        }

        public static bool TryGetTaskByThreadId(CommandsManager manager, int threadId, out FreeRTOSTaskInfo task)
        {
            task = null;
            if(threadId <= 0)
            {
                return false;
            }

            var unsignedThreadId = (uint)threadId;
            task = GetTasks(manager).FirstOrDefault(x => x.ThreadId == unsignedThreadId);
            return task != null;
        }

        public static bool TryReadSavedGeneralRegister(CommandsManager manager, int registerNumber, out byte[] value)
        {
            value = null;
            if(!manager.SelectedFreeRTOSThreadId.HasValue || registerNumber < 0 || registerNumber > ProgramCounterRegisterNumber
                || !TryGetTaskByThreadId(manager, (int)manager.SelectedFreeRTOSThreadId.Value, out var task))
            {
                return false;
            }

            // The current task's pxTopOfStack is only refreshed on a context switch.
            // Its live register values are already available directly from the CPU.
            if(TryGetCurrentTcb(manager, out var currentTcb) && currentTcb == task.TCBAddress)
            {
                return false;
            }

            if(!TryReadUInt32(manager, task.TCBAddress, out var savedStackPointer))
            {
                return false;
            }

            var stackWord = registerNumber == ProgramCounterRegisterNumber ? SavedMepcWord : (uint)registerNumber;
            if(!TryReadUInt32(manager, savedStackPointer + stackWord * WordSize, out var registerValue))
            {
                return false;
            }

            // pxPortInitialiseStack leaves the saved x2 slot zero because the
            // restore routine advances SP past the whole context frame instead.
            if(registerNumber == StackPointerRegisterNumber && registerValue == 0)
            {
                registerValue = savedStackPointer + ContextSizeBytes;
            }

            value = new byte[WordSize];
            for(var i = 0; i < value.Length; i++)
            {
                var byteIndex = manager.Cpu.Endianness == Endianess.BigEndian ? value.Length - 1 - i : i;
                value[byteIndex] = (byte)(registerValue >> (i * 8));
            }
            return true;
        }

        private static IEnumerable<FreeRTOSTaskInfo> ReadReadyTasks(CommandsManager manager)
        {
            if(!TryGetSymbolAddress(manager, ReadyTasksListsSymbol, out var readyListsAddress))
            {
                yield break;
            }

            var priorityCount = GetPriorityCount(manager);
            for(var priority = 0; priority < priorityCount; priority++)
            {
                var listAddress = readyListsAddress + (uint)(priority * ListSize);
                foreach(var task in ReadTaskList(manager, listAddress))
                {
                    yield return task;
                }
            }
        }

        private static IEnumerable<FreeRTOSTaskInfo> ReadTaskList(CommandsManager manager, ulong listAddress)
        {
            if(!TryReadUInt32(manager, listAddress + ListNumberOfItemsOffset, out var numberOfItems)
                || numberOfItems == 0
                || numberOfItems > MaxTasksInList)
            {
                yield break;
            }

            var listEndAddress = listAddress + ListEndOffset;
            if(!TryReadUInt32(manager, listEndAddress + ListItemNextOffset, out var currentItem))
            {
                yield break;
            }

            var visitedItems = new HashSet<uint>();
            for(var i = 0; i < numberOfItems && currentItem != 0 && currentItem != listEndAddress; i++)
            {
                if(!visitedItems.Add(currentItem))
                {
                    yield break;
                }

                if(TryReadUInt32(manager, currentItem + ListItemOwnerOffset, out var tcbAddress)
                    && tcbAddress != 0
                    && IsReadable(manager, tcbAddress))
                {
                    yield return CreateTask(manager, tcbAddress);
                }

                if(!TryReadUInt32(manager, currentItem + ListItemNextOffset, out currentItem))
                {
                    yield break;
                }
            }
        }

        private static int GetPriorityCount(CommandsManager manager)
        {
            if(TryGetSymbolAddress(manager, TopUsedPrioritySymbol, out var topUsedPriorityAddress)
                && TryReadUInt32(manager, topUsedPriorityAddress, out var topUsedPriority)
                && topUsedPriority < MaxPriorities)
            {
                return (int)topUsedPriority + 1;
            }

            return DefaultPriorityCount;
        }

        private static bool TryGetCurrentTcb(CommandsManager manager, out uint currentTcb)
        {
            currentTcb = 0;
            return TryGetSymbolAddress(manager, CurrentTcbSymbol, out var currentTcbPointerAddress)
                && TryReadUInt32(manager, currentTcbPointerAddress, out currentTcb)
                && currentTcb != 0
                && IsReadable(manager, currentTcb);
        }

        private static FreeRTOSTaskInfo CreateTask(CommandsManager manager, uint tcbAddress)
        {
            return new FreeRTOSTaskInfo(tcbAddress, ReadTaskName(manager, tcbAddress));
        }

        private static void AddTask(IDictionary<uint, FreeRTOSTaskInfo> tasksByTcb, FreeRTOSTaskInfo task)
        {
            if(task == null || task.TCBAddress == 0 || tasksByTcb.ContainsKey(task.TCBAddress))
            {
                return;
            }

            tasksByTcb.Add(task.TCBAddress, task);
        }

        private static uint CreateThreadId(FreeRTOSTaskInfo task, int index)
        {
            if(task.TCBAddress <= MaxSignedGdbThreadId)
            {
                return task.TCBAddress;
            }

            return GeneratedThreadIdBase + (uint)index + 1;
        }

        private static string ReadTaskName(CommandsManager manager, uint tcbAddress)
        {
            foreach(var offset in TaskNameOffsetCandidates)
            {
                var bytes = new List<byte>();
                for(var i = 0; i < MaxTaskNameLength; i++)
                {
                    if(!TryReadByte(manager, tcbAddress + offset + (uint)i, out var value))
                    {
                        bytes.Clear();
                        break;
                    }
                    if(value == 0)
                    {
                        break;
                    }
                    if(value < 0x20 || value > 0x7e)
                    {
                        bytes.Clear();
                        break;
                    }
                    bytes.Add(value);
                }

                if(bytes.Count > 0)
                {
                    return Encoding.ASCII.GetString(bytes.ToArray());
                }
            }

            return $"Task@0x{tcbAddress:x8}";
        }

        private static bool TryGetSymbolAddress(CommandsManager manager, string symbolName, out ulong address)
        {
            address = 0;
            var context = manager.Cpu as ICPU;
            try
            {
                if(manager.Machine.SystemBus.TryGetAllSymbolAddresses(symbolName, out var addresses, context)
                    || manager.Machine.SystemBus.TryGetAllSymbolAddresses(symbolName, out addresses))
                {
                    var resolvedAddress = addresses.FirstOrDefault();
                    if(resolvedAddress != 0)
                    {
                        address = resolvedAddress;
                        return true;
                    }
                }
            }
            catch(Exception)
            {
            }
            return false;
        }

        private static bool TryReadUInt32(CommandsManager manager, ulong address, out uint value)
        {
            value = 0;
            if(!TryReadByte(manager, address, out var b0)
                || !TryReadByte(manager, address + 1, out var b1)
                || !TryReadByte(manager, address + 2, out var b2)
                || !TryReadByte(manager, address + 3, out var b3))
            {
                return false;
            }

            if(manager.Cpu.Endianness == Endianess.BigEndian)
            {
                value = ((uint)b0 << 24) | ((uint)b1 << 16) | ((uint)b2 << 8) | b3;
            }
            else
            {
                value = b0 | ((uint)b1 << 8) | ((uint)b2 << 16) | ((uint)b3 << 24);
            }
            return true;
        }

        private static bool TryReadByte(CommandsManager manager, ulong address, out byte value)
        {
            value = 0;
            if(!IsReadable(manager, address))
            {
                return false;
            }

            try
            {
                value = manager.Machine.SystemBus.ReadByte(address, context: manager.Cpu as IPeripheral);
                return true;
            }
            catch(Exception)
            {
                return false;
            }
        }

        private static bool IsReadable(CommandsManager manager, ulong address)
        {
            try
            {
                return manager.Machine.SystemBus.WhatIsAt(address, context: manager.Cpu as IPeripheral) != null;
            }
            catch(Exception)
            {
                return false;
            }
        }

        private const string CurrentTcbSymbol = "pxCurrentTCB";
        private const string ReadyTasksListsSymbol = "pxReadyTasksLists";
        private const string TopUsedPrioritySymbol = "uxTopUsedPriority";

        private const uint GeneratedThreadIdBase = 0x10000;
        private const uint MaxSignedGdbThreadId = 0x7fffffff;

        private const uint ListNumberOfItemsOffset = 0;
        private const uint ListEndOffset = 8;
        private const uint ListItemNextOffset = 4;
        private const uint ListItemOwnerOffset = 12;
        private const uint ListSize = 20;

        private const uint WordSize = 4;
        private const uint ContextSizeBytes = 72 * WordSize;
        private const uint SavedMepcWord = 65;
        private const int StackPointerRegisterNumber = 2;
        private const int ProgramCounterRegisterNumber = 32;

        private const int DefaultPriorityCount = 10;
        private const int MaxPriorities = 64;
        private const int MaxTasksInList = 256;
        private const int MaxTaskNameLength = 16;

        private static readonly uint[] TaskNameOffsetCandidates =
        {
            0x34, 0x38, 0x40, 0x48, 0x4c, 0x50, 0x5c,
        };

        private static readonly string[] StaticListSymbols =
        {
            "xDelayedTaskList1",
            "xDelayedTaskList2",
            "xPendingReadyList",
            "xSuspendedTaskList",
            "xTasksWaitingTermination",
        };

        private static readonly string[] ListPointerSymbols =
        {
            "pxDelayedTaskList",
            "pxOverflowDelayedTaskList",
        };
    }

    internal class FreeRTOSTaskInfo
    {
        public FreeRTOSTaskInfo(uint tcbAddress, string name)
        {
            TCBAddress = tcbAddress;
            Name = name;
        }

        public uint TCBAddress { get; }

        public string Name { get; }

        public uint ThreadId { get; set; }
    }
}
