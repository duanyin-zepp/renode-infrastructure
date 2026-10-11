//
// Copyright (c) 2010-2025 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System.Linq;

using Antmicro.Renode.Utilities.GDB;

namespace Antmicro.Renode.Extensions.Utilities.GDB.Commands
{
    public class ThreadContextCommand : Command, IMultithreadCommand
    {
        public ThreadContextCommand(CommandsManager manager) : base(manager)
        {
        }

        [Execute("Hg")]
        public PacketData Execute(
            [Argument(Encoding = ArgumentAttribute.ArgumentEncoding.ThreadId)] PacketThreadId threadId)
        {
            var cpuId = threadId.ProcessId ?? threadId.ThreadId;
            if(cpuId == PacketThreadId.All)
            {
                // Choosing all isn't currently supported.
                return PacketData.ErrorReply(Error.OperationNotPermitted);
            }

            if(cpuId == PacketThreadId.Any)
            {
                manager.SelectedFreeRTOSThreadId = null;
                manager.SelectCpuForDebugging(manager.ManagedCpus[PacketThreadId.Any]);
                return PacketData.Success;
            }

            // FreeRTOS tasks all execute on the same CPU. GDB may select one
            // of their task IDs, but that does not change the emulated CPU.
            if(FreeRTOSThreadProvider.TryGetTaskByThreadId(manager, unchecked((int)threadId.ThreadId), out _))
            {
                manager.SelectedFreeRTOSThreadId = (uint)threadId.ThreadId;
                return PacketData.Success;
            }

            if(cpuId < 0 || !manager.ManagedCpus.GdbCpuIds.Contains((uint)cpuId))
            {
                return PacketData.ErrorReply();
            }

            manager.SelectedFreeRTOSThreadId = null;
            manager.SelectCpuForDebugging(manager.ManagedCpus[cpuId]);
            return PacketData.Success;
        }
    }
}
