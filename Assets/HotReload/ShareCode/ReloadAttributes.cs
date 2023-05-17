using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ScriptHotReload
{
    /// <summary>
    /// 泛型函数的HookWrapper(Patcher使用，请勿手动添加)
    /// </summary>
    public class GenericMethodIndexAttribute : Attribute
    {
        public int index;
    }

    /// <summary>
    /// 泛型函数生成的wrapper函数体(Patcher使用，请勿手动添加)
    /// </summary>
    public class GenericMethodWrapperAttribute : Attribute
    {
        /// <summary>
        /// 配对索引，与 HookWrapperGenericAttribute 相同，可以多对一
        /// </summary>
        public int index;
        /// <summary>
        /// 当此字段为 null 时所有泛型参数均为 object, 
        /// 否则对应相同index的HookWrapperGeneric需要填充的参数
        /// </summary>
        public Type[] types;
    }
}
