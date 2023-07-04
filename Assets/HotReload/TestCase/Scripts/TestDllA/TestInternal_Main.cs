//#define APPLY_PATCH

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace NS_Test
{
    public class TestInternal_Main
    {
        public TestInternal_Main()
        {
            var def = new TestInternal_Define();
#if APPLY_PATCH
            Debug.Log(def.x);
#else
#endif
        }
    }
}
