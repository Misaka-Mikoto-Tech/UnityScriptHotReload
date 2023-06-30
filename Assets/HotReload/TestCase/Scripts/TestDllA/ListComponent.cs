using System;
using System.Collections.Generic;

public class ListComponent<T>: List<T>, IDisposable
{
    /// <summary>
    /// 使用时1.注意线程安全问题2.必须调用Dispose销毁，否则会造成内存泄漏
    /// </summary>
    /// <returns></returns>
    public static ListComponent<T> Create()
    {
        return new ListComponent<T>();
    }

    public void Dispose()
    {
        this.Clear();
    }
}