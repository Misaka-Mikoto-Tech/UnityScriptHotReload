using Mono.Cecil;
using Mono.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using static AssemblyPatcher.Utils;
using System.Security.Permissions;
using SecurityAction = System.Security.Permissions.SecurityAction;
using OpCodes = Mono.Cecil.Cil.OpCodes;
using NHibernate.Mapping;
using System.Runtime.CompilerServices;

namespace AssemblyPatcher;

public class MethodPatcher
{
    AssemblyData _assemblyData;
    public MethodPatcher(AssemblyData assemblyData)
    {
        _assemblyData = assemblyData;
    }

    public void PatchMethod(MethodDefinition definition, Dictionary<MethodDefinition, MethodFixStatus> processed, int depth)
    {
        var fixStatus = new MethodFixStatus();
        if (processed.ContainsKey(definition))
            return;
        else
            processed.Add(definition, fixStatus);

        if (!definition.HasBody)
            return;

        var sig = definition.ToString();
        if (_assemblyData.methodsNeedHook.ContainsKey(sig))
            fixStatus.needHook = true;

        // 参数和返回值由于之前已经检查过名称是否一致，因此是二进制兼容的，可以不进行检查

        var arrIns = definition.Body.Instructions.ToArray();
        var ilProcessor = definition.Body.GetILProcessor();

        for (int i = 0, imax = arrIns.Length; i < imax; i++)
        {
            Instruction ins = arrIns[i];
            /*
                * Field 有两种类型: FieldReference/FieldDefinition, 经过研究发现 FieldReference 指向了当前类型外部的定义（当前Assembly或者引用的Assembly）, 
                * 而 FieldDefinition 则是当前类型内定义的字段
                * 因此我们需要检查 FieldDefinition 和 FieldReference, 把它们都替换成原始 Assembly 内同名的 FieldReference
                * Method/Type 同理, 但 lambda 表达式不进行替换，而是递归修正函数
                */
            if (ins.Operand == null)
                continue;

            switch(ins.Operand)
            {
                case string constStr:
                    break;
                case TypeDefinition typeDef:
                    break;
                case FieldDefinition fieldDef:
                    {
                        var fieldType = fieldDef.DeclaringType;
                        if (fieldType.Module != _assemblyData.newAssDef.MainModule) break;

                        bool isLambda = IsLambdaStaticType(fieldType);
                        if (!isLambda && _assemblyData.baseTypes.TryGetValue(fieldType.FullName, out TypeData baseTypeData))
                        {
                            if (baseTypeData.fields.TryGetValue(fieldDef.FullName, out FieldDefinition baseFieldDef))
                            {
                                var fieldRef = _assemblyData.newAssDef.MainModule.ImportReference(baseFieldDef);
                                var newIns = Instruction.Create(ins.OpCode, fieldRef);
                                ilProcessor.Replace(ins, newIns);
                                fixStatus.ilFixed = true;
                            }
                            else
                                throw new Exception($"can not find field {fieldDef.FullName} in base dll");
                        }
                        else
                        {
                            // 新定义的类型或者lambda, 可以使用新的Assembly内的定义, 但需要递归修正其中的方法
                            if (_assemblyData.newTypes.TryGetValue(fieldType.ToString(), out TypeData typeData))
                            {
                                foreach (var kv in typeData.methods)
                                {
                                    PatchMethod(kv.Value.definition, processed, depth + 1);
                                }
                            }
                        }
                    }
                    break;
                case MethodDefinition methodDef:
                    {
                        bool isLambda = IsLambdaMethod(methodDef);
                        if (!isLambda && _assemblyData.allBaseMethods.TryGetValue(methodDef.ToString(), out MethodData baseMethodDef))
                        {
                            var reference = _assemblyData.newAssDef.MainModule.ImportReference(baseMethodDef.definition);
                            var newIns = Instruction.Create(ins.OpCode, reference);
                            ilProcessor.Replace(ins, newIns);
                            fixStatus.ilFixed = true;
                        }
                        else // 这是新定义的方法或者lambda表达式，需要递归修正
                        {
                            PatchMethod(methodDef, processed, depth + 1); // TODO 当object由非hook代码创建时调用新添加的虚方法可能有问题
                        }
                    }
                    break;
                case GenericInstanceType gTypeDef:
                    {
                        var t = GetBaseInstanceType(gTypeDef);
                    }
                    break;
                case GenericInstanceMethod gMethodDef:
                    {
                        var m = GetBaseInstanceMethod(gMethodDef);
                    }
                    break;
                case TypeReference typeRef:
                    if (!IsDefInCurrentAssembly(typeRef))
                        break;

                    break;
                case FieldReference fieldRef:
                    if (!IsDefInCurrentAssembly(fieldRef))
                        break;

                    break;
                case MethodReference methodRef:
                    if (!IsDefInCurrentAssembly(methodRef))
                        break;

                    break;
                default:
                    {
                        Type t = ins.Operand?.GetType();
                    }
                    break;
            } // switch
        } // for

        // 即使没有修改任何IL，也需要刷新pdb, 因此在头部给它加个nop
        if (!fixStatus.ilFixed)
            ilProcessor.InsertBefore(ilProcessor.Body.Instructions[0], Instruction.Create(OpCodes.Nop));
    }

    /// <summary>
    /// 获取/生成 Base Assembly 内的类型定义
    /// </summary>
    /// <param name="t"></param>
    /// <returns></returns>
    GenericInstanceType GetBaseInstanceType(GenericInstanceType t)
    {
        /*
         * 1. 找到原始dll中同名的泛型类型
         * 2. 使用原始dll中的真实类型填充泛型参数（可能需要递归）
         */
        var gA = t.GenericArguments.ToArray();
        var gP = t.GenericParameters.ToArray();
        return null;
    }

    /// <summary>
    /// 获取/生成 Base Assembly 内的方法定义
    /// </summary>
    /// <param name="m"></param>
    /// <returns></returns>
    GenericInstanceMethod GetBaseInstanceMethod(GenericInstanceMethod m)
    {
        var gA = m.GenericArguments.ToArray();
        var gP = m.GenericParameters.ToArray();
        var eleMethod = m.ElementMethod;
        var methods = (m.DeclaringType as TypeDefinition)?.Methods.ToArray();
        return null;
    }

    /// <summary>
    /// 获取 Base Assembly 内的类型引用
    /// </summary>
    /// <param name="typeRef"></param>
    /// <returns></returns>
    TypeReference GetBaseTypeRef(TypeReference typeRef)
    {
        if (typeRef is GenericInstanceType genericTypeRef)
            return GetBaseInstanceType(genericTypeRef);

        return null;
    }

    /// <summary>
    /// 获取 Base Assembly 内的字段引用
    /// </summary>
    /// <param name="fieldRef"></param>
    /// <returns></returns>
    FieldReference GetBaseFieldRef(FieldReference fieldRef)
    {
        return null;
    }

    /// <summary>
    /// 获取 Base Assembly 内的方法引用
    /// </summary>
    /// <param name="methodRef"></param>
    /// <returns></returns>
    MethodReference GetBaseMethodRef(MethodReference methodRef)
    {
        if (methodRef is GenericInstanceMethod genericMethodRef)
            return GetBaseInstanceMethod(genericMethodRef);

        return null;
    }



    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsDefInCurrentAssembly(TypeReference typeRef)
    {
        return typeRef.Scope == _assemblyData.newAssDef.MainModule;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsDefInCurrentAssembly(FieldReference fieldRef)
    {
        return fieldRef.DeclaringType.Scope == _assemblyData.newAssDef.MainModule;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsDefInCurrentAssembly(MethodReference methodRef)
    {
        return methodRef.DeclaringType.Scope == _assemblyData.newAssDef.MainModule;
    }

}
