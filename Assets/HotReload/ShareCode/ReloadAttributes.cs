using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
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
        /// wrapper 函数关联的泛型方法实例
        /// </summary>
        public MethodBase genericInstMethod;
        /// <summary>
        /// 方法所属的泛型类型的类型参数列表 + 泛型方法的类型参数列表
        /// </summary>
        public Type[] typeGenArgs;
    }
}
