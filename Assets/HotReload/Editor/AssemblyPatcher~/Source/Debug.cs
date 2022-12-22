using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AssemblyPatcher
{
    internal class Debug
    {
        public static void Log(string msg)
        {
            Console.WriteLine($"[Info]{msg}");
        }

        public static void LogWarning(string msg)
        {
            Console.WriteLine($"[Warning]{msg}");
        }

        public static void LogError(string msg)
        {
            Console.WriteLine($"[Error]{msg}");
        }

        public static void LogDebug(string msg)
        {
            Console.WriteLine($"[Debug]{msg}");
        }

        public static void Assert(bool condition)
        {
            System.Diagnostics.Debug.Assert(condition);
        }
    }
}
