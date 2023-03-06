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

    public Dictionary<string, TypeData>                 addedTypes = new Dictionary<string, TypeData>(); // 新增类型，不包括自动生成的 lambda表达式类
    public Dictionary<PdbDocument, List<MethodData>>    doc2methodsOfNew = new Dictionary<PdbDocument, List<MethodData>>(); // newTypes 中 doc 与 method的映射

    public Dictionary<string, TypeRefUser>              baseTypeRefImported = new Dictionary<string, TypeRefUser>(); // 从 baseDll 内引用的生成到 newDll 内的类型引用列表
    public Dictionary<string, MemberRefUser>            baseMemberRefImported = new Dictionary<string, MemberRefUser>(); // 从 baseDll 内引用的生成到 newDll 内的成员引用列表

    /// <summary>
    /// 在 newAssDef 中创建的指向 baseAssDef 的Ref
    /// </summary>
    public AssemblyRefUser baseRefAtNewAss;
    public ModuleRefUser baseRefAtNewDll;

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
        allMethodsInBase.Clear();
        allMethodsInPatch.Clear();

        var baseTypes = baseDllData.types;
        var newTypes = patchDllData.types;

        // 收集新添加的类型并且记录每个文件定义的所有新增方法
        foreach (var (typeName, typeData) in newTypes)
        {
            if (!baseTypes.ContainsKey(typeName))
            {
                addedTypes.Add(typeName, typeData);
                continue;
            }

            if (IsLambdaStaticType(typeName))
                continue;

            foreach (var (_, methodData) in typeData.methods)
            {
                var doc = methodData.document;
                if (doc == null || methodData.isLambda)
                    continue;

                if (!doc2methodsOfNew.TryGetValue(doc, out var lst))
                {
                    lst = new List<MethodData>();
                    doc2methodsOfNew.Add(doc, lst);
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
                refUser = new TypeRefUser(baseDllData.moduleDef, @namespace, name, baseRefAtNewAss);

            baseTypeRefImported.Add(fullName, refUser);
        }
        return refUser;
    }

    public MemberRefUser GetMemberRefFromBaseMethod(MemberRef methodRef)
    {
        string fullName = methodRef.FullName
        if (!baseMemberRefImported.TryGetValue())
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
        var newTypes = patchDllData.types;

        foreach (var (typeName, baseType) in baseTypes)
        {
            if (!newTypes.TryGetValue(typeName, out TypeData newType))
            {
                Debug.LogError($"can not find type `{typeName}` in new assembly[{patchName}], you can not delete type definition");
                return false;
            }

            if (IsLambdaStaticType(typeName))
                continue; // 名称为 `<>` 的存储不包含外部引用的lambda函数的类型，内部只有静态字段, 由于我们不对其hook，因此内存布局不予考虑

            var baseFields = baseType.definition.Fields.ToArray();
            var newFields = newType.definition.Fields.ToArray();
            if (baseFields.Length != newFields.Length)
            {
                Debug.LogError($"field count changed of type:{typeName}");
                return false;
            }
            for (int i = 0, imax = baseFields.Length; i < imax; i++)
            {
                var baseField = baseFields[i];
                var newField = newFields[i];
                if (baseField.ToString() != newField.ToString())
                {
                    Debug.LogError($"field `{baseField}` changed of type:{typeName}");
                    return false;
                }
            }

            // 不允许新增虚函数，会导致虚表发生变化
            foreach (var kvM in newType.methods)
            {
                if (!baseType.methods.ContainsKey(kvM.Key))
                {
                    if (kvM.Value.definition.IsVirtual)
                    {
                        Debug.LogError($"add virtual method is not allowd:{kvM.Key}");
                        return false;
                    }
                }
            }
        }
        return true;
    }
}