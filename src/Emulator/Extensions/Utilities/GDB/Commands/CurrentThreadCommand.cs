//
// Copyright (c) 2010-2025 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Utilities.GDB;

namespace Antmicro.Renode.Extensions.Utilities.GDB.Commands
{
    public class CurrentThreadCommand : Command, IMultithreadCommand
    {
        public CurrentThreadCommand(CommandsManager manager) : base(manager)
        {
        }

        [Execute("qC")]
        public PacketData Execute()
        {
            var threadId = FreeRTOSThreadProvider.TryGetCurrentThreadId(manager, out var freeRtosThreadId)
                ? freeRtosThreadId
                : manager.ManagedCpus[manager.Cpu];
            return new PacketData(string.Format("QC{0:x}", threadId));
        }
    }
}
