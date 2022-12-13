using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace NS_Test
{
    public class TestDll_2
    {
        public int Mul_2(int x, int y)
        {
            PrintMethodLocation_2(MethodBase.GetCurrentMethod());
            return x * y + 2;
        }

        void PrintMethodLocation_2(MethodBase method)
        {
            var currMethod = MethodBase.GetCurrentMethod();
            string assPath = method.DeclaringType.Assembly.Location.Substring(Environment.CurrentDirectory.Length + 1);
            Debug.Log($"location `<color=yellow>{method.Name}</color>` of current dll: <color=yellow>{assPath.Replace('\\', '/')}</color>");
        }
    }
}

