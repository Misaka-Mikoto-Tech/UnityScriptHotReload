using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AssemblyPatcher;

internal class Debug
{
    public static void Log(string msg)
    {
        Console.WriteLine($"[Info]{msg.Replace("\r\n", "<br/>")}");
    }

    public static void LogWarning(string msg)
    {
        Console.WriteLine($"[Warning]{msg.Replace("\r\n", "<br/>")}");
    }

    public static void LogError(string msg)
    {
        Console.WriteLine($"[Error]{msg.Replace("\r\n", "<br/>")}");
    }

    public static void LogDebug(string msg)
    {
        Console.WriteLine($"[Debug]{msg.Replace("\r\n", "<br/>")}");
    }

    public static void Assert(bool condition)
    {
        System.Diagnostics.Debug.Assert(condition);
    }
}
