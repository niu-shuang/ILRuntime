﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ILRuntime.Mono.Cecil;

using ILRuntime.CLR.TypeSystem;
using ILRuntime.CLR.Method;
using ILRuntime.Runtime.Stack;
using ILRuntime.Runtime.Intepreter.OpCodes;
using ILRuntime.Runtime.Enviorment;
using ILRuntime.CLR.Utils;

namespace ILRuntime.Runtime.Intepreter
{
    unsafe struct RegisterFrameInfo
    {
        public ILIntepreter Intepreter;
        public int LocalManagedBase;
        public int ParameterCount;
        public int LocalCount;
        public StackObject* RegisterStart;
        public IList<object> ManagedStack;
    }
    public unsafe partial class ILIntepreter
    {   
        internal StackObject* ExecuteR(ILMethod method, StackObject* esp, out bool unhandledException)
        {
#if DEBUG
            if (method == null)
                throw new NullReferenceException();
#endif
#if DEBUG && !NO_PROFILER
            if (System.Threading.Thread.CurrentThread.ManagedThreadId == AppDomain.UnityMainThreadID)

#if UNITY_5_5_OR_NEWER
                UnityEngine.Profiling.Profiler.BeginSample(method.ToString());
#else
                UnityEngine.Profiler.BeginSample(method.ToString());
#endif

#endif
            OpCodeR[] body = method.BodyRegister;
            StackFrame frame;
            stack.InitializeFrame(method, esp, out frame); 
            StackObject* v1 = frame.LocalVarPointer;
            int finallyEndAddress = 0;

            var stackRegStart = frame.BasePointer;
            esp = Add(frame.BasePointer, method.StackRegisterCount);
            StackObject* r = Minus(frame.LocalVarPointer, method.ParameterCount);
            IList<object> mStack = stack.ManagedStack;
            int paramCnt = method.ParameterCount;
            if (method.HasThis)//this parameter is always object reference
            {
                r--;
                paramCnt++;
                /// 为确保性能，暂时先确保开发的时候，安全检查完备。
                /// 当然手机端运行时可能会出现为空的类对象可正常调用成员函数，导致成员函数里面访问成员变量报错时可能使得根据Log跟踪BUG时方向错误。
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
                var thisObj = RetriveObject(r, mStack);
                if (thisObj == null)
                    throw new NullReferenceException();
#endif
            }
            unhandledException = false;
            var hasReturn = method.ReturnType != AppDomain.VoidType;

            //Managed Stack reserved for arguments(In case of starg)
            for (int i = 0; i < paramCnt; i++)
            {
                var a = Add(r, i);
                switch (a->ObjectType)
                {
                    case ObjectTypes.Null:
                        //Need to reserve place for null, in case of starg
                        a->ObjectType = ObjectTypes.Object;
                        a->Value = mStack.Count;
                        mStack.Add(null);
                        break;
                    case ObjectTypes.ValueTypeObjectReference:
                        //CloneStackValueType(a, a, mStack);
                        break;
                    case ObjectTypes.Object:
                    case ObjectTypes.FieldReference:
                    case ObjectTypes.ArrayReference:
                        {
                            if (i > 0 || !method.HasThis)//this instance should not be cloned
                                mStack[a->Value] = CheckAndCloneValueType(mStack[a->Value], AppDomain);
                        }
                        frame.ManagedStackBase--;
                        break;
                }
            }

            stack.PushFrame(ref frame);

            int locBase = mStack.Count;
            int locCnt = method.LocalVariableCount;
            int stackRegCnt = method.StackRegisterCount;
            RegisterFrameInfo info;
            info.Intepreter = this;
            info.LocalCount = locCnt;
            info.LocalManagedBase = locBase;
            info.ParameterCount = paramCnt;
            info.RegisterStart = r;
            info.ManagedStack = mStack;

            object obj;

            //Managed Stack reserved for local variable
            for (int i = 0; i < locCnt; i++)
            {
                mStack.Add(null);
            }

            for (int i = 0; i < locCnt; i++)
            {
                var v = method.Variables[i];
                bool isEnum = false;
                var vt = v.VariableType;
                IType t;
                if (vt.IsGenericParameter)
                {
                    t = method.FindGenericArgument(vt.Name);
                }
                else
                {
                    t = AppDomain.GetType(v.VariableType, method.DeclearingType, method);
                }
                isEnum = t.IsEnum;
                if (!t.IsByRef && t.IsValueType && !t.IsPrimitive && !isEnum)
                {
                    if (t is ILType)
                    {
                        //var obj = ((ILType)t).Instantiate(false);
                        var loc = Add(v1, i);
                        stack.AllocValueType(loc, t);

                        /*loc->ObjectType = ObjectTypes.Object;
                        loc->Value = mStack.Count;
                        mStack.Add(obj);*/

                    }
                    else
                    {
                        CLRType cT = (CLRType)t;
                        var loc = Add(v1, i);
                        if (cT.ValueTypeBinder != null)
                        {
                            stack.AllocValueType(loc, t);
                        }
                        else
                        {
                            obj = ((CLRType)t).CreateDefaultInstance();
                            loc->ObjectType = ObjectTypes.Object;
                            loc->Value = locBase + i;
                            mStack[locBase + i] = obj;
                        }
                    }
                }
                else
                {
                    if (t.IsPrimitive || isEnum)
                    {
                        var loc = Add(v1, i);
                        StackObject.Initialized(loc, t);
                    }
                    else
                    {
                        var loc = Add(v1, i);
                        loc->ObjectType = ObjectTypes.Object;
                        loc->Value = locBase + i;
                    }
                }
            }
            for (int i = 0; i < stackRegCnt; i++)
            {
                var loc = Add(stackRegStart, i);
                *loc = StackObject.Null;
                mStack.Add(null);
            }
            var bp = stack.ValueTypeStackPointer;
            ValueTypeBasePointer = bp;

            StackObject* reg1, reg2, reg3, objRef, val, dst, ret;
            int intVal = 0;
            long longVal = 0;
            float floatVal = 0;
            double doubleVal = 0;
            IType type;
            Type clrType;

            fixed (OpCodeR* ptr = body)
            {
                OpCodeR* ip = ptr;
                OpCodeREnum code = ip->Code;
                bool returned = false;
                while (!returned)
                {
                    try
                    {
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
                        if (ShouldBreak)
                            Break();
                        var insOffset = (int)(ip - ptr);
                        frame.Address.Value = insOffset;
                        AppDomain.DebugService.CheckShouldBreak(method, this, insOffset);
#endif
                        code = ip->Code;

                        switch (code)
                        {
                            #region Arguments and Local Variable
                            case OpCodeREnum.Ldarg:
                            case OpCodeREnum.Ldarg_S:
                                {
                                    reg1 = Add(r, ip->Register2);
                                    CopyToRegister(ref info, ip->Register1, reg1);
                                }
                                break;
                            #endregion
                            #region Load Constants
                            case OpCodeREnum.Ldc_I4_M1:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg1->ObjectType = ObjectTypes.Integer;
                                    reg1->Value = -1;
                                }
                                break;
                            case OpCodeREnum.Ldc_I4_0:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg1->ObjectType = ObjectTypes.Integer;
                                    reg1->Value = 0;
                                }
                                break;
                            case OpCodeREnum.Ldc_I4_1:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg1->ObjectType = ObjectTypes.Integer;
                                    reg1->Value = 1;
                                }
                                break;
                            case OpCodeREnum.Ldc_I4_2:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg1->ObjectType = ObjectTypes.Integer;
                                    reg1->Value = 2;
                                }
                                break;
                            case OpCodeREnum.Ldc_I4_3:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg1->ObjectType = ObjectTypes.Integer;
                                    reg1->Value = 3;
                                }
                                break;
                            case OpCodeREnum.Ldc_I4_4:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg1->ObjectType = ObjectTypes.Integer;
                                    reg1->Value = 4;
                                }
                                break;
                            case OpCodeREnum.Ldc_I4_5:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg1->ObjectType = ObjectTypes.Integer;
                                    reg1->Value = 5;
                                }
                                break;
                            case OpCodeREnum.Ldc_I4_6:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg1->ObjectType = ObjectTypes.Integer;
                                    reg1->Value = 6;
                                }
                                break;
                            case OpCodeREnum.Ldc_I4_7:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg1->ObjectType = ObjectTypes.Integer;
                                    reg1->Value = 7;
                                }
                                break;
                            case OpCodeREnum.Ldc_I4_8:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg1->ObjectType = ObjectTypes.Integer;
                                    reg1->Value = 8;
                                }
                                break;

                            case OpCodeREnum.Ldc_I4:
                            case OpCodeREnum.Ldc_I4_S:
                                reg1 = Add(r, ip->Register1);
                                reg1->ObjectType = ObjectTypes.Integer;
                                reg1->Value = ip->Operand;
                                break;
                            case OpCodeREnum.Ldc_R4:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    *(float*)(&reg1->Value) = ip->OperandFloat;
                                    reg1->ObjectType = ObjectTypes.Float;
                                }
                                break;
                            case OpCodeREnum.Ldc_I8:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    *(long*)(&reg1->Value) = ip->OperandLong;
                                    reg1->ObjectType = ObjectTypes.Long;
                                }
                                break;
                            case OpCodeREnum.Ldc_R8:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    *(double*)(&reg1->Value) = ip->OperandDouble;
                                    reg1->ObjectType = ObjectTypes.Double;
                                }
                                break;
                            case OpCodeREnum.Ldstr:
                                AssignToRegister(ref info, ip->Register1, AppDomain.GetString(ip->OperandLong));
                                break;
                            case OpCodeREnum.Ldnull:
                                reg1 = Add(r, ip->Register1);
                                WriteNull(reg1);
                                break;
                            #endregion

                            #region Althemetics
                            case OpCodeREnum.Add:
                                {
                                    reg1 = Add(r, ip->Register2);
                                    reg2 = Add(r, ip->Register3);
                                    reg3 = Add(r, ip->Register1);
                                    switch (reg1->ObjectType)
                                    {
                                        case ObjectTypes.Long:
                                            reg3->ObjectType = ObjectTypes.Long;
                                            *((long*)&reg3->Value) = *((long*)&reg1->Value) + *((long*)&reg2->Value);
                                            break;
                                        case ObjectTypes.Integer:
                                            reg3->ObjectType = ObjectTypes.Integer;
                                            reg3->Value = reg1->Value + reg2->Value;
                                            break;
                                        case ObjectTypes.Float:
                                            reg3->ObjectType = ObjectTypes.Float;
                                            *((float*)&reg3->Value) = *((float*)&reg1->Value) + *((float*)&reg2->Value);
                                            break;
                                        case ObjectTypes.Double:
                                            reg3->ObjectType = ObjectTypes.Double;
                                            *((double*)&reg3->Value) = *((double*)&reg1->Value) + *((double*)&reg2->Value);
                                            break;
                                        default:
                                            throw new NotImplementedException();
                                    }
                                }
                                break;
                            case OpCodeREnum.Div:
                                {
                                    reg1 = Add(r, ip->Register2);
                                    reg2 = Add(r, ip->Register3);
                                    reg3 = Add(r, ip->Register1);
                                    switch (reg1->ObjectType)
                                    {
                                        case ObjectTypes.Long:
                                            reg3->ObjectType = ObjectTypes.Long;
                                            *((long*)&reg3->Value) = *((long*)&reg1->Value) / *((long*)&reg2->Value);
                                            break;
                                        case ObjectTypes.Integer:
                                            reg3->ObjectType = ObjectTypes.Integer;
                                            reg3->Value = reg1->Value / reg2->Value;
                                            break;
                                        case ObjectTypes.Float:
                                            reg3->ObjectType = ObjectTypes.Float;
                                            *((float*)&reg3->Value) = *((float*)&reg1->Value) / *((float*)&reg2->Value);
                                            break;
                                        case ObjectTypes.Double:
                                            reg3->ObjectType = ObjectTypes.Double;
                                            *((double*)&reg3->Value) = *((double*)&reg1->Value) / *((double*)&reg2->Value);
                                            break;
                                        default:
                                            throw new NotImplementedException();
                                    }
                                }
                                break;
                            case OpCodeREnum.Div_Un:
                                {
                                    reg1 = Add(r, ip->Register2);
                                    reg2 = Add(r, ip->Register3);
                                    reg3 = Add(r, ip->Register1);
                                    switch (reg1->ObjectType)
                                    {
                                        case ObjectTypes.Long:
                                            reg3->ObjectType = ObjectTypes.Long;
                                            *((ulong*)&reg3->Value) = *((ulong*)&reg1->Value) / *((ulong*)&reg2->Value);
                                            break;
                                        case ObjectTypes.Integer:
                                            reg3->ObjectType = ObjectTypes.Integer;
                                            reg3->Value = (int)((uint)reg1->Value / (uint)reg2->Value);
                                            break;
                                        default:
                                            throw new NotImplementedException();
                                    }
                                }
                                break;
                            #endregion

                            #region Conversion
                            case OpCodeREnum.Conv_I4:
                            case OpCodeREnum.Conv_I:
                            case OpCodeREnum.Conv_Ovf_I:
                            case OpCodeREnum.Conv_Ovf_I_Un:
                            case OpCodeREnum.Conv_Ovf_I4:
                            case OpCodeREnum.Conv_Ovf_I4_Un:
                                {
                                    reg1 = Add(r, ip->Register2);
                                    reg2 = Add(r, ip->Register1);
                                    switch (reg1->ObjectType)
                                    {
                                        case ObjectTypes.Long:
                                            intVal = (int)*(long*)&reg1->Value;
                                            break;
                                        case ObjectTypes.Float:
                                            intVal = (int)*(float*)&reg1->Value;
                                            break;
                                        case ObjectTypes.Double:
                                            intVal = (int)*(double*)&reg1->Value;
                                            break;
                                        case ObjectTypes.Integer:
                                            intVal = reg1->Value;
                                            break;
                                        default:
                                            throw new NotImplementedException();
                                    }
                                    reg2->ObjectType = ObjectTypes.Integer;
                                    reg2->Value = intVal;
                                }
                                break;
                            case OpCodeREnum.Conv_I8:
                            case OpCodeREnum.Conv_Ovf_I8:
                            case OpCodeREnum.Conv_Ovf_I8_Un:
                                {
                                    reg1 = Add(r, ip->Register2);
                                    reg2 = Add(r, ip->Register1);
                                    switch (reg1->ObjectType)
                                    {
                                        case ObjectTypes.Integer:
                                            longVal = reg1->Value;
                                            break;
                                        case ObjectTypes.Long:
                                            ip++;
                                            continue;
                                        case ObjectTypes.Float:
                                            longVal = (long)*(float*)&reg1->Value;
                                            break;
                                        case ObjectTypes.Double:
                                            longVal = (long)*(double*)&reg1->Value;
                                            break;
                                        default:
                                            throw new NotImplementedException();
                                    }
                                    reg2->ObjectType = ObjectTypes.Long;
                                    *(long*)(&reg2->Value) = longVal;
                                }
                                break;
                            case OpCodeREnum.Conv_R4:
                                {
                                    reg1 = Add(r, ip->Register2);
                                    reg2 = Add(r, ip->Register1);
                                    switch (reg1->ObjectType)
                                    {
                                        case ObjectTypes.Long:
                                            floatVal = (float)*(long*)&reg1->Value;
                                            break;
                                        case ObjectTypes.Float:
                                            ip++;
                                            continue;
                                        case ObjectTypes.Double:
                                            floatVal = (float)*(double*)&reg1->Value;
                                            break;
                                        case ObjectTypes.Integer:
                                            floatVal = reg1->Value;
                                            break;
                                        default:
                                            throw new NotImplementedException();
                                    }
                                    reg2->ObjectType = ObjectTypes.Float;
                                    *(float*)&reg2->Value = floatVal;
                                }
                                break;
                            case OpCodeREnum.Conv_R8:
                                {
                                    reg1 = Add(r, ip->Register2);
                                    reg2 = Add(r, ip->Register1);
                                    switch (reg1->ObjectType)
                                    {
                                        case ObjectTypes.Long:
                                            doubleVal = (double)*(long*)&reg1->Value;
                                            break;
                                        case ObjectTypes.Float:
                                            doubleVal = *(float*)&reg1->Value;
                                            break;
                                        case ObjectTypes.Integer:
                                            doubleVal = reg1->Value;
                                            break;
                                        case ObjectTypes.Double:
                                            ip++;
                                            continue;
                                        default:
                                            throw new NotImplementedException();
                                    }
                                    reg2->ObjectType = ObjectTypes.Double;
                                    *(double*)&reg2->Value = doubleVal;
                                }
                                break;
                            case OpCodeREnum.Conv_R_Un:
                                {
                                    reg1 = Add(r, ip->Register2);
                                    reg2 = Add(r, ip->Register1);
                                    bool isDouble = false;
                                    double val2 = 0;
                                    switch (reg1->ObjectType)
                                    {
                                        case ObjectTypes.Long:
                                            doubleVal = (double)*(ulong*)&reg1->Value;
                                            isDouble = true;
                                            break;
                                        case ObjectTypes.Float:
                                            ip++;
                                            continue;
                                        case ObjectTypes.Integer:
                                            floatVal = (uint)reg1->Value;
                                            break;
                                        case ObjectTypes.Double:
                                            ip++;
                                            continue;
                                        default:
                                            throw new NotImplementedException();
                                    }
                                    if (isDouble)
                                    {
                                        reg2->ObjectType = ObjectTypes.Double;
                                        *(double*)&reg2->Value = doubleVal;
                                    }
                                    else
                                    {
                                        reg2->ObjectType = ObjectTypes.Float;
                                        *(float*)&reg2->Value = floatVal;
                                    }
                                }
                                break;
                            #endregion

                            #region Load Store
                            case OpCodeREnum.Move:
                                {
                                    reg1 = Add(r, ip->Register2);
                                    CopyToRegister(ref info, ip->Register1, reg1);
                                }
                                break;

                            case OpCodeREnum.Push:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    CopyToStack(esp, reg1, mStack);
                                    esp++;
                                }
                                break;
                            case OpCodeREnum.Ldloca:
                            case OpCodeREnum.Ldloca_S:
                                {
                                    reg1 = Add(r, ip->Register2);
                                    reg2 = Add(r, ip->Register1);

                                    reg2->ObjectType = ObjectTypes.StackObjectReference;
                                    *(long*)&reg2->Value = (long)reg1;
                                }
                                break;
                            case OpCodeREnum.Ldind_I:
                            case OpCodeREnum.Ldind_I1:
                            case OpCodeREnum.Ldind_I2:
                            case OpCodeREnum.Ldind_I4:
                            case OpCodeREnum.Ldind_U1:
                            case OpCodeREnum.Ldind_U2:
                            case OpCodeREnum.Ldind_U4:
                                {
                                    reg1 = Add(r, ip->Register2);
                                    reg2 = Add(r, ip->Register1);
                                    val = GetObjectAndResolveReference(reg1);
                                    switch (val->ObjectType)
                                    {
                                        case ObjectTypes.FieldReference:
                                            {
                                                var instance = mStack[val->Value];
                                                var idx = val->ValueLow;
                                                //Free(dst);
                                                //LoadFromFieldReference(instance, idx, reg2, mStack);
                                                throw new NotImplementedException();
                                            }
                                            break;
                                        case ObjectTypes.ArrayReference:
                                            {
                                                var instance = mStack[val->Value];
                                                var idx = val->ValueLow;
                                                //Free(dst);
                                                //LoadFromArrayReference(instance, idx, dst, instance.GetType().GetElementType(), mStack);

                                                throw new NotImplementedException();
                                            }
                                            break;
                                        default:
                                            {
                                                reg2->ObjectType = ObjectTypes.Integer;
                                                reg2->Value = val->Value;
                                                reg2->ValueLow = 0;
                                            }
                                            break;
                                    }
                                }
                                break;
                            case OpCodeREnum.Stind_I:
                            case OpCodeREnum.Stind_I1:
                            case OpCodeREnum.Stind_I2:
                            case OpCodeREnum.Stind_I4:
                            case OpCodeREnum.Stind_R4:
                                {
                                    reg1 = Add(r, ip->Register2);
                                    reg2 = Add(r, ip->Register1);
                                    dst = GetObjectAndResolveReference(reg2);
                                    switch (dst->ObjectType)
                                    {
                                        case ObjectTypes.FieldReference:
                                            {
                                                //StoreValueToFieldReference(mStack[dst->Value], dst->ValueLow, val, mStack);
                                                throw new NotImplementedException();
                                            }
                                            break;
                                        case ObjectTypes.ArrayReference:
                                            {
                                                //StoreValueToArrayReference(dst, val, mStack[dst->Value].GetType().GetElementType(), mStack);
                                                throw new NotImplementedException();
                                            }
                                            break;
                                        default:
                                            {
                                                dst->Value = reg1->Value;
                                            }
                                            break;
                                    }
                                    /*Free(esp - 1);
                                    Free(esp - 1 - 1);
                                    esp = esp - 1 - 1;*/
                                }
                                break;
                            case OpCodeREnum.Ldtoken:
                                {
                                    switch (ip->Operand)
                                    {
                                        case 0:
                                            {
                                                type = AppDomain.GetType((int)(ip->OperandLong >> 32));
                                                if (type != null)
                                                {
                                                    if (type is ILType)
                                                    {
                                                        ILType t = type as ILType;

                                                        t.StaticInstance.CopyToRegister((int)ip->OperandLong,ref info, ip->Register1);
                                                    }
                                                    else
                                                        throw new NotImplementedException();
                                                }
                                            }
                                            break;
                                        case 1:
                                            {
                                                type = AppDomain.GetType((int)ip->OperandLong);
                                                if (type != null)
                                                {
                                                    AssignToRegister(ref info, ip->Register1, type.ReflectionType);
                                                }
                                                else
                                                    throw new TypeLoadException();
                                            }
                                            break;
                                        default:
                                            throw new NotImplementedException();
                                    }
                                }
                                break;
                            #endregion

                            #region Control Flow
                            case OpCodeREnum.Ret:
                                if (hasReturn)
                                {
                                    reg1 = Add(r, ip->Register1);
                                    CopyToStack(esp, reg1, mStack);
                                    esp++;
                                }
                                returned = true;
                                break;
                            case OpCodeREnum.Br_S:
                            case OpCodeREnum.Br:
                                ip = ptr + ip->Operand;
                                continue;
                            case OpCodeREnum.Brtrue:
                            case OpCodeREnum.Brtrue_S:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    bool res = false;
                                    switch (reg1->ObjectType)
                                    {
                                        case ObjectTypes.Integer:
                                            res = reg1->Value != 0;
                                            break;
                                        case ObjectTypes.Long:
                                            res = *(long*)&reg1->Value != 0;
                                            break;
                                        case ObjectTypes.Object:
                                            res = mStack[reg1->Value] != null;
                                            break;
                                    }
                                    if (res)
                                    {
                                        ip = ptr + ip->Operand;
                                        continue;
                                    }
                                }
                                break;
                            case OpCodeREnum.Brfalse:
                            case OpCodeREnum.Brfalse_S:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    bool res = false;
                                    switch (reg1->ObjectType)
                                    {
                                        case ObjectTypes.Null:
                                            res = true;
                                            break;
                                        case ObjectTypes.Integer:
                                            res = reg1->Value == 0;
                                            break;
                                        case ObjectTypes.Long:
                                            res = *(long*)&reg1->Value == 0;
                                            break;
                                        case ObjectTypes.Object:
                                            res = mStack[reg1->Value] == null;
                                            break;
                                    }
                                    if (res)
                                    {
                                        ip = ptr + ip->Operand;
                                        continue;
                                    }
                                }
                                break;
                            case OpCodeREnum.Blt:
                            case OpCodeREnum.Blt_S:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg2 = Add(r, ip->Register2);
                                    bool transfer = false;
                                    switch (reg1->ObjectType)
                                    {
                                        case ObjectTypes.Integer:
                                            transfer = reg1->Value < reg2->Value;
                                            break;
                                        case ObjectTypes.Long:
                                            transfer = *(long*)&reg1->Value < *(long*)&reg2->Value;
                                            break;
                                        case ObjectTypes.Float:
                                            transfer = *(float*)&reg1->Value < *(float*)&reg2->Value;
                                            break;
                                        case ObjectTypes.Double:
                                            transfer = *(double*)&reg1->Value < *(double*)&reg2->Value;
                                            break;
                                        default:
                                            throw new NotImplementedException();
                                    }

                                    if (transfer)
                                    {
                                        ip = ptr + ip->Operand;
                                        continue;
                                    }

                                }
                                break;
                            case OpCodeREnum.Bge:
                            case OpCodeREnum.Bge_S:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg2 = Add(r, ip->Register2);
                                    bool transfer = false;
                                    switch (reg1->ObjectType)
                                    {
                                        case ObjectTypes.Integer:
                                            transfer = reg1->Value >= reg2->Value;
                                            break;
                                        case ObjectTypes.Long:
                                            transfer = *(long*)&reg1->Value >= *(long*)&reg2->Value;
                                            break;
                                        case ObjectTypes.Float:
                                            transfer = *(float*)&reg1->Value >= *(float*)&reg2->Value;
                                            break;
                                        case ObjectTypes.Double:
                                            transfer = *(double*)&reg1->Value >= *(double*)&reg2->Value;
                                            break;
                                        default:
                                            throw new NotImplementedException();
                                    }

                                    if (transfer)
                                    {
                                        ip = ptr + ip->Operand;
                                        continue;
                                    }

                                }
                                break;
                            case OpCodeREnum.Call:
                            case OpCodeREnum.Callvirt:
                                {
                                    IMethod m = domain.GetMethod(ip->Operand);
                                    if (m == null)
                                    {
                                        //Irrelevant method
                                        int cnt = (int)ip->OperandLong;
                                        //Balance the stack
                                        for (int i = 0; i < cnt; i++)
                                        {
                                            Free(esp - 1);
                                            esp--;
                                        }
                                    }
                                    else
                                    {
                                        if (m is ILMethod)
                                        {
                                            ILMethod ilm = (ILMethod)m;
                                            bool processed = false;
                                            if (m.IsDelegateInvoke)
                                            {
                                                var instance = StackObject.ToObject((Minus(esp, m.ParameterCount + 1)), domain, mStack);
                                                if (instance is IDelegateAdapter)
                                                {
                                                    esp = ((IDelegateAdapter)instance).ILInvoke(this, esp, mStack);
                                                    processed = true;
                                                }
                                            }
                                            if (!processed)
                                            {
                                                if (code == OpCodeREnum.Callvirt)
                                                {
                                                    objRef = GetObjectAndResolveReference(Minus(esp, ilm.ParameterCount + 1));
                                                    if (objRef->ObjectType == ObjectTypes.Null)
                                                        throw new NullReferenceException();
                                                    if (objRef->ObjectType == ObjectTypes.ValueTypeObjectReference)
                                                    {
                                                        dst = *(StackObject**)&objRef->Value;
                                                        var ft = domain.GetType(dst->Value) as ILType;
                                                        ilm = ft.GetVirtualMethod(ilm) as ILMethod;
                                                    }
                                                    else
                                                    {
                                                        obj = mStack[objRef->Value];
                                                        if (obj == null)
                                                            throw new NullReferenceException();
                                                        ilm = ((ILTypeInstance)obj).Type.GetVirtualMethod(ilm) as ILMethod;
                                                    }
                                                }
                                                esp = ExecuteR(ilm, esp, out unhandledException);
                                                ValueTypeBasePointer = bp;
                                                if (unhandledException)
                                                    returned = true;
                                            }
                                        }
                                        else
                                        {
                                            CLRMethod cm = (CLRMethod)m;
                                            bool processed = false;
                                            if (cm.IsDelegateInvoke)
                                            {
                                                var instance = StackObject.ToObject((Minus(esp, cm.ParameterCount + 1)), domain, mStack);
                                                if (instance is IDelegateAdapter)
                                                {
                                                    esp = ((IDelegateAdapter)instance).ILInvoke(this, esp, mStack);
                                                    processed = true;
                                                }
                                            }

                                            if (!processed)
                                            {
                                                var redirect = cm.Redirection;
                                                if (redirect != null)
                                                    esp = redirect(this, esp, mStack, cm, false);
                                                else
                                                {
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
                                                    if (!allowUnboundCLRMethod)
                                                        throw new NotSupportedException(cm.ToString() + " is not bound!");
#endif
#if DEBUG && !NO_PROFILER
                                                    if (System.Threading.Thread.CurrentThread.ManagedThreadId == AppDomain.UnityMainThreadID)

#if UNITY_5_5_OR_NEWER
                                                        UnityEngine.Profiling.Profiler.BeginSample(cm.ToString());
#else
                                                        UnityEngine.Profiler.BeginSample(cm.ToString());
#endif
#endif
                                                    object result = cm.Invoke(this, esp, mStack);
#if DEBUG && !NO_PROFILER
                                                    if (System.Threading.Thread.CurrentThread.ManagedThreadId == AppDomain.UnityMainThreadID)
#if UNITY_5_5_OR_NEWER
                                                        UnityEngine.Profiling.Profiler.EndSample();
#else
                                                        UnityEngine.Profiler.EndSample();
#endif

#endif
                                                    if (result is CrossBindingAdaptorType)
                                                        result = ((CrossBindingAdaptorType)result).ILInstance;
                                                    int paramCount = cm.ParameterCount;
                                                    for (int i = 1; i <= paramCount; i++)
                                                    {
                                                        Free(Minus(esp, i));
                                                    }
                                                    esp = Minus(esp, paramCount);
                                                    if (cm.HasThis)
                                                    {
                                                        Free(esp - 1);
                                                        esp--;
                                                    }
                                                    if (cm.ReturnType != AppDomain.VoidType && !cm.IsConstructor)
                                                    {
                                                        esp = PushObject(esp, mStack, result, cm.ReturnType.TypeForCLR == typeof(object));
                                                    }
                                                }
                                            }
                                        }

                                        if (m.ReturnType != AppDomain.VoidType)
                                        {
                                            esp = PopToRegister(ref info, ip->Register1, esp);
                                        }
                                    }
                                }
                                break;
                            #endregion

                            #region FieldOperation
                            case OpCodeREnum.Stfld:
                                {
                                    reg2 = Add(r, ip->Register2); 
                                    objRef = GetObjectAndResolveReference(Add(r, ip->Register1));
                                    if (objRef->ObjectType == ObjectTypes.ValueTypeObjectReference)
                                    {
                                        dst = ILIntepreter.ResolveReference(objRef);
                                        var ft = domain.GetType(dst->Value);
                                        if (ft is ILType)
                                            CopyToValueTypeField(dst, (int)ip->OperandLong, reg2, mStack);
                                        else
                                            CopyToValueTypeField(dst, ((CLRType)ft).FieldIndexMapping[(int)ip->OperandLong], reg2, mStack);
                                    }
                                    else
                                    {
                                        obj = RetriveObject(objRef, mStack);

                                        if (obj != null)
                                        {
                                            if (obj is ILTypeInstance)
                                            {
                                                ILTypeInstance instance = obj as ILTypeInstance;
                                                instance.AssignFromStack((int)ip->OperandLong, reg2, AppDomain, mStack);
                                            }
                                            else
                                            {
                                                var t = obj.GetType();
                                                type = AppDomain.GetType((int)(ip->OperandLong >> 32));
                                                if (type != null)
                                                {
                                                    var fieldToken = (int)ip->OperandLong;
                                                    var f = ((CLRType)type).GetField(fieldToken);
                                                    if (!((CLRType)type).AssignFieldFromStack(fieldToken, ref obj, this, reg2, mStack))
                                                        ((CLRType)type).SetFieldValue(fieldToken, ref obj, f.FieldType.CheckCLRTypes(CheckAndCloneValueType(StackObject.ToObject(reg2, domain, mStack), domain)));
                                                    //Writeback
                                                    if (t.IsValueType)
                                                    {
                                                        switch (objRef->ObjectType)
                                                        {
                                                            case ObjectTypes.Object:
                                                                mStack[objRef->Value] = obj;
                                                                break;
                                                            case ObjectTypes.FieldReference:
                                                                {
                                                                    var oldObj = mStack[objRef->Value];
                                                                    int idx = objRef->ValueLow;
                                                                    if (oldObj is ILTypeInstance)
                                                                    {
                                                                        ((ILTypeInstance)oldObj)[idx] = obj;
                                                                    }
                                                                    else
                                                                    {
                                                                        var it = AppDomain.GetType(oldObj.GetType());
                                                                        ((CLRType)it).SetFieldValue(idx, ref oldObj, obj);
                                                                    }
                                                                }
                                                                break;
                                                            case ObjectTypes.StaticFieldReference:
                                                                {
                                                                    var it = AppDomain.GetType(objRef->Value);
                                                                    int idx = objRef->ValueLow;
                                                                    if (it is ILType)
                                                                    {
                                                                        ((ILType)it).StaticInstance[idx] = obj;
                                                                    }
                                                                    else
                                                                    {
                                                                        ((CLRType)it).SetStaticFieldValue(idx, obj);
                                                                    }
                                                                }
                                                                break;
                                                            case ObjectTypes.ValueTypeObjectReference:
                                                                {
                                                                    dst = ILIntepreter.ResolveReference(objRef);
                                                                    var ct = domain.GetType(dst->Value) as CLRType;
                                                                    var binder = ct.ValueTypeBinder;
                                                                    binder.CopyValueTypeToStack(obj, dst, mStack);
                                                                }
                                                                break;
                                                            default:
                                                                throw new NotImplementedException();
                                                        }
                                                    }
                                                }
                                                else
                                                    throw new TypeLoadException();
                                            }
                                        }
                                        else
                                            throw new NullReferenceException();
                                    }
                                }
                                break;
                            case OpCodeREnum.Ldfld:
                                {
                                    reg2 = Add(r, ip->Register2);
                                    objRef = GetObjectAndResolveReference(reg2);
                                    if (objRef->ObjectType == ObjectTypes.ValueTypeObjectReference)
                                    {
                                        dst = *(StackObject**)&objRef->Value;
                                        var ft = domain.GetType(dst->Value);
                                        if (ft is ILType)
                                            val = Minus(dst, (int)ip->OperandLong + 1);
                                        else
                                            val = Minus(dst, ((CLRType)ft).FieldIndexMapping[(int)ip->OperandLong] + 1);
                                        //TODO: Check master modification
                                        CopyToRegister(ref info, ip->Register1, dst);
                                    }
                                    else
                                    {
                                        obj = RetriveObject(objRef, mStack);
                                        if (obj != null)
                                        {
                                            if (obj is ILTypeInstance)
                                            {
                                                ILTypeInstance instance = obj as ILTypeInstance;
                                                instance.CopyToRegister((int)ip->OperandLong, ref info, ip->Register1);//Check #345
                                            }
                                            else
                                            {
                                                //var t = obj.GetType();
                                                type = AppDomain.GetType((int)(ip->OperandLong >> 32));
                                                if (type != null)
                                                {
                                                    var token = (int)ip->OperandLong;
                                                    ret = Add(r, ip->Register1);
                                                    if (!((CLRType)type).CopyFieldToStack(token, obj, this, ref ret, mStack))
                                                    {
                                                        var ft = ((CLRType)type).GetField(token);
                                                        obj = ((CLRType)type).GetFieldValue(token, obj);
                                                        if (obj is CrossBindingAdaptorType)
                                                            obj = ((CrossBindingAdaptorType)obj).ILInstance;

                                                        AssignToRegister(ref info, ip->Register1, obj, ft.FieldType == typeof(object));
                                                    }
                                                }
                                                else
                                                    throw new TypeLoadException();
                                            }
                                        }
                                        else
                                            throw new NullReferenceException();
                                    }
                                }
                                break;
                            case OpCodeREnum.Ldflda:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg2 = Add(r, ip->Register2);
                                    objRef = GetObjectAndResolveReference(reg2);
                                    if (objRef->ObjectType == ObjectTypes.ValueTypeObjectReference)
                                    {
                                        var ft = domain.GetType((int)(ip->OperandLong >> 32));
                                        StackObject* fieldAddr;
                                        if (ft is ILType)
                                        {
                                            fieldAddr = Minus(ILIntepreter.ResolveReference(objRef), (int)ip->OperandLong + 1);
                                        }
                                        else
                                        {
                                            fieldAddr = Minus(ILIntepreter.ResolveReference(objRef), ((CLRType)ft).FieldIndexMapping[(int)ip->OperandLong] + 1);
                                        }
                                        reg1->ObjectType = ObjectTypes.StackObjectReference;
                                        *(long*)&reg1->Value = (long)fieldAddr;
                                    }
                                    else
                                    {
                                        obj = RetriveObject(objRef, mStack);
                                        if (obj != null)
                                        {
                                            AssignToRegister(ref info, ip->Register1, obj);
                                            reg1->ObjectType = ObjectTypes.FieldReference;
                                            reg1->ValueLow = (int)ip->OperandLong;
                                        }
                                        else
                                            throw new NullReferenceException();
                                    }
                                }
                                break;
                            case OpCodeREnum.Stsfld:
                                {
                                    type = AppDomain.GetType((int)(ip->OperandLong >> 32));
                                    if (type != null)
                                    {
                                        reg1 = Add(r, ip->Register1);
                                        if (type is ILType)
                                        {
                                            ILType t = type as ILType;
                                            t.StaticInstance.AssignFromStack((int)ip->OperandLong, reg1, AppDomain, mStack);
                                        }
                                        else
                                        {
                                            CLRType t = type as CLRType;
                                            int idx = (int)ip->OperandLong;
                                            var f = t.GetField(idx);
                                            obj = null;
                                            if (!((CLRType)t).AssignFieldFromStack(idx, ref obj, this, reg1, mStack))
                                                t.SetStaticFieldValue(idx, f.FieldType.CheckCLRTypes(CheckAndCloneValueType(StackObject.ToObject(reg1, domain, mStack), domain)));                                            
                                        }
                                    }
                                    else
                                        throw new TypeLoadException();
                                }
                                break;
                            case OpCodeREnum.Ldsfld:
                                {
                                    type = AppDomain.GetType((int)(ip->OperandLong >> 32));
                                    if (type != null)
                                    {
                                        if (type is ILType)
                                        {
                                            ILType t = type as ILType;
                                            t.StaticInstance.CopyToRegister((int)ip->OperandLong,ref info, ip->Register1);
                                        }
                                        else
                                        {
                                            CLRType t = type as CLRType;
                                            int idx = (int)ip->OperandLong;
                                            if (!((CLRType)type).CopyFieldToStack(idx, null, this, ref esp, mStack))
                                            {
                                                var f = t.GetField(idx);
                                                obj = t.GetFieldValue(idx, null);
                                                if (obj is CrossBindingAdaptorType)
                                                    obj = ((CrossBindingAdaptorType)obj).ILInstance;
                                                AssignToRegister(ref info, ip->Register1, obj, f.FieldType == typeof(object));
                                            }
                                        }
                                    }
                                    else
                                        throw new TypeLoadException();
                                }
                                break;
                            case OpCodeREnum.Ldsflda:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg1->ObjectType = ObjectTypes.StaticFieldReference;
                                    reg1->Value = (int)(ip->OperandLong >> 32); 
                                    reg1->ValueLow = (int)(ip->OperandLong);
                                }
                                break;
                            #endregion

                            #region Initialization & Instantiation
                            case OpCodeREnum.Newobj:
                                {
                                    IMethod m = domain.GetMethod(ip->Operand);
                                    if (m is ILMethod)
                                    {
                                        type = m.DeclearingType as ILType;
                                        if (type.IsDelegate)
                                        {
                                            objRef = GetObjectAndResolveReference(esp - 1 - 1);
                                            var mi = (IMethod)mStack[(esp - 1)->Value];
                                            object ins;
                                            if (objRef->ObjectType == ObjectTypes.Null)
                                                ins = null;
                                            else
                                                ins = mStack[objRef->Value];
                                            Free(esp - 1);
                                            Free(esp - 1 - 1);
                                            esp = esp - 1 - 1;
                                            object dele;
                                            var ilMethod = mi as ILMethod;
                                            if (ilMethod != null)
                                            {
                                                if (ins != null)
                                                {
                                                    dele = ((ILTypeInstance)ins).GetDelegateAdapter(ilMethod);
                                                    if (dele == null)
                                                    {
                                                        var invokeMethod = type.GetMethod("Invoke", mi.ParameterCount);
                                                        dele = domain.DelegateManager.FindDelegateAdapter(
                                                            (ILTypeInstance)ins, ilMethod, invokeMethod);
                                                    }
                                                }
                                                else
                                                {
                                                    if (ilMethod.DelegateAdapter == null)
                                                    {
                                                        var invokeMethod = type.GetMethod("Invoke", mi.ParameterCount);
                                                        ilMethod.DelegateAdapter = domain.DelegateManager.FindDelegateAdapter(null, ilMethod, invokeMethod);
                                                    }
                                                    dele = ilMethod.DelegateAdapter;
                                                }
                                            }

                                            else
                                            {
                                                throw new NotImplementedException();
                                            }
                                            esp = PushObject(esp, mStack, dele);                                            
                                        }
                                        else
                                        {
                                            reg1 = esp - m.ParameterCount;
                                            obj = null;
                                            bool isValueType = type.IsValueType;
                                            if (isValueType)
                                            {
                                                throw new NotImplementedException();
                                                stack.AllocValueType(esp, type);
                                                objRef = esp + 1;
                                                objRef->ObjectType = ObjectTypes.StackObjectReference;
                                                *(StackObject**)&objRef->Value = esp;
                                                objRef++;
                                            }
                                            else
                                            {
                                                obj = ((ILType)type).Instantiate(false);
                                                objRef = PushObject(esp, mStack, obj);//this parameter for constructor
                                            }
                                            esp = objRef;
                                            for (int i = 0; i < m.ParameterCount; i++)
                                            {
                                                CopyToStack(esp, reg1 + i, mStack);
                                                esp++;
                                            }
                                            esp = ExecuteR((ILMethod)m, esp, out unhandledException);
                                            ValueTypeBasePointer = bp;
                                            if (isValueType)
                                            {
                                                var ins = objRef - 1 - 1;
                                                *reg1 = *ins;
                                                esp = reg1 + 1;
                                            }
                                            else
                                            {
                                                //PushToRegister(ref info, ip->Register1, obj);
                                                esp = PushObject(reg1, mStack, obj);//new constructedObj
                                            }
                                        }
                                        if (unhandledException)
                                            returned = true;
                                    }
                                    else
                                    {
                                        CLRMethod cm = (CLRMethod)m;
                                        //Means new object();
                                        if (cm == null)
                                        {
                                            esp = PushObject(esp, mStack, new object());
                                        }
                                        else
                                        {
                                            if (cm.DeclearingType.IsDelegate)
                                            {
                                                objRef = GetObjectAndResolveReference(esp - 1 - 1);
                                                var mi = (IMethod)mStack[(esp - 1)->Value];
                                                object ins;
                                                if (objRef->ObjectType == ObjectTypes.Null)
                                                    ins = null;
                                                else
                                                    ins = mStack[objRef->Value];
                                                Free(esp - 1);
                                                Free(esp - 1 - 1);
                                                esp = esp - 1 - 1;
                                                object dele;
                                                var ilMethod = mi as ILMethod;
                                                if (ilMethod != null)
                                                {
                                                    if (ins != null)
                                                    {
                                                        dele = ((ILTypeInstance)ins).GetDelegateAdapter(ilMethod);
                                                        if (dele == null)
                                                        {
                                                            var invokeMethod =
                                                                cm.DeclearingType.GetMethod("Invoke",
                                                                    mi.ParameterCount);
                                                            dele = domain.DelegateManager.FindDelegateAdapter(
                                                                (ILTypeInstance)ins, ilMethod, invokeMethod);
                                                        }
                                                    }
                                                    else
                                                    {
                                                        if (ilMethod.DelegateAdapter == null)
                                                        {
                                                            var invokeMethod = cm.DeclearingType.GetMethod("Invoke", mi.ParameterCount);
                                                            ilMethod.DelegateAdapter = domain.DelegateManager.FindDelegateAdapter(null, ilMethod, invokeMethod);
                                                        }
                                                        dele = ilMethod.DelegateAdapter;
                                                    }
                                                }
                                                else
                                                {
                                                    if (ins is ILTypeInstance)
                                                        ins = ((ILTypeInstance)ins).CLRInstance;
                                                    dele = Delegate.CreateDelegate(cm.DeclearingType.TypeForCLR, ins, ((CLRMethod)mi).MethodInfo);
                                                }
                                                esp = PushObject(esp, mStack, dele);
                                            }
                                            else
                                            {
                                                var redirect = cm.Redirection;
                                                if (redirect != null)
                                                    esp = redirect(this, esp, mStack, cm, true);
                                                else
                                                {
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
                                                    if (!allowUnboundCLRMethod)
                                                        throw new NotSupportedException(cm.ToString() + " is not bound!");
#endif
                                                    object result = cm.Invoke(this, esp, mStack, true);
                                                    int paramCount = cm.ParameterCount;
                                                    for (int i = 1; i <= paramCount; i++)
                                                    {
                                                        Free(esp - i);
                                                    }
                                                    esp = Minus(esp, paramCount);
                                                    esp = PushObject(esp, mStack, result);//new constructedObj
                                                }
                                            }
                                        }
                                    }
                                    esp = PopToRegister(ref info, ip->Register1, esp);
                                }
                                break;
                            case OpCodeREnum.Box:
                                {
                                    objRef = Add(r, ip->Register2);
                                    type = domain.GetType(ip->Operand);
                                    if (type != null)
                                    {
                                        if (type is ILType)
                                        {
                                            if (((ILType)type).IsEnum)
                                            {
                                                ILEnumTypeInstance ins = new Intepreter.ILEnumTypeInstance((ILType)type);
                                                ins.AssignFromStack(0, objRef, AppDomain, mStack);
                                                ins.Boxed = true;
                                                esp = PushObject(esp, mStack, ins, true);
                                            }
                                            else
                                            {
                                                switch (objRef->ObjectType)
                                                {
                                                    case ObjectTypes.Null:
                                                        break;
                                                    case ObjectTypes.ValueTypeObjectReference:
                                                        {
                                                            ILTypeInstance ins = ((ILType)type).Instantiate(false);
                                                            ins.AssignFromStack(objRef, domain, mStack);
                                                            //FreeStackValueType(obj);
                                                            esp = PushObject(esp, mStack, ins, true);
                                                        }
                                                        break;
                                                    default:
                                                        {
                                                            obj = mStack[objRef->Value];
                                                            //Free(obj);
                                                            if (type.IsArray)
                                                            {
                                                                esp = PushObject(esp, mStack, obj, true);
                                                            }
                                                            else
                                                            {
                                                                ILTypeInstance ins = (ILTypeInstance)obj;
                                                                if (ins != null)
                                                                {
                                                                    if (ins.IsValueType)
                                                                    {
                                                                        ins.Boxed = true;
                                                                    }
                                                                    esp = PushObject(esp, mStack, ins, true);
                                                                }
                                                                else
                                                                {
                                                                    esp = PushNull(esp);
                                                                }
                                                            }
                                                        }
                                                        break;
                                                }
                                            }
                                        }
                                        else
                                        {
                                            if (type.TypeForCLR.IsPrimitive)
                                            {
                                                var t = type.TypeForCLR;
                                                if (t == typeof(int))
                                                {
                                                    switch (objRef->ObjectType)
                                                    {
                                                        case ObjectTypes.Integer:
                                                            esp = PushObject(esp, mStack, objRef->Value, true);
                                                            break;
                                                        case ObjectTypes.Null:
                                                            esp = PushObject(esp, mStack, 0, true);
                                                            break;
                                                        case ObjectTypes.Object:
                                                            break;
                                                        default:
                                                            throw new NotImplementedException();
                                                    }
                                                }
                                                else if (t == typeof(bool))
                                                {
                                                    switch (objRef->ObjectType)
                                                    {
                                                        case ObjectTypes.Integer:
                                                            esp = PushObject(esp, mStack, (objRef->Value == 1), true);
                                                            break;
                                                        case ObjectTypes.Null:
                                                            esp = PushObject(esp, mStack, false, true);
                                                            break;
                                                        case ObjectTypes.Object:
                                                            break;
                                                        default:
                                                            throw new NotImplementedException();
                                                    }
                                                }
                                                else if (t == typeof(byte))
                                                {
                                                    switch (objRef->ObjectType)
                                                    {
                                                        case ObjectTypes.Integer:
                                                            esp = PushObject(esp, mStack, (byte)objRef->Value, true);
                                                            break;
                                                        case ObjectTypes.Null:
                                                            esp = PushObject(esp, mStack, 0L, true);
                                                            break;
                                                        case ObjectTypes.Object:
                                                            break;
                                                        default:
                                                            throw new NotImplementedException();
                                                    }
                                                }
                                                else if (t == typeof(short))
                                                {
                                                    switch (objRef->ObjectType)
                                                    {
                                                        case ObjectTypes.Integer:
                                                            esp = PushObject(objRef, mStack, (short)objRef->Value, true);
                                                            break;
                                                        case ObjectTypes.Null:
                                                            esp = PushObject(objRef, mStack, (short)0, true);
                                                            break;
                                                        case ObjectTypes.Object:
                                                            break;
                                                        default:
                                                            throw new NotImplementedException();
                                                    }
                                                }
                                                else if (t == typeof(long))
                                                {
                                                    switch (objRef->ObjectType)
                                                    {
                                                        case ObjectTypes.Long:
                                                            esp = PushObject(esp, mStack, *(long*)&objRef->Value, true);
                                                            break;
                                                        case ObjectTypes.Null:
                                                            esp = PushObject(esp, mStack, 0L, true);
                                                            break;
                                                        case ObjectTypes.Object:
                                                            break;
                                                        default:
                                                            throw new NotImplementedException();
                                                    }
                                                }
                                                else if (t == typeof(float))
                                                {
                                                    switch (objRef->ObjectType)
                                                    {
                                                        case ObjectTypes.Float:
                                                            esp = PushObject(esp, mStack, *(float*)&objRef->Value, true);
                                                            break;
                                                        case ObjectTypes.Null:
                                                            esp = PushObject(esp, mStack, 0f, true);
                                                            break;
                                                        case ObjectTypes.Object:
                                                            break;
                                                        default:
                                                            throw new NotImplementedException();
                                                    }
                                                }
                                                else if (t == typeof(double))
                                                {
                                                    switch (objRef->ObjectType)
                                                    {
                                                        case ObjectTypes.Double:
                                                            esp = PushObject(esp, mStack, *(double*)&objRef->Value, true);
                                                            break;
                                                        case ObjectTypes.Null:
                                                            esp = PushObject(esp, mStack, 0.0, true);
                                                            break;
                                                        case ObjectTypes.Object:
                                                            break;
                                                        default:
                                                            throw new NotImplementedException();
                                                    }
                                                }
                                                else if (t == typeof(char))
                                                {
                                                    switch (objRef->ObjectType)
                                                    {
                                                        case ObjectTypes.Integer:
                                                            esp = PushObject(esp, mStack, (char)objRef->Value, true);
                                                            break;
                                                        case ObjectTypes.Object:
                                                            break;
                                                        default:
                                                            throw new NotImplementedException();
                                                    }
                                                }
                                                else if (t == typeof(uint))
                                                {
                                                    switch (objRef->ObjectType)
                                                    {
                                                        case ObjectTypes.Integer:
                                                            esp = PushObject(esp, mStack, (uint)objRef->Value, true);
                                                            break;
                                                        case ObjectTypes.Null:
                                                            esp = PushObject(esp, mStack, (uint)0, true);
                                                            break;
                                                        case ObjectTypes.Object:
                                                            break;
                                                        default:
                                                            throw new NotImplementedException();
                                                    }
                                                }
                                                else if (t == typeof(ushort))
                                                {
                                                    switch (objRef->ObjectType)
                                                    {
                                                        case ObjectTypes.Integer:
                                                            esp = PushObject(esp, mStack, (ushort)objRef->Value, true);
                                                            break;
                                                        case ObjectTypes.Null:
                                                            esp = PushObject(esp, mStack, (ushort)0, true);
                                                            break;
                                                        case ObjectTypes.Object:
                                                            break;
                                                        default:
                                                            throw new NotImplementedException();
                                                    }
                                                }
                                                else if (t == typeof(ulong))
                                                {
                                                    switch (objRef->ObjectType)
                                                    {
                                                        case ObjectTypes.Long:
                                                            esp = PushObject(esp, mStack, *(ulong*)&objRef->Value, true);
                                                            break;
                                                        case ObjectTypes.Null:
                                                            esp = PushObject(esp, mStack, (ulong)0, true);
                                                            break;
                                                        case ObjectTypes.Object:
                                                            break;
                                                        default:
                                                            throw new NotImplementedException();
                                                    }
                                                }
                                                else if (t == typeof(sbyte))
                                                {
                                                    switch (objRef->ObjectType)
                                                    {
                                                        case ObjectTypes.Integer:
                                                            esp = PushObject(esp, mStack, (sbyte)objRef->Value, true);
                                                            break;
                                                        case ObjectTypes.Null:
                                                            esp = PushObject(esp, mStack, (sbyte)0, true);
                                                            break;
                                                        case ObjectTypes.Object:
                                                            break;
                                                        default:
                                                            throw new NotImplementedException();
                                                    }
                                                }
                                                else
                                                    throw new NotImplementedException();
                                            }
                                            else if (type.TypeForCLR.IsEnum)
                                            {
                                                esp = PushObject(esp, mStack, Enum.ToObject(type.TypeForCLR, StackObject.ToObject(objRef, AppDomain, mStack)), true);
                                            }
                                            else
                                            {
                                                if (objRef->ObjectType == ObjectTypes.ValueTypeObjectReference)
                                                {
                                                    dst = *(StackObject**)&objRef->Value;
                                                    var vt = domain.GetType(dst->Value);
                                                    if (vt != type)
                                                        throw new InvalidCastException();
                                                    object ins = ((CLRType)vt).ValueTypeBinder.ToObject(dst, mStack);
                                                    FreeStackValueType(objRef);
                                                    esp = PushObject(esp, mStack, ins, true);
                                                }
                                                //nothing to do for CLR type boxing
                                            }
                                        }
                                    }
                                    else
                                        throw new NullReferenceException();

                                    esp = PopToRegister(ref info, ip->Register1, esp);
                                }
                                break;
                            case OpCodeREnum.Unbox:
                            case OpCodeREnum.Unbox_Any:
                                {
                                    objRef = Add(r, ip->Register2);
                                    if (objRef->ObjectType == ObjectTypes.Object)
                                    {
                                        obj = mStack[objRef->Value];
                                        if (obj != null)
                                        {
                                            var t = domain.GetType(ip->Operand);
                                            if (t != null)
                                            {
                                                clrType = t.TypeForCLR;
                                                bool isEnumObj = obj is ILEnumTypeInstance;
                                                if ((t is CLRType) && clrType.IsPrimitive && !isEnumObj)
                                                {
                                                    reg1 = Add(r, ip->Register1);
                                                    if (clrType == typeof(int))
                                                    {
                                                        intVal = obj.ToInt32();
                                                        reg1->ObjectType = ObjectTypes.Integer;
                                                        reg1->Value = intVal;
                                                    }
                                                    else if (clrType == typeof(bool))
                                                    {
                                                        var boolVal = (bool)obj;
                                                        reg1->ObjectType = ObjectTypes.Integer;
                                                        reg1->Value = boolVal ? 1 : 0;
                                                    }
                                                    else if (clrType == typeof(short))
                                                    {
                                                        short shortVal = obj.ToInt16();
                                                        reg1->ObjectType = ObjectTypes.Integer;
                                                        reg1->Value = shortVal;
                                                    }
                                                    else if (clrType == typeof(long))
                                                    {
                                                        longVal = obj.ToInt64();
                                                        reg1->ObjectType = ObjectTypes.Long;
                                                        *(long*)&reg1->Value = longVal;
                                                    }
                                                    else if (clrType == typeof(float))
                                                    {
                                                        floatVal = obj.ToFloat();
                                                        reg1->ObjectType = ObjectTypes.Float;
                                                        *(float*)&reg1->Value = floatVal;
                                                    }
                                                    else if (clrType == typeof(byte))
                                                    {
                                                        byte bVal = (byte)obj;
                                                        reg1->ObjectType = ObjectTypes.Integer;
                                                        reg1->Value = bVal;
                                                    }
                                                    else if (clrType == typeof(double))
                                                    {
                                                        doubleVal = obj.ToDouble();
                                                        reg1->ObjectType = ObjectTypes.Double;
                                                        *(double*)&reg1->Value = doubleVal;
                                                    }
                                                    else if (clrType == typeof(char))
                                                    {
                                                        char cVal = (char)obj;
                                                        reg1->ObjectType = ObjectTypes.Integer;
                                                        *(char*)&reg1->Value = cVal;
                                                    }
                                                    else if (clrType == typeof(uint))
                                                    {
                                                        uint uVal = (uint)obj;
                                                        reg1->ObjectType = ObjectTypes.Integer;
                                                        reg1->Value = (int)uVal;
                                                    }
                                                    else if (clrType == typeof(ushort))
                                                    {
                                                        ushort usVal = (ushort)obj;
                                                        reg1->ObjectType = ObjectTypes.Integer;
                                                        reg1->Value = usVal;
                                                    }
                                                    else if (clrType == typeof(ulong))
                                                    {
                                                        ulong ulVal = (ulong)obj;
                                                        reg1->ObjectType = ObjectTypes.Long;
                                                        *(ulong*)&reg1->Value = ulVal;
                                                    }
                                                    else if (clrType == typeof(sbyte))
                                                    {
                                                        sbyte sbVal = (sbyte)obj;
                                                        reg1->ObjectType = ObjectTypes.Integer;
                                                        reg1->Value = sbVal;
                                                    }
                                                    else
                                                        throw new NotImplementedException();
                                                }
                                                else if (t.IsValueType)
                                                {
                                                    if (obj is ILTypeInstance)
                                                    {
                                                        var res = ((ILTypeInstance)obj);
                                                        if (res is ILEnumTypeInstance)
                                                        {
                                                            res.CopyToRegister(0, ref info, ip->Register1);
                                                        }
                                                        else
                                                        {
                                                            if (res.Boxed)
                                                            {
                                                                res = res.Clone();
                                                                res.Boxed = false;
                                                            }
                                                            AssignToRegister(ref info, ip->Register1, res);
                                                        }
                                                    }
                                                    else
                                                        AssignToRegister(ref info, ip->Register1, obj);

                                                }
                                                else
                                                {
                                                    AssignToRegister(ref info, ip->Register1, obj);
                                                }
                                            }
                                            else
                                                throw new TypeLoadException();
                                        }
                                        else
                                            throw new NullReferenceException();
                                    }
                                    else if (objRef->ObjectType < ObjectTypes.StackObjectReference)
                                    {
                                        //Nothing to do with primitive types
                                    }
                                    else
                                        throw new InvalidCastException();
                                }
                                break;
                            case OpCodeREnum.Isinst:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg2 = Add(r, ip->Register2);
                                    type = domain.GetType(ip->Operand);
                                    if (type != null)
                                    {
                                        objRef = GetObjectAndResolveReference(reg2);
                                        if (objRef->ObjectType <= ObjectTypes.Double)
                                        {
                                            var tclr = type.TypeForCLR;
                                            switch (objRef->ObjectType)
                                            {
                                                case ObjectTypes.Integer:
                                                    {
                                                        if (tclr != typeof(int) && tclr != typeof(bool) && tclr != typeof(short) && tclr != typeof(byte) && tclr != typeof(ushort) && tclr != typeof(uint))
                                                        {
                                                            WriteNull(reg1);
                                                        }
                                                    }
                                                    break;
                                                case ObjectTypes.Long:
                                                    {
                                                        if (tclr != typeof(long) && tclr != typeof(ulong))
                                                        {
                                                            WriteNull(reg1);
                                                        }
                                                    }
                                                    break;
                                                case ObjectTypes.Float:
                                                    {
                                                        if (tclr != typeof(float))
                                                        {
                                                            WriteNull(reg1);
                                                        }
                                                    }
                                                    break;
                                                case ObjectTypes.Double:
                                                    {
                                                        if (tclr != typeof(double))
                                                        {
                                                            WriteNull(reg1);
                                                        }
                                                    }
                                                    break;
                                                case ObjectTypes.Null:
                                                    WriteNull(reg1);
                                                    break;
                                            }
                                        }
                                        else
                                        {
                                            obj = RetriveObject(objRef, mStack);

                                            if (obj != null)
                                            {
                                                if (obj is ILTypeInstance)
                                                {
                                                    if (((ILTypeInstance)obj).CanAssignTo(type))
                                                    {
                                                        AssignToRegister(ref info, ip->Register1, obj);
                                                    }
                                                    else
                                                    {
                                                        WriteNull(reg1);
                                                    }
                                                }
                                                else
                                                {
                                                    if (type.TypeForCLR.IsAssignableFrom(obj.GetType()))
                                                    {
                                                        AssignToRegister(ref info, ip->Register1, obj, true);
                                                    }
                                                    else
                                                    {
                                                        WriteNull(reg1);
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                WriteNull(reg1);
                                            }
                                        }
                                    }
                                    else
                                        throw new NullReferenceException();
                                }
                                break;

                            case OpCodeREnum.Ldftn:
                                {
                                    IMethod m = domain.GetMethod(ip->Operand);
                                    AssignToRegister(ref info, ip->Register1, m);
                                }
                                break;
                            case OpCodeREnum.Ldvirtftn:
                                {
                                    IMethod m = domain.GetMethod(ip->Operand);
                                    objRef = Add(r, ip->Register2);
                                    if (m is ILMethod)
                                    {
                                        ILMethod ilm = (ILMethod)m;

                                        obj = mStack[objRef->Value];
                                        m = ((ILTypeInstance)obj).Type.GetVirtualMethod(ilm) as ILMethod;
                                    }
                                    else
                                    {
                                        obj = mStack[objRef->Value];
                                        if (obj is ILTypeInstance)
                                            m = ((ILTypeInstance)obj).Type.GetVirtualMethod(m);
                                        else if (obj is CrossBindingAdaptorType)
                                        {
                                            m = ((CrossBindingAdaptorType)obj).ILInstance.Type.BaseType.GetVirtualMethod(m);
                                        }
                                    }
                                    AssignToRegister(ref info, ip->Register1, m);
                                }
                                break;
                            #endregion

                            #region Compare
                            case OpCodeREnum.Ceq:
                                {
                                    reg1 = Add(r, ip->Register2);
                                    reg2 = Add(r, ip->Register3);
                                    reg3 = Add(r, ip->Register1);
                                    bool res = false;
                                    if (reg1->ObjectType == reg2->ObjectType)
                                    {
                                        switch (reg1->ObjectType)
                                        {
                                            case ObjectTypes.Integer:
                                            case ObjectTypes.Float:
                                                res = reg1->Value == reg2->Value;
                                                break;
                                            case ObjectTypes.Object:
                                                res = mStack[reg1->Value] == mStack[reg2->Value];
                                                break;
                                            case ObjectTypes.FieldReference:
                                                res = mStack[reg1->Value] == mStack[reg2->Value] && reg1->ValueLow == reg2->ValueLow;
                                                break;
                                            case ObjectTypes.Null:
                                                res = true;
                                                break;
                                            default:
                                                res = reg1->Value == reg2->Value && reg1->ValueLow == reg2->ValueLow;
                                                break;
                                        }
                                    }
                                    else
                                    {
                                        switch (reg1->ObjectType)
                                        {
                                            case ObjectTypes.Object:
                                                res = mStack[reg1->Value] == null && reg2->ObjectType == ObjectTypes.Null;
                                                break;
                                            case ObjectTypes.Null:
                                                res = reg1->ObjectType == ObjectTypes.Object && mStack[reg2->Value] == null;
                                                break;
                                        }
                                    }
                                    if (res)
                                        WriteOne(reg3);
                                    else
                                        WriteZero(reg3);

                                }
                                break;
                            case OpCodeREnum.Clt:
                                {
                                    reg1 = Add(r, ip->Register2);
                                    reg2 = Add(r, ip->Register3);
                                    reg3 = Add(r, ip->Register1);
                                    bool res = false;
                                    switch (reg1->ObjectType)
                                    {
                                        case ObjectTypes.Integer:
                                            res = reg1->Value < reg2->Value;
                                            break;
                                        case ObjectTypes.Long:
                                            res = *(long*)&reg1->Value < *(long*)&reg2->Value;
                                            break;
                                        case ObjectTypes.Float:
                                            res = *(float*)&reg1->Value < *(float*)&reg2->Value;
                                            break;
                                        case ObjectTypes.Double:
                                            res = *(double*)&reg1->Value < *(double*)&reg2->Value;
                                            break;
                                        default:
                                            throw new NotImplementedException();
                                    }
                                    if (res)
                                        WriteOne(reg3);
                                    else
                                        WriteZero(reg3);
                                }
                                break;
                            case OpCodeREnum.Clt_Un:
                                {
                                    reg1 = Add(r, ip->Register2);
                                    reg2 = Add(r, ip->Register3);
                                    reg3 = Add(r, ip->Register1);
                                    bool res = false;
                                    switch (reg1->ObjectType)
                                    {
                                        case ObjectTypes.Integer:
                                            res = (uint)reg1->Value < (uint)reg2->Value && reg2->ObjectType != ObjectTypes.Null;
                                            break;
                                        case ObjectTypes.Long:
                                            res = (ulong)*(long*)&reg1->Value < (ulong)*(long*)&reg2->Value && reg2->ObjectType != ObjectTypes.Null;
                                            break;
                                        case ObjectTypes.Float:
                                            res = *(float*)&reg1->Value < *(float*)&reg2->Value && reg2->ObjectType != ObjectTypes.Null;
                                            break;
                                        case ObjectTypes.Double:
                                            res = *(double*)&reg1->Value < *(double*)&reg2->Value && reg2->ObjectType != ObjectTypes.Null;
                                            break;
                                        default:
                                            throw new NotImplementedException();
                                    }
                                    if (res)
                                        WriteOne(reg3);
                                    else
                                        WriteZero(reg3);
                                }
                                break;
                            case OpCodeREnum.Cgt:
                                {
                                    reg1 = Add(r, ip->Register2);
                                    reg2 = Add(r, ip->Register3);
                                    reg3 = Add(r, ip->Register1);
                                    bool res = false;
                                    switch (reg1->ObjectType)
                                    {
                                        case ObjectTypes.Integer:
                                            res = reg1->Value > reg2->Value || reg2->ObjectType == ObjectTypes.Null;
                                            break;
                                        case ObjectTypes.Long:
                                            res = *(long*)&reg1->Value > *(long*)&reg2->Value || reg2->ObjectType == ObjectTypes.Null;
                                            break;
                                        case ObjectTypes.Float:
                                            res = *(float*)&reg1->Value > *(float*)&reg2->Value || reg2->ObjectType == ObjectTypes.Null;
                                            break;
                                        case ObjectTypes.Double:
                                            res = *(double*)&reg1->Value > *(double*)&reg2->Value || reg2->ObjectType == ObjectTypes.Null;
                                            break;
                                        default:
                                            throw new NotImplementedException();
                                    }
                                    if (res)
                                        WriteOne(reg3);
                                    else
                                        WriteZero(reg3);
                                }
                                break;
                            case OpCodeREnum.Cgt_Un:
                                {
                                    reg1 = Add(r, ip->Register2);
                                    reg2 = Add(r, ip->Register3);
                                    reg3 = Add(r, ip->Register1);
                                    bool res = false;
                                    switch (reg1->ObjectType)
                                    {
                                        case ObjectTypes.Integer:
                                            res = ((uint)reg1->Value > (uint)reg2->Value) || reg2->ObjectType == ObjectTypes.Null;
                                            break;
                                        case ObjectTypes.Long:
                                            res = (ulong)*(long*)&reg1->Value > (ulong)*(long*)&reg2->Value || reg2->ObjectType == ObjectTypes.Null;
                                            break;
                                        case ObjectTypes.Float:
                                            res = *(float*)&reg1->Value > *(float*)&reg2->Value || reg2->ObjectType == ObjectTypes.Null;
                                            break;
                                        case ObjectTypes.Double:
                                            res = *(double*)&reg1->Value > *(double*)&reg2->Value || reg2->ObjectType == ObjectTypes.Null;
                                            break;
                                        case ObjectTypes.Object:
                                            res = mStack[reg1->Value] != null && reg2->ObjectType == ObjectTypes.Null;
                                            break;
                                        case ObjectTypes.Null:
                                            res = false;
                                            break;
                                        default:
                                            throw new NotImplementedException();
                                    }
                                    if (res)
                                        WriteOne(reg3);
                                    else
                                        WriteZero(reg3);
                                }
                                break;
                            #endregion

                            #region Array
                            case OpCodeREnum.Newarr:
                                {
                                    reg2 = Add(r, ip->Register2);
                                    type = domain.GetType(ip->Operand);
                                    object arr = null;
                                    if (type != null)
                                    {
                                        if (type.TypeForCLR != typeof(ILTypeInstance))
                                        {
                                            if (type is CLRType)
                                            {
                                                arr = ((CLRType)type).CreateArrayInstance(reg2->Value);
                                            }
                                            else
                                            {
                                                arr = Array.CreateInstance(type.TypeForCLR, reg2->Value);
                                            }

                                            //Register Type
                                            AppDomain.GetType(arr.GetType());
                                        }
                                        else
                                        {
                                            arr = new ILTypeInstance[reg2->Value];
                                            ILTypeInstance[] ilArr = (ILTypeInstance[])arr;
                                            if (type.IsValueType)
                                            {
                                                for (int i = 0; i < reg2->Value; i++)
                                                {
                                                    ilArr[i] = ((ILType)type).Instantiate(true);
                                                }
                                            }
                                        }
                                    }
                                    AssignToRegister(ref info, ip->Register1, arr);
                                }
                                break;
                            case OpCodeREnum.Stelem_Ref:
                            case OpCodeREnum.Stelem_Any:
                                {
                                    reg1 = Add(r, ip->Register3);
                                    reg2 = Add(r, ip->Register2);
                                    reg3 = Add(r, ip->Register1);

                                    val = GetObjectAndResolveReference(reg1);
                                    Array arr = mStack[reg3->Value] as Array;

                                    if (arr is object[])
                                    {
                                        switch (val->ObjectType)
                                        {
                                            case ObjectTypes.Null:
                                                arr.SetValue(null, reg2->Value);
                                                break;
                                            case ObjectTypes.Object:
                                                ArraySetValue(arr, mStack[val->Value], reg2->Value);
                                                break;
                                            case ObjectTypes.Integer:
                                                arr.SetValue(val->Value, reg2->Value);
                                                break;
                                            case ObjectTypes.Long:
                                                arr.SetValue(*(long*)&val->Value, reg2->Value);
                                                break;
                                            case ObjectTypes.Float:
                                                arr.SetValue(*(float*)&val->Value, reg2->Value);
                                                break;
                                            case ObjectTypes.Double:
                                                arr.SetValue(*(double*)&val->Value, reg2->Value);
                                                break;
                                            case ObjectTypes.ValueTypeObjectReference:
                                                ArraySetValue(arr, StackObject.ToObject(val, domain, mStack), reg2->Value);
                                                FreeStackValueType(esp - 1);
                                                break;
                                            default:
                                                throw new NotImplementedException();
                                        }
                                    }
                                    else
                                    {
                                        switch (val->ObjectType)
                                        {
                                            case ObjectTypes.Object:
                                                ArraySetValue(arr, mStack[val->Value], reg2->Value);
                                                break;
                                            case ObjectTypes.Integer:
                                                {
                                                    StoreIntValueToArray(arr, val, reg2);
                                                }
                                                break;
                                            case ObjectTypes.Long:
                                                {
                                                    if (arr is long[])
                                                    {
                                                        ((long[])arr)[reg2->Value] = *(long*)&val->Value;
                                                    }
                                                    else
                                                    {
                                                        ((ulong[])arr)[reg2->Value] = *(ulong*)&val->Value;
                                                    }
                                                }
                                                break;
                                            case ObjectTypes.Float:
                                                {
                                                    ((float[])arr)[reg2->Value] = *(float*)&val->Value;
                                                }
                                                break;
                                            case ObjectTypes.Double:
                                                {
                                                    ((double[])arr)[reg2->Value] = *(double*)&val->Value;
                                                }
                                                break;
                                            case ObjectTypes.ValueTypeObjectReference:
                                                ArraySetValue(arr, StackObject.ToObject(val, domain, mStack), reg2->Value);
                                                FreeStackValueType(esp - 1);
                                                break;
                                            default:
                                                throw new NotImplementedException();
                                        }
                                    }
                                }
                                break;
                            case OpCodeREnum.Stelem_I1:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg2 = Add(r, ip->Register2);
                                    reg3 = Add(r, ip->Register3);

                                    byte[] arr = mStack[reg1->Value] as byte[];
                                    if (arr != null)
                                    {
                                        arr[reg2->Value] = (byte)reg3->Value;
                                    }
                                    else
                                    {
                                        bool[] arr2 = mStack[reg1->Value] as bool[];
                                        if (arr2 != null)
                                        {
                                            arr2[reg2->Value] = reg3->Value == 1;
                                        }
                                        else
                                        {
                                            sbyte[] arr3 = mStack[reg1->Value] as sbyte[];
                                            arr3[reg2->Value] = (sbyte)reg3->Value;
                                        }
                                    }
                                }
                                break;
                            case OpCodeREnum.Stelem_I2:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg2 = Add(r, ip->Register2);
                                    reg3 = Add(r, ip->Register3);

                                    short[] arr = mStack[reg1->Value] as short[];
                                    if (arr != null)
                                    {
                                        arr[reg2->Value] = (short)reg3->Value;
                                    }
                                    else
                                    {
                                        ushort[] arr2 = mStack[reg1->Value] as ushort[];
                                        if(arr2 != null)
                                        {
                                            arr2[reg2->Value] = (ushort)reg3->Value;
                                        }
                                        else
                                        {
                                            char[] arr3 = mStack[reg1->Value] as char[];
                                            arr3[reg2->Value] = (char)reg3->Value;
                                        }
                                    }
                                }
                                break;
                            case OpCodeREnum.Stelem_I4:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg2 = Add(r, ip->Register2);
                                    reg3 = Add(r, ip->Register3);

                                    int[] arr = mStack[reg1->Value] as int[];
                                    if (arr != null)
                                    {
                                        arr[reg2->Value] = reg3->Value;
                                    }
                                    else
                                    {
                                        uint[] arr2 = mStack[reg1->Value] as uint[];
                                        arr2[reg2->Value] = (uint)reg3->Value;
                                    }
                                }
                                break;
                            case OpCodeREnum.Stelem_R4:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg2 = Add(r, ip->Register2);
                                    reg3 = Add(r, ip->Register3);

                                    float[] arr = mStack[reg1->Value] as float[];
                                    arr[reg2->Value] = *(float*)(&reg3->Value);
                                }
                                break;
                            case OpCodeREnum.Stelem_I8:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg2 = Add(r, ip->Register2);
                                    reg3 = Add(r, ip->Register3);

                                    long[] arr = mStack[reg1->Value] as long[];
                                    if (arr != null)
                                    {
                                        arr[reg2->Value] = *(long*)(&reg3->Value);
                                    }
                                    else
                                    {
                                        ulong[] arr2 = mStack[reg1->Value] as ulong[];
                                        arr2[reg2->Value] = *(ulong*)(&reg3->Value);
                                    }
                                }
                                break;
                            case OpCodeREnum.Stelem_R8:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg2 = Add(r, ip->Register2);
                                    reg3 = Add(r, ip->Register3);

                                    double[] arr = mStack[reg1->Value] as double[];
                                    arr[reg2->Value] = *(double*)(&reg3->Value);
                                }
                                break;
                            case OpCodeREnum.Ldlen:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg2 = Add(r, ip->Register2);
                                    Array arr = mStack[reg2->Value] as Array;

                                    reg1->ObjectType = ObjectTypes.Integer;
                                    reg1->Value = arr.Length;
                                }
                                break;
                            case OpCodeREnum.Ldelema:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg2 = Add(r, ip->Register2);
                                    reg3 = Add(r, ip->Register3);

                                    Array arr = mStack[reg2->Value] as Array;
                                    var idx = reg3->Value;

                                    reg1->ObjectType = ObjectTypes.ArrayReference;
                                    reg1->Value = mStack.Count;
                                    mStack.Add(arr);
                                    reg1->ValueLow = idx;
                                }
                                break;
                            case OpCodeREnum.Ldelem_I1:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg2 = Add(r, ip->Register2);
                                    reg3 = Add(r, ip->Register3);

                                    bool[] arr = mStack[reg2->Value] as bool[];
                                    if (arr != null)
                                    {
                                        reg1->ObjectType = ObjectTypes.Integer;
                                        reg1->Value = arr[reg3->Value] ? 1 : 0;
                                    }
                                    else
                                    {
                                        sbyte[] arr2 = mStack[reg2->Value] as sbyte[];
                                        reg1->ObjectType = ObjectTypes.Integer;
                                        reg1->Value = arr2[reg3->Value];
                                    }
                                }
                                break;
                            case OpCodeREnum.Ldelem_U1:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg2 = Add(r, ip->Register2);
                                    reg3 = Add(r, ip->Register3);

                                    byte[] arr = mStack[reg2->Value] as byte[];
                                    if (arr != null)
                                    {
                                        reg1->ObjectType = ObjectTypes.Integer;
                                        reg1->Value = arr[reg3->Value];
                                    }
                                    else
                                    {
                                        bool[] arr2 = mStack[reg2->Value] as bool[];
                                        reg1->ObjectType = ObjectTypes.Integer;
                                        reg1->Value = arr2[reg3->Value] ? 1 : 0;
                                    }
                                }
                                break;
                            case OpCodeREnum.Ldelem_I2:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg2 = Add(r, ip->Register2);
                                    reg3 = Add(r, ip->Register3);

                                    short[] arr = mStack[reg2->Value] as short[];
                                    if (arr != null)
                                    {
                                        reg1->ObjectType = ObjectTypes.Integer;
                                        reg1->Value = arr[reg3->Value];
                                    }
                                    else
                                    {
                                        char[] arr2 = mStack[reg2->Value] as char[];
                                        reg1->ObjectType = ObjectTypes.Integer;
                                        reg1->Value = arr2[reg3->Value];
                                    }
                                }
                                break;
                            case OpCodeREnum.Ldelem_U2:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg2 = Add(r, ip->Register2);
                                    reg3 = Add(r, ip->Register3);

                                    ushort[] arr = mStack[reg2->Value] as ushort[];
                                    if (arr != null)
                                    {
                                        reg1->ObjectType = ObjectTypes.Integer;
                                        reg1->Value = arr[reg3->Value];
                                    }
                                    else
                                    {
                                        char[] arr2 = mStack[reg2->Value] as char[];
                                        reg1->ObjectType = ObjectTypes.Integer;
                                        reg1->Value = arr2[reg3->Value];
                                    }
                                }
                                break;
                            case OpCodeREnum.Ldelem_I4:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg2 = Add(r, ip->Register2);
                                    reg3 = Add(r, ip->Register3);

                                    int[] arr = mStack[reg2->Value] as int[];
                                    reg1->ObjectType = ObjectTypes.Integer;
                                    reg1->Value = arr[reg3->Value];
                                }
                                break;
                            case OpCodeREnum.Ldelem_U4:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg2 = Add(r, ip->Register2);
                                    reg3 = Add(r, ip->Register3);

                                    uint[] arr = mStack[reg2->Value] as uint[];
                                    reg1->ObjectType = ObjectTypes.Integer;
                                    reg1->Value = (int)arr[reg3->Value];
                                }
                                break;
                            case OpCodeREnum.Ldelem_I8:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg2 = Add(r, ip->Register2);
                                    reg3 = Add(r, ip->Register3);

                                    long[] arr = mStack[reg2->Value] as long[];
                                    if(arr != null)
                                    {
                                        reg1->ObjectType = ObjectTypes.Long;
                                        *(long*)(&reg1->Value) = arr[reg3->Value];
                                    }
                                    else
                                    {
                                        ulong[] arr2 = mStack[reg2->Value] as ulong[];
                                        reg1->ObjectType = ObjectTypes.Long;
                                        *(ulong*)(&reg1->Value) = arr2[reg3->Value];
                                    }
                                }
                                break;
                            case OpCodeREnum.Ldelem_R4:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg2 = Add(r, ip->Register2);
                                    reg3 = Add(r, ip->Register3);

                                    float[] arr = mStack[reg2->Value] as float[];
                                    reg1->ObjectType = ObjectTypes.Float;
                                    *(float*)&reg1->Value = arr[reg3->Value];
                                }
                                break;
                            case OpCodeREnum.Ldelem_R8:
                                {
                                    reg1 = Add(r, ip->Register1);
                                    reg2 = Add(r, ip->Register2);
                                    reg3 = Add(r, ip->Register3);

                                    double[] arr = mStack[reg2->Value] as double[];
                                    reg1->ObjectType = ObjectTypes.Double;
                                    *(double*)&reg1->Value = arr[reg3->Value];
                                }
                                break;
                            #endregion

                            case OpCodeREnum.Throw:
                                {
                                    objRef = GetObjectAndResolveReference(Add(r, ip->Register1));
                                    var ex = mStack[objRef->Value] as Exception;
                                    throw ex;
                                }
                            default:
                                throw new NotSupportedException("Not supported opcode " + code);
                        }
                        ip++;
                    }
                    catch (Exception ex)
                    {
                        if (method.ExceptionHandler != null)
                        {
                            int addr = (int)(ip - ptr);
                            var eh = GetCorrespondingExceptionHandler(method, ex, addr, ExceptionHandlerType.Catch, true);

                            if (eh == null)
                            {
                                eh = GetCorrespondingExceptionHandler(method, ex, addr, ExceptionHandlerType.Catch, false);
                            }
                            if (eh != null)
                            {
                                if (ex is ILRuntimeException)
                                {
                                    ILRuntimeException ire = (ILRuntimeException)ex;
                                    var inner = ire.InnerException;
                                    inner.Data["ThisInfo"] = ire.ThisInfo;
                                    inner.Data["StackTrace"] = ire.StackTrace;
                                    inner.Data["LocalInfo"] = ire.LocalInfo;
                                    ex = inner;
                                }
                                else
                                {
                                    var debugger = AppDomain.DebugService;
                                    if (method.HasThis)
                                        ex.Data["ThisInfo"] = debugger.GetThisInfo(this);
                                    else
                                        ex.Data["ThisInfo"] = "";
                                    ex.Data["StackTrace"] = debugger.GetStackTrace(this);
                                    ex.Data["LocalInfo"] = debugger.GetLocalVariableInfo(this);
                                }
                                //Clear call stack
                                while (stack.Frames.Peek().BasePointer != frame.BasePointer)
                                {
                                    var f = stack.Frames.Peek();
                                    esp = stack.PopFrame(ref f, esp);
                                    if (f.Method.ReturnType != AppDomain.VoidType)
                                    {
                                        Free(esp - 1);
                                        esp--;
                                    }
                                }
                                esp = PushObject(esp, mStack, ex);
                                unhandledException = false;
                                ip = ptr + eh.HandlerStart;
                                continue;
                            }
                        }
                        if (unhandledException)
                        {
                            throw ex;
                        }

                        unhandledException = true;
                        returned = true;
#if DEBUG && !DISABLE_ILRUNTIME_DEBUG
                        if (!AppDomain.DebugService.Break(this, ex))
#endif
                        {
                            var newEx = new ILRuntimeException(ex.Message, this, method, ex);
                            throw newEx;
                        }
                    }
                }
            }

#if UNITY_EDITOR
            if (System.Threading.Thread.CurrentThread.ManagedThreadId == AppDomain.UnityMainThreadID)
#if UNITY_5_5_OR_NEWER
                UnityEngine.Profiling.Profiler.EndSample();
#else
                UnityEngine.Profiler.EndSample();
#endif
#endif
            //ClearStack
            return stack.PopFrame(ref frame, esp);
        }

        internal void CopyToRegister(ref RegisterFrameInfo info, short reg, StackObject* val, IList<object> mStackSrc = null)
        {
            var argCnt = info.ParameterCount;
            var mStack = info.ManagedStack;
            var locCnt = info.LocalCount;
            if (mStackSrc == null)
                mStackSrc = mStack;

            if (reg < argCnt)
                throw new NotImplementedException();
            var v = Add(info.RegisterStart, reg);
            var idx = info.LocalManagedBase + (reg - argCnt);
                
            switch (val->ObjectType)
            {
                case ObjectTypes.Null:
                    v->ObjectType = ObjectTypes.Object;
                    v->Value = idx;
                    mStack[idx] = null;
                    break;
                case ObjectTypes.Object:
                case ObjectTypes.FieldReference:
                case ObjectTypes.ArrayReference:
                    if (v->ObjectType == ObjectTypes.ValueTypeObjectReference)
                    {
                        var obj = mStackSrc[val->Value];
                        if (obj is ILTypeInstance)
                        {
                            var dst = *(StackObject**)&v->Value;
                            ((ILTypeInstance)obj).CopyValueTypeToStack(dst, mStack);
                        }
                        else
                        {
                            var dst = *(StackObject**)&v->Value;
                            var ct = domain.GetType(dst->Value) as CLRType;
                            var binder = ct.ValueTypeBinder;
                            binder.CopyValueTypeToStack(obj, dst, mStack);
                        }
                    }
                    else
                    {
                        *v = *val;
                        mStack[idx] = CheckAndCloneValueType(mStackSrc[v->Value], domain);
                        v->Value = idx;
                    }
                    break;
                case ObjectTypes.ValueTypeObjectReference:
                    if (v->ObjectType == ObjectTypes.ValueTypeObjectReference)
                    {
                        CopyStackValueType(val, v, mStack);
                    }
                    else
                        throw new NotImplementedException();
                    FreeStackValueType(val);
                    break;
                default:
                    *v = *val;
                    mStack[idx] = null;
                    break;
            }
        }

        internal static void AssignToRegister(ref RegisterFrameInfo info, short reg, object obj, bool isBox = false)
        {
            var argCnt = info.ParameterCount;
            var mStack = info.ManagedStack;
            var locCnt = info.LocalCount;

            if (reg < argCnt)
                throw new NotImplementedException();
            var dst = Add(info.RegisterStart, reg);
            if (obj != null)
            {
                var idx = info.LocalManagedBase + (reg - argCnt);

                if (!isBox)
                {
                    var typeFlags = obj.GetType().GetTypeFlags();

                    if ((typeFlags & CLR.Utils.Extensions.TypeFlags.IsPrimitive) != 0)
                    {
                        UnboxObject(dst, obj, mStack);
                    }
                    else if ((typeFlags & CLR.Utils.Extensions.TypeFlags.IsEnum) != 0)
                    {
                        dst->ObjectType = ObjectTypes.Integer;
                        dst->Value = Convert.ToInt32(obj);
                    }
                    else
                    {
                        dst->ObjectType = ObjectTypes.Object;
                        dst->Value = idx;
                        mStack[idx] = obj;
                    }
                }
                else
                {
                    dst->ObjectType = ObjectTypes.Object;
                    dst->Value = idx;
                    mStack[idx] = obj;
                }
            }
            else
            {
                WriteNull(dst);
            }
        }

        StackObject* PopToRegister(ref RegisterFrameInfo info, short reg, StackObject* esp)
        {
            var val = esp - 1;
            CopyToRegister(ref info, reg, val);
            Free(val);
            return val;
        }

        public static void WriteOne(StackObject* esp)
        {
            esp->ObjectType = ObjectTypes.Integer;
            esp->Value = 1;
        }

        public static void WriteZero(StackObject* esp)
        {
            esp->ObjectType = ObjectTypes.Integer;
            esp->Value = 0;
        }

        public static void WriteNull(StackObject* esp)
        {
            esp->ObjectType = ObjectTypes.Null;
            esp->Value = -1;
            esp->ValueLow = 0;
        }
    }
}