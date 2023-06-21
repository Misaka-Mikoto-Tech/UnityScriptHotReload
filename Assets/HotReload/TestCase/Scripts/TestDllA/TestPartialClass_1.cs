//#define APPLY_PATCH

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace NS_Test
{
    public partial class TestPartialClass
    {
        public int x;
        public void DoTest()
        {
#if APPLY_PATCH
            x += 1;
            Debug.Log($"TestPartialClass.DoTest with Patch.x:{x}, y_partial:{y_partial}");
#else
            Debug.Log($"TestPartialClass.DoTest.x:{y_partial}");
#endif
        }
    }

}
