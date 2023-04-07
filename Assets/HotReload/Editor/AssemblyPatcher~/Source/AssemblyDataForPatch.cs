using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System;
using System.Linq;
using System.Text;
using SimpleJSON;
using dnlib;
using dnlib.DotNet;
using dnlib.DotNet.Pdb;

using static AssemblyPatcher.Utils;
using System.Security.Permissions;
using SecurityAction = System.Security.Permissions.SecurityAction;
using dnlib.DotNet.Emit;
using NHibernate.Mapping;
using TypeDef = dnlib.DotNet.TypeDef;
using dnlib.DotNet.MD;

namespace AssemblyPatcher;

/// <summary>
/// 每一对需要Patch的Dll的数据集合(类型，方法，文件名 etc.)
/// </summary>
public class AssemblyDataForPatch
{
    public bool                         isValid { get; private set; }
    public string                       name;
    public string                       patchName;
    public ModuleDefData                baseDllData;
    public ModuleDefData                patchDllData;
    /// <summary>
    /// PatchDll新增的类型
    /// </summary>
    public Dictionary<string, TypeData>                 addedTypes = new Dictionary<string, TypeData>();
    /// <summary>
    /// PatchDll中源文件与method的映射
    /// </summary>
    public Dictionary<PdbDocument, List<MethodData>>    doc2methodsOfPatch = new Dictionary<PdbDocument, List<MethodData>>();
    /// <summary>
    /// 从 baseDll 定义生成的类型引用
    /// </summary>
    public Dictionary<string, TypeRefUser>              baseTypeRefImported = new Dictionary<string, TypeRefUser>();
    /// <summary>
    /// 从 baseDll 定义生成的成员引用
    /// </summary>
    public Dictionary<string, MemberRefUser>            baseMemberRefImported = new Dictionary<string, MemberRefUser>();

    public AssemblyDataForPatch(string name)
    {
        this.name = name;
        this.patchName = GetPatchDllName(name);
    }

    public void Init()
    {
        baseDllData = ModuleDefPool.GetModuleData(name);
        patchDllData = ModuleDefPool.GetModuleData(patchName);
        addedTypes.Clear();

        var baseTypes = baseDllData.types;
        var patchTypes = patchDllData.types;

        // 收集新添加的类型并且记录每个文件定义的所有新增方法
        foreach (var (typeName, typeData) in patchTypes)
        {
            if (!baseTypes.ContainsKey(typeName))
            {
                addedTypes.Add(typeName, typeData);
                continue;
            }

            foreach (var (_, methodData) in typeData.methods)
            {
                var doc = methodData.document;
                if (doc == null)
                    continue;

                if (!doc2methodsOfPatch.TryGetValue(doc, out var lst))
                {
                    lst = new List<MethodData>();
                    doc2methodsOfPatch.Add(doc, lst);
                }

                lst.Add(methodData);
            }
        }
        
        isValid = CheckTypesLayout();
    }

    public TypeRefUser GetTypeRefFromBaseType(ITypeDefOrRef typeDefOrRef)
    {
        string fullName = typeDefOrRef.FullName;
        if (!baseTypeRefImported.TryGetValue(fullName, out TypeRefUser refUser))
        {
            string @namespace = typeDefOrRef.Namespace;
            string name = typeDefOrRef.Name;

            // NestedType 的 Namespace 始终为 null, 但 scope 为 DeclaredType, 需要递归生成
            if ((bool)(typeDefOrRef as TypeDef)?.IsNested)
            {
                var declareRefUser = GetTypeRefFromBaseType(typeDefOrRef.DeclaringType);
                refUser = new TypeRefUser(baseDllData.moduleDef, @namespace, name, declareRefUser);
            }
            else
                refUser = new TypeRefUser(baseDllData.moduleDef, @namespace, name);

            baseTypeRefImported.Add(fullName, refUser);
        }
        return refUser;
    }

    public MemberRefUser GetMemberRefFromBaseMethod(MemberRef methodRef)
    {
        //string fullName = methodRef.FullName
        //if (!baseMemberRefImported.TryGetValue())
        throw new NotImplementedException();
    }

    public FieldDefUser GetMemberRefFromBaseField(FieldDef fieldDef)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// hotreload时要求已存在类型的内存布局严格一致
    /// </summary>
    /// <returns></returns>
    private bool CheckTypesLayout()
    {
        var baseTypes = baseDllData.types;
        var patchTypes = patchDllData.types;

        foreach (var (typeName, patchTypeData) in patchTypes)
        {
            if (!baseTypes.TryGetValue(typeName, out TypeData baseTypeData)) // 新增类型
                continue;

            if (IsLambdaStaticType(typeName))
                continue; // 名称为 `<>` 的存储不包含外部引用的lambda函数的类型，内部只有静态字段, 由于我们不对其hook，因此内存布局不予考虑

            var baseFields = baseTypeData.definition.Fields.ToArray();
            var patchFields = patchTypeData.definition.Fields.ToArray();
            if (baseFields.Length != patchFields.Length)
            {
                Debug.LogError($"field count changed of type:{typeName}");
                return false;
            }
            for (int i = 0, imax = patchFields.Length; i < imax; i++)
            {
                var baseField = baseFields[i];
                var patchField = patchFields[i];
                if (baseField.FieldOffset != patchField.FieldOffset
                    || baseField.ToString() != patchField.ToString())
                {
                    Debug.LogError($"field `{baseField}` changed of type:{typeName}");
                    return false;
                }
            }

            // 不允许新增虚函数，会导致虚表发生变化
            foreach (var (methodName, methodData) in patchTypeData.methods)
            {
                if (!baseTypeData.methods.TryGetValue(methodName, out var baseMethodData))
                {
                    if (methodData.definition.IsVirtual) // 新增了虚函数
                    {
                        Debug.LogError($"add virtual method is not allowd:{methodName}");
                        return false;
                    }
                }
                else
                {
                    if(methodData.definition.IsVirtual != baseMethodData.definition.IsVirtual) // 已有函数的虚函数属性发生了改变
                    {
                        Debug.LogError($"change virtual flag of method is not allowd:{methodName}");
                        return false;
                    }
                }
            }
        }
        return true;
    }
}