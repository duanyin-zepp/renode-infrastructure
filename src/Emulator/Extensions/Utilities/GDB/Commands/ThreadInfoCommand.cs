//
// Copyright (c) 2010-2025 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Utilities.GDB;

namespace Antmicro.Renode.Extensions.Utilities.GDB.Commands
{
    public class ThreadInfoCommand : Command, IMultithreadCommand
    {
        public ThreadInfoCommand(CommandsManager manager) : base(manager)
        {
        }

        [Execute("qfThreadInfo")]
        public PacketData GetFirstThreadInfo()
        {
            currentThreadIndex = 0;
            return GetNextThreadInfo();
        }

        [Execute("qsThreadInfo")]
        public PacketData GetNextThreadInfo()
        {
            var threadIds = GetThreadIds();
            
            if(currentThreadIndex >= threadIds.Count)
            {
                return new PacketData("l");
            }

            var response = new List<string>();
            int count = 0;
            while(currentThreadIndex < threadIds.Count && count < 32)
            {
                response.Add(threadIds[currentThreadIndex].ToString("x"));
                currentThreadIndex++;
                count++;
            }

            return new PacketData("m" + string.Join(",", response));
        }

        [Execute("qL")]
        public PacketData GetLegacyThreadInfo(
            [Argument(Encoding = ArgumentAttribute.ArgumentEncoding.String)] string data)
        {
            if(data.Length < 3 || !int.TryParse(data.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var requestedCount))
            {
                return PacketData.ErrorReply();
            }

            var startFlag = data[0];
            var echoThread = NormalizeLegacyThreadId(data.Length > 3 ? data.Substring(3) : string.Empty);
            var threadIds = GetThreadIds();
            var startIndex = 0;
            if(startFlag == '0')
            {
                if(TryParseLegacyThreadId(echoThread, out var nextThreadId))
                {
                    var index = threadIds.FindIndex(x => x == nextThreadId);
                    startIndex = index >= 0 ? index + 1 : threadIds.Count;
                }
            }
            else if(startFlag != '1')
            {
                return PacketData.ErrorReply();
            }

            var batch = threadIds.Skip(startIndex).Take(requestedCount).ToList();
            var done = batch.Count == 0 || startIndex + batch.Count >= threadIds.Count ? '1' : '0';
            var response = new StringBuilder();
            response.Append("qM");
            response.Append(batch.Count.ToString("x2"));
            response.Append(done);
            response.Append(echoThread);
            foreach(var threadId in batch)
            {
                response.Append(FormatLegacyThreadId(threadId));
            }

            return new PacketData(response.ToString());
        }

        [Execute("qP")]
        public PacketData GetLegacyThreadExtraInfo(
            [Argument(Encoding = ArgumentAttribute.ArgumentEncoding.String)] string data)
        {
            if(data.Length < 8 + LegacyThreadIdWidth
                || !uint.TryParse(data.Substring(0, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var requestedMask))
            {
                return PacketData.ErrorReply();
            }

            var threadRef = NormalizeLegacyThreadId(data.Substring(8));
            if(!TryParseLegacyThreadId(threadRef, out var threadId))
            {
                return PacketData.ErrorReply();
            }

            var exists = TryGetThreadName(threadId, out var threadName);
            var responseMask = requestedMask & SupportedLegacyThreadInfoMask;
            if(responseMask == 0)
            {
                responseMask = LegacyThreadIdTag | LegacyThreadExistsTag;
            }

            var response = new StringBuilder();
            response.Append("qQ");
            response.Append(responseMask.ToString("x8"));
            response.Append(threadRef);

            if((responseMask & LegacyThreadIdTag) != 0)
            {
                AppendLegacyThreadInfoField(response, LegacyThreadIdTag, threadRef);
            }
            if((responseMask & LegacyThreadExistsTag) != 0)
            {
                AppendLegacyThreadInfoField(response, LegacyThreadExistsTag, exists ? "1" : "0");
            }
            if((responseMask & LegacyThreadDisplayTag) != 0)
            {
                AppendLegacyThreadInfoField(response, LegacyThreadDisplayTag, threadName);
            }
            if((responseMask & LegacyThreadNameTag) != 0)
            {
                AppendLegacyThreadInfoField(response, LegacyThreadNameTag, threadName);
            }
            if((responseMask & LegacyThreadMoreDisplayTag) != 0)
            {
                AppendLegacyThreadInfoField(response, LegacyThreadMoreDisplayTag, exists ? "FreeRTOS/Renode thread" : string.Empty);
            }

            return new PacketData(response.ToString());
        }

        private List<uint> GetThreadIds()
        {
            var freeRtosTasks = FreeRTOSThreadProvider.GetTasks(manager);
            if(freeRtosTasks.Any())
            {
                return freeRtosTasks.Select(x => x.ThreadId).ToList();
            }

            var threadIds = new List<uint>();
            foreach(var cpu in manager.ManagedCpus)
            {
                threadIds.Add(manager.ManagedCpus[cpu]);
            }
            return threadIds;
        }

        private bool TryGetThreadName(uint threadId, out string name)
        {
            var task = FreeRTOSThreadProvider.GetTasks(manager).FirstOrDefault(x => x.ThreadId == threadId);
            if(task != null)
            {
                name = task.Name;
                return true;
            }

            if(manager.ManagedCpus.GdbCpuIds.Contains(threadId))
            {
                name = manager.ManagedCpus[threadId].GetName();
                return true;
            }

            name = string.Empty;
            return false;
        }

        private static void AppendLegacyThreadInfoField(StringBuilder response, uint tag, string value)
        {
            value = SanitizeLegacyThreadInfoValue(value);
            response.Append(tag.ToString("x8"));
            response.Append(value.Length.ToString("x2"));
            response.Append(value);
        }

        private static string SanitizeLegacyThreadInfoValue(string value)
        {
            if(string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach(var character in value)
            {
                if(builder.Length >= LegacyMaxStringFieldLength)
                {
                    break;
                }

                builder.Append(character >= 0x20 && character <= 0x7e && character != '#' && character != '$' ? character : '?');
            }
            return builder.ToString();
        }

        private static string NormalizeLegacyThreadId(string threadId)
        {
            if(threadId.Length >= LegacyThreadIdWidth)
            {
                return threadId.Substring(threadId.Length - LegacyThreadIdWidth, LegacyThreadIdWidth);
            }

            return threadId.PadLeft(LegacyThreadIdWidth, '0');
        }

        private static bool TryParseLegacyThreadId(string threadId, out uint result)
        {
            result = 0;
            if(threadId.Length > 8)
            {
                threadId = threadId.Substring(threadId.Length - 8, 8);
            }
            return uint.TryParse(threadId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);
        }

        private static string FormatLegacyThreadId(uint threadId)
        {
            return threadId.ToString("x16");
        }

        private int currentThreadIndex;
        private const int LegacyThreadIdWidth = 16;
        private const int LegacyMaxStringFieldLength = 200;

        private const uint LegacyThreadIdTag = 1;
        private const uint LegacyThreadExistsTag = 2;
        private const uint LegacyThreadDisplayTag = 4;
        private const uint LegacyThreadNameTag = 8;
        private const uint LegacyThreadMoreDisplayTag = 16;
        private const uint SupportedLegacyThreadInfoMask = LegacyThreadIdTag
            | LegacyThreadExistsTag
            | LegacyThreadDisplayTag
            | LegacyThreadNameTag
            | LegacyThreadMoreDisplayTag;
    }
}
