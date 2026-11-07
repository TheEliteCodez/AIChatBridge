using System;
using System.Diagnostics;

namespace ChromeSidecarStandalone.Util
{
    internal static class Log
    {
        public static void Init()
        {
        }

        public static void Shutdown()
        {
        }

        public static void Debug(string message)
        {
            var line = "[AI Chat Bridge] " + message;
            Trace.WriteLine(line);
            Console.Error.WriteLine(line);
        }
    }
}
