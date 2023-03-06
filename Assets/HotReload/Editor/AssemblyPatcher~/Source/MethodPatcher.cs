using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using dnlib;
using dnlib.DotNet;
using dnlib.DotNet.Pdb;

using static AssemblyPatcher.Utils;
using System.Security.Permissions;
using SecurityAction = System.Security.Permissions.SecurityAction;
using NHibernate.Mapping;
using System.Runtime.CompilerServices;
using dnlib.DotNet.Emit;
using TypeDef = dnlib.DotNet.TypeDef;

namespace AssemblyPatcher;

public class MethodPatcher
{
    AssemblyDataForPatch _assemblyDataForPatch;
    public MethodPatcher(AssemblyDataForPatch assemblyData)
    {
        _assemblyDataForPatch = assemblyData;
    }

    public void PatchMethod(MethodDef methodDef, Dictionary<MethodDef, MethodFixStatus> processed, int depth)
    {
        var fixStatus = new MethodFixStatus();
        if (processed.ContainsKey(methodDef))
            return;
        else
            processed.Add(methodDef, fixStatus);

        if (!methodDef.HasBody)
            return;

        var sig = methodDef.ToString();
        if (_assemblyDataForPatch.baseDllData.allMethods.ContainsKey(sig))
            fixStatus.needHook = true;

        // 参数和返回值由于之前已经检查过名称是否一致，因此是二进制兼容的，可以不进行检查
        var arrIns = methodDef.Body.Instructions.ToArray();
        var currAssembly = _assemblyDataForPatch.patchDllData.moduleDef.Assembly;

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
                /*
                 * TypeDef: 当前Scope(eg.Type)范围内定义的类型，可能是非泛型或者未填充参数的泛型类型
                 * TypeRef: 纯虚类 TypeRef 只有两个子类：TypeRefMD 和 TypeRefUser, 分别对应从metadata里读取的原有类型和用户后添加的类型
                 */
                case ITypeDefOrRef typeDefOrRef:
                    if (typeDefOrRef.DefinitionAssembly == currAssembly) // ldtoken   NS_Test.TestCls
                    {
                        var baseTypeRef = _assemblyDataForPatch.GetTypeRefFromBaseType(typeDefOrRef);
                        if (baseTypeRef is not null)
                            ins.Operand = baseTypeRef;
                        else { } // PatchDll 内新增的类型
                    }
                    break;
                case FieldDef fieldDef:
                    // TODO isArray? isGeneric?
                    break;
                case MemberRef memberRef:
                    if (memberRef.DeclaringType.DefinitionAssembly == currAssembly)
                    {

                    }
                    break;
                #region 注释掉的代码
                //case FieldDef fieldDef:
                //    {
                //        var fieldType = fieldDef.DeclaringType;
                //        if (fieldType.Module != _assemblyData.newAssDef) break;

                //        bool isLambda = IsLambdaStaticType(fieldType);
                //        if (!isLambda && _assemblyData.baseTypes.TryGetValue(fieldType.FullName, out TypeData baseTypeData))
                //        {
                //            if (baseTypeData.fields.TryGetValue(fieldDef.FullName, out FieldDef baseFieldDef))
                //            {
                //                var fieldRef = _assemblyData.newAssDef.ImportReference(baseFieldDef);
                //                var newIns = Instruction.Create(ins.OpCode, fieldRef);
                //                arrIns[i] = newIns;
                //                fixStatus.ilFixed = true;
                //            }
                //            else
                //                throw new Exception($"can not find field {fieldDef.FullName} in base dll");
                //        }
                //        else
                //        {
                //            // 新定义的类型或者lambda, 可以使用新的Assembly内的定义, 但需要递归修正其中的方法
                //            if (_assemblyData.newTypes.TryGetValue(fieldType.ToString(), out TypeData typeData))
                //            {
                //                foreach (var kv in typeData.methods)
                //                {
                //                    PatchMethod(kv.Value.definition, processed, depth + 1);
                //                }
                //            }
                //        }
                //    }
                //    break;
                //case MethodDef methodDef:
                //    {
                //        bool isLambda = IsLambdaMethod(methodDef);
                //        if (!isLambda && _assemblyData.allBaseMethods.TryGetValue(methodDef.ToString(), out MethodData baseMethodDef))
                //        {
                //            var reference = _assemblyData.newAssDef.MainModule.ImportReference(baseMethodDef.definition);
                //            var newIns = Instruction.Create(ins.OpCode, reference);
                //            ilProcessor.Replace(ins, newIns);
                //            fixStatus.ilFixed = true;
                //        }
                //        else // 这是新定义的方法或者lambda表达式，需要递归修正
                //        {
                //            PatchMethod(methodDef, processed, depth + 1); // TODO 当object由非hook代码创建时调用新添加的虚方法可能有问题
                //        }
                //    }
                //    break;
                //case GenericInstanceType gTypeDef:
                //    {
                //        var t = GetBaseInstanceType(gTypeDef);
                //    }
                //    break;
                //case GenericInstanceMethod gMethodDef:
                //    {
                //        var m = GetBaseInstanceMethod(gMethodDef);
                //    }
                //    break;
                //case TypeRef typeRef:
                //    if (!IsDefInCurrentAssembly(typeRef))
                //        break;

                //    break;
                //case FieldRef fieldRef:
                //    if (!IsDefInCurrentAssembly(fieldRef))
                //        break;

                //    break;
                //case MethodRef methodRef:
                //    if (!IsDefInCurrentAssembly(methodRef))
                //        break;

                //    break;
                #endregion
                case TypeSpec typeSpec: // 填充了泛型参数的泛型类型实例(.net不允许只填充部分泛型参数，因此只存在完全不填充和全部填充两种情况)
                    {
                        // 含有泛型参数的类型出现在这里只可能是 T, V 等参数，经dnspy查看也无法引用其它dll中的类型，因此跳过(但也有可能是复合类型）
                        if (typeSpec.ContainsGenericParameter)
                            break;

                        IScope scope = typeSpec.Scope;
                        ITypeDefOrRef scopeType = typeSpec.ScopeType;
                        TypeDef typeDef = typeSpec.ScopeType as TypeDef;
                        TypeSig typeSig = typeSpec.ToTypeSig();

                        TypeSpec newSpec = CreateBaseInstanceType(typeSpec);
                        ins.Operand = newSpec;
                    }
                    break;
                case MethodSpec methodSpec:
                    {
                        var mToken = methodSpec.Method.MDToken;
                        var mDefOrRef = methodSpec.Method.Module.ResolveToken(mToken);
                        var mRef = mDefOrRef as MemberRef;
                        if(mRef is not null)
                        {
                            var mSig = mRef.MethodSig;
                            var mDef = mRef.ResolveMethod();
                            if(mDef is not null)
                            {
                                var fullName = mDef.FullName;
                                // 原始方法定义在当前Module或者泛型参数为当前Module，进行替换
                                // 由于 dotnet 禁止两个dll之间循环引用，因此当前方法内调用的参数类型也在当前Module内定义的方法一定也是定义在当前Module内的


                            }
                            //methodSpec.Method
                        }
                        //methodSpec.Method.Module.
                    }
                    break;
                default:
                    {
                        Type t = ins.Operand?.GetType();
                    }
                    break;
            } // switch
        } // for

        // 即使没有修改任何IL，也需要刷新pdb, 因此在头部给它加个nop
        //if (!fixStatus.ilFixed)
        //    ilProcessor.InsertBefore(ilProcessor.Body.Instructions[0], Instruction.Create(OpCodes.Nop));
    }

    /// <summary>
    /// 生成类型和泛型实参全部都是 Base Assembly 内的类型的类型描述
    /// </summary>
    /// <param name="t"></param>
    /// <returns></returns>
    TypeSpec CreateBaseInstanceType(TypeSpec t)
    {
        /*
         * 1. 找到原始dll中同名的泛型类型
         * 2. 使用原始dll中的真实类型填充泛型参数（可能需要递归）
         */

        TypeSig typeSig = t.ToTypeSig();


        //var gA = t.GenericArguments.ToArray();
        //var gP = t.GenericParameters.ToArray();

        return null;
    }

    TypeSig CreateBaseInstanceTypeSig(TypeSig typeSig)
    {
        return null;
    }

    /// <summary>
    /// 获取/生成 Base Assembly 内的方法定义
    /// </summary>
    /// <param name="m"></param>
    /// <returns></returns>
    MethodSpec GetBaseInstanceMethod(MethodSpec m)
    {
        //var gA = m.GenericArguments.ToArray();
        //var gP = m.GenericParameters.ToArray();
        //var eleMethod = m.ElementMethod;
        //var methods = (m.DeclaringType as TypeDefinition)?.Methods.ToArray();
        return null;
    }

    /// <summary>
    /// 获取 Base Assembly 内的类型引用
    /// </summary>
    /// <param name="typeRef"></param>
    /// <returns></returns>
    TypeRef GetBaseTypeRef(TypeRef typeRef)
    {
        //if (typeRef is GenericInstanceType genericTypeRef)
        //    return GetBaseInstanceType(genericTypeRef);

        return null;
    }

    /// <summary>
    /// 获取 Base Assembly 内的字段引用
    /// </summary>
    /// <param name="fieldRef"></param>
    /// <returns></returns>
    MemberRef GetBaseFieldRef(MemberRef fieldRef)
    {
        return null;
    }

    /// <summary>
    /// 获取 Base Assembly 内的方法引用
    /// </summary>
    /// <param name="methodRef"></param>
    /// <returns></returns>
    MemberRef GetBaseMethodRef(MemberRef methodRef)
    {
        //if (methodRef is GenericInstanceMethod genericMethodRef)
        //    return GetBaseInstanceMethod(genericMethodRef);

        return null;
    }



    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsDefInCurrentAssembly(TypeRef typeRef)
    {
        return typeRef.Scope == _assemblyDataForPatch.newDllDef;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsDefInCurrentAssembly(MemberRef memberRef)
    {
        return memberRef.DeclaringType.Scope == _assemblyDataForPatch.newDllDef;
    }


}
