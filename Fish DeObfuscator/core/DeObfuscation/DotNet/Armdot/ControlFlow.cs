using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Fish.Shared;

namespace Fish_DeObfuscator.core.DeObfuscation.DotNet.Armdot
{
    public class ControlFlow : IStage
    {
        #region Fields

        private Dictionary<IField, IMethod[]> functionPointerArrays = new Dictionary<IField, IMethod[]>();
        private HashSet<string> allObfuscatedMethodNames = new HashSet<string>();

        #endregion

        #region IStage

        private int deobfuscatedMethods = 0;

        public void Execute(IContext context)
        {
            var module = context.ModuleDefinition;
            AnalyzeFunctionPointerArrays(module);
            ProcessMethods(module);

            if (deobfuscatedMethods > 0)
                Logger.Detail($"Deobfuscated {deobfuscatedMethods} methods");
        }

        #endregion

        #region Control Flow Function Pointer Analysis

        private void AnalyzeFunctionPointerArrays(ModuleDefMD module)
        {
            foreach (var type in module.GetTypes())
            {
                foreach (var field in type.Fields)
                {
                    if (field.IsStatic && field.FieldType.IsSZArray)
                    {
                        var initMethod = FindArrayInitializationMethod(field, type);
                        if (initMethod != null)
                        {
                            var pointers = ExtractFunctionPointers(initMethod);
                            if (pointers.Any())
                            {
                                int maxIndex = pointers.Keys.Max();
                                var methodArray = new IMethod[maxIndex + 1];
                                foreach (var kvp in pointers)
                                    methodArray[kvp.Key] = kvp.Value;
                                functionPointerArrays[field] = methodArray;
                                foreach (var m in pointers.Values)
                                    if (m != null)
                                        allObfuscatedMethodNames.Add(m.FullName);
                            }
                        }
                    }
                }
            }
        }

        private MethodDef FindArrayInitializationMethod(IField field, TypeDef type)
        {
            var staticCtor = type.FindStaticConstructor();
            if (staticCtor != null && ReferencesField(staticCtor, field))
                return staticCtor;
            return type.Methods.FirstOrDefault(m => m.IsStatic && m.HasBody && ReferencesField(m, field) && InitializesArray(m, field));
        }

        private bool ReferencesField(MethodDef method, IField field)
        {
            return method.HasBody && method.Body.Instructions.Any(i => i.Operand is IField f && f.FullName == field.FullName);
        }

        private bool InitializesArray(MethodDef method, IField field)
        {
            if (!method.HasBody)
                return false;
            var instrs = method.Body.Instructions;
            bool newArr = false,
                stsfld = false;
            foreach (var instr in instrs)
            {
                if (instr.OpCode == OpCodes.Newarr)
                    newArr = true;
                if (instr.OpCode == OpCodes.Stsfld && instr.Operand is IField f && f.FullName == field.FullName)
                    stsfld = true;
            }
            return newArr && stsfld;
        }

        private Dictionary<int, IMethod> ExtractFunctionPointers(MethodDef method)
        {
            var pointers = new Dictionary<int, IMethod>();
            var instrs = method.Body.Instructions;
            for (int i = 0; i < instrs.Count - 3; i++)
            {
                if (instrs[i].OpCode == OpCodes.Ldsfld && Fish.Shared.Utils.IsIntegerConstant(instrs[i + 1]) && instrs[i + 2].OpCode == OpCodes.Ldftn && instrs[i + 3].OpCode == OpCodes.Stelem_I)
                {
                    int index = Fish.Shared.Utils.GetConstantValue(instrs[i + 1]);
                    if (instrs[i + 2].Operand is IMethod m)
                        pointers[index] = m;
                }
            }
            return pointers;
        }

        #endregion

        #region Control Flow Data Classes

        private class LoopInfo
        {
            public int DispatcherInstructionIndex;
            public int LoopStartIndex;
            public int LoopEndIndex;
            public Local StateVariable;
            public Local ConditionVariable;
            public int InitialState;
            public IField ArrayField;
            public int ExitValue = 0;
            public List<Instruction> Arguments;
            public int StateParameterIndex;
        }

        private class TransitionInfo
        {
            public bool IsExit;
            public bool IsConditional;
            public int NextState = -1;
            public int TrueState = -1;
            public int FalseState = -1;
            public Instruction SourceInstruction;
        }

        #endregion

        #region Control Flow Loop Detection

        private void ProcessMethods(ModuleDefMD module)
        {
            foreach (var type in module.GetTypes())
            foreach (var method in type.Methods)
                if (method.HasBody && DeobfuscateMethod(method))
                    deobfuscatedMethods++;
        }

        private bool DeobfuscateMethod(MethodDef method)
        {
            var loopInfo = FindControlFlowLoop(method);
            if (loopInfo != null)
                return ReconstructControlFlow(method, loopInfo);
            return false;
        }

        private LoopInfo FindControlFlowLoop(MethodDef method)
        {
            var instrs = method.Body.Instructions;
            for (int i = 0; i < instrs.Count; i++)
            {
                bool isDispatcher = false;
                IField arrayField = null;
                int argsEndIndex = -1;
                int paramCount = 0;

                if (instrs[i].OpCode == OpCodes.Calli)
                {
                    if (instrs[i].Operand is MethodSig sig)
                    {
                        paramCount = sig.Params.Count;
                        if (sig.HasThis)
                            paramCount++;
                    }
                    int ldelemIdx = -1;
                    for (int j = i - 1; j >= Math.Max(0, i - 20); j--)
                        if (instrs[j].OpCode == OpCodes.Ldelem_I)
                        {
                            ldelemIdx = j;
                            break;
                        }
                    if (ldelemIdx != -1)
                    {
                        for (int j = ldelemIdx - 1; j >= Math.Max(0, ldelemIdx - 10); j--)
                        {
                            if (instrs[j].OpCode == OpCodes.Ldsfld && instrs[j].Operand is IField f && functionPointerArrays.ContainsKey(f))
                            {
                                arrayField = f;
                                isDispatcher = true;
                                argsEndIndex = j;
                                break;
                            }
                        }
                    }
                }
                else if (instrs[i].OpCode == OpCodes.Call)
                {
                    if (instrs[i].Operand is IMethod target)
                    {
                        if (allObfuscatedMethodNames.Contains(target.FullName))
                        {
                            paramCount = target.MethodSig.Params.Count;
                            if (target.MethodSig != null && target.MethodSig.HasThis)
                                paramCount++;
                            foreach (var kvp in functionPointerArrays)
                            {
                                if (kvp.Value.Any(m => m != null && m.FullName == target.FullName))
                                {
                                    arrayField = kvp.Key;
                                    isDispatcher = true;
                                    argsEndIndex = i;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (isDispatcher && arrayField != null)
                {
                    var arguments = new List<Instruction>();
                    if (argsEndIndex != -1 && paramCount > 0)
                    {
                        int currentScan = argsEndIndex - 1;
                        for (int k = 0; k < paramCount; k++)
                            if (currentScan >= 0)
                            {
                                arguments.Insert(0, instrs[currentScan]);
                                currentScan--;
                            }
                    }

                    int loopStart = -1,
                        loopEnd = -1;
                    Local conditionVar = null;
                    int exitValue = 0;

                    for (int j = i; j < instrs.Count; j++)
                    {
                        if (instrs[j].OpCode == OpCodes.Br || instrs[j].OpCode == OpCodes.Br_S)
                        {
                            if (instrs[j].Operand is Instruction target && instrs.IndexOf(target) <= i)
                            {
                                loopStart = instrs.IndexOf(target);
                                loopEnd = j + 1;
                                for (int k = loopStart; k < i; k++)
                                {
                                    if (instrs[k].OpCode == OpCodes.Bne_Un || instrs[k].OpCode == OpCodes.Bne_Un_S)
                                    {
                                        if (k >= 2 && IsLoadLocal(instrs[k - 2]))
                                        {
                                            conditionVar = GetLocal(instrs[k - 2], method.Body.Variables);
                                            if (instrs[k - 1].IsLdcI4())
                                                exitValue = instrs[k - 1].GetLdcI4Value();
                                            break;
                                        }
                                    }
                                    else if ((instrs[k].OpCode == OpCodes.Brtrue || instrs[k].OpCode == OpCodes.Brtrue_S) && k >= 1 && IsLoadLocal(instrs[k - 1]))
                                    {
                                        conditionVar = GetLocal(instrs[k - 1], method.Body.Variables);
                                        exitValue = 0;
                                        break;
                                    }
                                    else if ((instrs[k].OpCode == OpCodes.Brfalse || instrs[k].OpCode == OpCodes.Brfalse_S) && k >= 1 && IsLoadLocal(instrs[k - 1]))
                                    {
                                        conditionVar = GetLocal(instrs[k - 1], method.Body.Variables);
                                        exitValue = 1;
                                        break;
                                    }
                                }
                                break;
                            }
                        }
                        else if ((instrs[j].OpCode == OpCodes.Brtrue || instrs[j].OpCode == OpCodes.Brtrue_S) && instrs[j].Operand is Instruction t1 && instrs.IndexOf(t1) <= i)
                        {
                            loopStart = instrs.IndexOf(t1);
                            loopEnd = j + 1;
                            if (j >= 1 && IsLoadLocal(instrs[j - 1]))
                            {
                                conditionVar = GetLocal(instrs[j - 1], method.Body.Variables);
                                exitValue = 0;
                            }
                            break;
                        }
                        else if ((instrs[j].OpCode == OpCodes.Brfalse || instrs[j].OpCode == OpCodes.Brfalse_S) && instrs[j].Operand is Instruction t2 && instrs.IndexOf(t2) <= i)
                        {
                            loopStart = instrs.IndexOf(t2);
                            loopEnd = j + 1;
                            if (j >= 1 && IsLoadLocal(instrs[j - 1]))
                            {
                                conditionVar = GetLocal(instrs[j - 1], method.Body.Variables);
                                exitValue = 1;
                            }
                            break;
                        }
                    }

                    if (loopStart != -1 && conditionVar != null)
                    {
                        Local stateVar = null;
                        for (int k = i - 1; k >= loopStart; k--)
                        {
                            if (instrs[k].OpCode == OpCodes.Ldloca || instrs[k].OpCode == OpCodes.Ldloca_S)
                            {
                                var loc = GetLocal(instrs[k], method.Body.Variables);
                                int init = GetInitialValue(method, loc, loopStart);
                                if (init != -1 && loc != conditionVar)
                                {
                                    stateVar = loc;
                                    break;
                                }
                            }
                            else if (instrs[k].OpCode == OpCodes.Ldloc || instrs[k].OpCode == OpCodes.Ldloc_S)
                            {
                                var loc = GetLocal(instrs[k], method.Body.Variables);
                                int init = GetInitialValue(method, loc, loopStart);
                                if (init != -1 && loc != conditionVar)
                                {
                                    stateVar = loc;
                                    break;
                                }
                            }
                        }

                        if (stateVar != null)
                        {
                            int initVal = GetInitialValue(method, stateVar, loopStart);
                            if (initVal != -1)
                            {
                                int stateParamIndex = 0;
                                if (arguments != null)
                                {
                                    for (int m = 0; m < arguments.Count; m++)
                                    {
                                        var argInstr = arguments[m];
                                        if ((argInstr.OpCode == OpCodes.Ldloca || argInstr.OpCode == OpCodes.Ldloca_S) && GetLocal(argInstr, method.Body.Variables) == stateVar)
                                        {
                                            stateParamIndex = m;
                                            break;
                                        }
                                    }
                                }
                                return new LoopInfo
                                {
                                    DispatcherInstructionIndex = i,
                                    LoopStartIndex = loopStart,
                                    LoopEndIndex = loopEnd,
                                    StateVariable = stateVar,
                                    ConditionVariable = conditionVar,
                                    InitialState = initVal,
                                    ArrayField = arrayField,
                                    ExitValue = exitValue,
                                    Arguments = arguments,
                                    StateParameterIndex = stateParamIndex,
                                };
                            }
                        }
                    }
                }
            }
            return null;
        }

        private int GetInitialValue(MethodDef method, Local local, int beforeIndex)
        {
            var instrs = method.Body.Instructions;
            for (int i = beforeIndex - 1; i >= 0; i--)
            {
                if (IsStoreLocal(instrs[i]) && GetLocal(instrs[i], method.Body.Variables) == local)
                    if (i > 0 && Fish.Shared.Utils.IsIntegerConstant(instrs[i - 1]))
                        return Fish.Shared.Utils.GetConstantValue(instrs[i - 1]);
            }
            return -1;
        }

        #endregion

        #region Control Flow State Machine Analysis

        private TransitionInfo AnalyzeTransition(MethodDef method, int exitValue, int stateParameterIndex)
        {
            var info = new TransitionInfo();
            var instrs = method.Body.Instructions;
            for (int i = 0; i < instrs.Count; i++)
            {
                if (instrs[i].OpCode == OpCodes.Stind_I4)
                {
                    if (i >= 2)
                    {
                        var addr = instrs[i - 2];
                        if (IsLoadArg(addr, 1))
                        {
                            int val = Fish.Shared.Utils.EmulateStackTop(method, instrs[i]);
                            if (val == exitValue)
                                info.IsExit = true;
                        }
                    }
                    int stackTop = Fish.Shared.Utils.EmulateStackTop(method, instrs[i]);
                    if (stackTop != -1 && i >= 1)
                    {
                        if (IsLoadArg(instrs[i - 1], stateParameterIndex) || (i >= 2 && IsLoadArg(instrs[i - 2], stateParameterIndex)))
                            if (info.NextState == -1)
                                info.NextState = stackTop;
                    }
                    if (stackTop == -1)
                    {
                        Instruction valInstr = i >= 1 ? instrs[i - 1] : null;
                        if (valInstr != null && valInstr.OpCode == OpCodes.Add)
                        {
                            int addIdx = instrs.IndexOf(valInstr);
                            if (addIdx >= 2 && instrs[addIdx - 1].IsLdcI4() && instrs[addIdx - 2].OpCode == OpCodes.Mul)
                            {
                                int A = instrs[addIdx - 1].GetLdcI4Value();
                                if (addIdx >= 3 && instrs[addIdx - 3].IsLdcI4())
                                {
                                    int M = instrs[addIdx - 3].GetLdcI4Value();
                                    if (i >= 2 && IsLoadArg(instrs[i - 2], 0))
                                    {
                                        info.IsConditional = true;
                                        info.TrueState = 1 * M + A;
                                        info.FalseState = 0 * M + A;
                                        info.SourceInstruction = valInstr;
                                        return info;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            return info;
        }

        #endregion

        #region Control Flow Reconstruction

        private bool ReconstructControlFlow(MethodDef method, LoopInfo info)
        {
            var visited = new HashSet<int>();
            var queue = new Queue<int>();
            var stateMap = new Dictionary<int, MethodDef>();
            var transitions = new Dictionary<int, TransitionInfo>();

            queue.Enqueue(info.InitialState);
            visited.Add(info.InitialState);

            while (queue.Count > 0)
            {
                int state = queue.Dequeue();
                if (state < 0 || state >= functionPointerArrays[info.ArrayField].Length)
                    continue;

                var targetMethod = functionPointerArrays[info.ArrayField][state] as MethodDef;
                if (targetMethod == null)
                    continue;

                stateMap[state] = targetMethod;
                var trans = AnalyzeTransition(targetMethod, info.ExitValue, info.StateParameterIndex);
                transitions[state] = trans;

                if (!trans.IsExit)
                {
                    if (trans.IsConditional)
                    {
                        if (trans.TrueState != -1 && !visited.Contains(trans.TrueState))
                        {
                            visited.Add(trans.TrueState);
                            queue.Enqueue(trans.TrueState);
                        }
                        if (trans.FalseState != -1 && !visited.Contains(trans.FalseState))
                        {
                            visited.Add(trans.FalseState);
                            queue.Enqueue(trans.FalseState);
                        }
                    }
                    else if (trans.NextState != -1 && !visited.Contains(trans.NextState))
                    {
                        visited.Add(trans.NextState);
                        queue.Enqueue(trans.NextState);
                    }
                }
            }

            var labels = new Dictionary<int, Instruction>();
            foreach (var state in visited)
                labels[state] = OpCodes.Nop.ToInstruction();
            var exitLabel = OpCodes.Nop.ToInstruction();
            var newBody = new List<Instruction>();

            foreach (var state in visited)
            {
                if (!stateMap.ContainsKey(state))
                    continue;
                newBody.Add(labels[state]);
                var targetMethod = stateMap[state];
                var trans = transitions[state];
                var inlined = InlineMethodBody(method, info, targetMethod);
                PatchStateUpdate(inlined, trans, labels, exitLabel, info);
                newBody.AddRange(inlined);
            }
            newBody.Add(exitLabel);

            CleanupInitialization(method, info);
            var body = method.Body;
            int removeCount = info.LoopEndIndex - info.LoopStartIndex;
            for (int i = 0; i < removeCount; i++)
                body.Instructions.RemoveAt(info.LoopStartIndex);
            int insertPos = info.LoopStartIndex;
            foreach (var instr in newBody)
                body.Instructions.Insert(insertPos++, instr);

            PostProcessMethod(method);
            return true;
        }

        #endregion

        #region Control Flow Method Inlining

        private List<Instruction> InlineMethodBody(MethodDef method, LoopInfo info, MethodDef targetMethod)
        {
            var newInstructions = new List<Instruction>();
            var workerMap = new Dictionary<Instruction, Instruction>();
            var paramMap = new Dictionary<int, Local>();
            var paramLocals = new List<Local>();

            foreach (var param in targetMethod.Parameters)
            {
                if (param.IsHiddenThisParameter && !targetMethod.HasThis)
                    continue;
                if (param.IsReturnTypeParameter)
                    continue;
                var loc = new Local(param.Type) { Name = $"inline_arg_{param.Index}" };
                method.Body.Variables.Add(loc);
                paramMap[param.Index] = loc;
                paramLocals.Add(loc);
            }

            if (info.Arguments != null && info.Arguments.Count == paramLocals.Count)
            {
                for (int i = 0; i < paramLocals.Count; i++)
                {
                    var argInstr = info.Arguments[i];
                    newInstructions.Add(new Instruction(argInstr.OpCode, argInstr.Operand));
                    newInstructions.Add(OpCodes.Stloc.ToInstruction(paramLocals[i]));
                }
            }

            foreach (var instr in targetMethod.Body.Instructions)
            {
                if (instr.OpCode == OpCodes.Ret)
                {
                    if (targetMethod.ReturnType.ElementType != ElementType.Void)
                        newInstructions.Add(OpCodes.Pop.ToInstruction());
                    continue;
                }
                var newInstr = new Instruction(instr.OpCode, instr.Operand);
                if (IsLoadArg(newInstr))
                {
                    int idx = GetArgIndex(newInstr);
                    if (paramMap.ContainsKey(idx))
                    {
                        newInstr.OpCode = OpCodes.Ldloc;
                        newInstr.Operand = paramMap[idx];
                    }
                }
                else if (newInstr.OpCode == OpCodes.Starg || newInstr.OpCode == OpCodes.Starg_S)
                {
                    int idx = GetArgIndex(newInstr);
                    if (paramMap.ContainsKey(idx))
                    {
                        newInstr.OpCode = OpCodes.Stloc;
                        newInstr.Operand = paramMap[idx];
                    }
                }
                workerMap[instr] = newInstr;
                newInstructions.Add(newInstr);
            }

            foreach (var instr in newInstructions)
            {
                if (instr.Operand is Instruction target && workerMap.ContainsKey(target))
                    instr.Operand = workerMap[target];
                else if (instr.Operand is Instruction[] targets)
                {
                    var newTargets = new Instruction[targets.Length];
                    for (int k = 0; k < targets.Length; k++)
                        if (workerMap.ContainsKey(targets[k]))
                            newTargets[k] = workerMap[targets[k]];
                    instr.Operand = newTargets;
                }
            }
            return newInstructions;
        }

        private void PatchStateUpdate(List<Instruction> instrs, TransitionInfo trans, Dictionary<int, Instruction> labels, Instruction exitLabel, LoopInfo info)
        {
            for (int i = instrs.Count - 1; i >= 0; i--)
            {
                if (instrs[i].OpCode == OpCodes.Stind_I4 && i >= 2 && instrs[i - 2].OpCode == OpCodes.Ldloca && GetLocal(instrs[i - 2], null) == info.ConditionVariable)
                {
                    instrs.RemoveRange(i - 2, 3);
                    i -= 2;
                }
            }
            if (trans.IsExit)
            {
                instrs.Add(OpCodes.Br.ToInstruction(exitLabel));
                return;
            }
            if (trans.IsConditional)
            {
                for (int i = instrs.Count - 1; i >= 0; i--)
                {
                    if (instrs[i].OpCode == OpCodes.Stind_I4)
                    {
                        int exprStart = GetExpressionStartIndex(instrs, i);
                        if (exprStart != -1 && exprStart < i)
                        {
                            var startInstr = instrs[exprStart];
                            if ((startInstr.OpCode == OpCodes.Ldloca || startInstr.OpCode == OpCodes.Ldloca_S) && GetLocal(startInstr, null) == info.StateVariable)
                            {
                                int count = i - exprStart + 1;
                                instrs.RemoveRange(exprStart, count);
                                instrs.Insert(exprStart, OpCodes.Br.ToInstruction(labels[trans.FalseState]));
                                instrs.Insert(exprStart, OpCodes.Brtrue.ToInstruction(labels[trans.TrueState]));
                                return;
                            }
                        }
                    }
                }
            }
            else if (trans.NextState != -1)
            {
                for (int i = instrs.Count - 1; i >= 0; i--)
                {
                    if (instrs[i].OpCode == OpCodes.Stind_I4)
                    {
                        int exprStart = GetExpressionStartIndex(instrs, i);
                        if (exprStart != -1 && exprStart < i)
                        {
                            var startInstr = instrs[exprStart];
                            if ((startInstr.OpCode == OpCodes.Ldloca || startInstr.OpCode == OpCodes.Ldloca_S) && GetLocal(startInstr, null) == info.StateVariable)
                            {
                                int count = i - exprStart + 1;
                                instrs.RemoveRange(exprStart, count);
                                instrs.Insert(exprStart, OpCodes.Br.ToInstruction(labels[trans.NextState]));
                                return;
                            }
                        }
                    }
                }
            }
        }

        private void CleanupInitialization(MethodDef method, LoopInfo info)
        {
            var instrs = method.Body.Instructions;
            for (int i = info.LoopStartIndex - 1; i >= 0; i--)
            {
                if (IsStoreLocal(instrs[i]))
                {
                    var local = GetLocal(instrs[i], method.Body.Variables);
                    if (local == info.StateVariable || local == info.ConditionVariable)
                    {
                        if (i > 0 && Fish.Shared.Utils.IsIntegerConstant(instrs[i - 1]))
                        {
                            instrs.RemoveAt(i);
                            instrs.RemoveAt(i - 1);
                            i--;
                            info.LoopStartIndex -= 2;
                            info.LoopEndIndex -= 2;
                        }
                    }
                }
            }
        }

        #endregion

        #region Control Flow Post-Processing

        private void PostProcessMethod(MethodDef method)
        {
            bool modified = true;
            while (modified)
            {
                modified = false;
                modified |= RemoveNops(method);
                modified |= RemoveRedundantJumps(method);
                modified |= RemoveUnusedAssignments(method);
                modified |= RemovePopSequences(method);
            }
        }

        private bool RemoveNops(MethodDef method)
        {
            var instrs = method.Body.Instructions;
            bool modified = false;
            var nopRedirects = new Dictionary<Instruction, Instruction>();

            for (int i = 0; i < instrs.Count; i++)
            {
                if (instrs[i].OpCode == OpCodes.Nop)
                {
                    Instruction nextNonNop = null;
                    for (int j = i + 1; j < instrs.Count; j++)
                        if (instrs[j].OpCode != OpCodes.Nop)
                        {
                            nextNonNop = instrs[j];
                            break;
                        }
                    if (nextNonNop != null)
                        nopRedirects[instrs[i]] = nextNonNop;
                }
            }

            foreach (var instr in instrs)
            {
                if (instr.Operand is Instruction target && nopRedirects.TryGetValue(target, out var newTarget))
                {
                    instr.Operand = newTarget;
                    modified = true;
                }
                else if (instr.Operand is Instruction[] targets)
                    for (int i = 0; i < targets.Length; i++)
                        if (nopRedirects.TryGetValue(targets[i], out var newT))
                        {
                            targets[i] = newT;
                            modified = true;
                        }
            }

            foreach (var handler in method.Body.ExceptionHandlers)
            {
                if (handler.TryStart != null && nopRedirects.TryGetValue(handler.TryStart, out var ts))
                {
                    handler.TryStart = ts;
                    modified = true;
                }
                if (handler.TryEnd != null && nopRedirects.TryGetValue(handler.TryEnd, out var te))
                {
                    handler.TryEnd = te;
                    modified = true;
                }
                if (handler.HandlerStart != null && nopRedirects.TryGetValue(handler.HandlerStart, out var hs))
                {
                    handler.HandlerStart = hs;
                    modified = true;
                }
                if (handler.HandlerEnd != null && nopRedirects.TryGetValue(handler.HandlerEnd, out var he))
                {
                    handler.HandlerEnd = he;
                    modified = true;
                }
                if (handler.FilterStart != null && nopRedirects.TryGetValue(handler.FilterStart, out var fs))
                {
                    handler.FilterStart = fs;
                    modified = true;
                }
            }

            for (int i = instrs.Count - 1; i >= 0; i--)
                if (instrs[i].OpCode == OpCodes.Nop && !IsTarget(method, instrs[i]))
                {
                    instrs.RemoveAt(i);
                    modified = true;
                }
            return modified;
        }

        private bool RemoveRedundantJumps(MethodDef method)
        {
            var instrs = method.Body.Instructions;
            bool modified = false;
            for (int i = 0; i < instrs.Count - 1; i++)
            {
                if ((instrs[i].OpCode == OpCodes.Br || instrs[i].OpCode == OpCodes.Br_S) && instrs[i].Operand is Instruction target && target == instrs[i + 1])
                {
                    instrs.RemoveAt(i);
                    modified = true;
                    i--;
                }
            }
            return modified;
        }

        private bool RemoveUnusedAssignments(MethodDef method)
        {
            var instrs = method.Body.Instructions;
            bool modified = false;
            for (int i = instrs.Count - 1; i >= 0; i--)
            {
                if (IsStoreLocal(instrs[i]))
                {
                    var local = GetLocal(instrs[i], method.Body.Variables);
                    if (local != null && !IsLocalReadAfter(instrs, i + 1, local, method.Body.Variables))
                    {
                        if (i > 0)
                        {
                            var prev = instrs[i - 1];
                            if (Fish.Shared.Utils.IsIntegerConstant(prev) || IsLoadLocal(prev) || IsLoadArg(prev) || prev.OpCode == OpCodes.Ldstr || prev.OpCode == OpCodes.Ldnull)
                            {
                                instrs.RemoveAt(i);
                                instrs.RemoveAt(i - 1);
                                modified = true;
                                i--;
                            }
                            else if (prev.OpCode == OpCodes.Pop)
                            {
                                instrs.RemoveAt(i);
                                instrs.Insert(i, OpCodes.Pop.ToInstruction());
                                modified = true;
                            }
                            else
                            {
                                instrs[i].OpCode = OpCodes.Pop;
                                instrs[i].Operand = null;
                                modified = true;
                            }
                        }
                    }
                }
            }
            return modified;
        }

        private bool IsLocalReadAfter(IList<Instruction> instrs, int startIndex, Local local, IList<Local> locals)
        {
            for (int i = startIndex; i < instrs.Count; i++)
            {
                if (IsLoadLocal(instrs[i]) && GetLocal(instrs[i], locals) == local)
                    return true;
                if ((instrs[i].OpCode == OpCodes.Ldloca || instrs[i].OpCode == OpCodes.Ldloca_S) && GetLocal(instrs[i], locals) == local)
                    return true;
            }
            return false;
        }

        private bool RemovePopSequences(MethodDef method)
        {
            var instrs = method.Body.Instructions;
            bool modified = false;
            for (int i = instrs.Count - 1; i >= 1; i--)
            {
                if (instrs[i].OpCode == OpCodes.Pop)
                {
                    var prev = instrs[i - 1];
                    if (Fish.Shared.Utils.IsIntegerConstant(prev) || IsLoadLocal(prev) || IsLoadArg(prev) || prev.OpCode == OpCodes.Ldstr || prev.OpCode == OpCodes.Ldnull || prev.OpCode == OpCodes.Ldloca || prev.OpCode == OpCodes.Ldloca_S)
                    {
                        instrs.RemoveAt(i);
                        instrs.RemoveAt(i - 1);
                        modified = true;
                        i--;
                    }
                }
            }
            return modified;
        }

        private bool IsTarget(MethodDef method, Instruction target)
        {
            foreach (var instr in method.Body.Instructions)
            {
                if (instr.Operand is Instruction t && t == target)
                    return true;
                if (instr.Operand is Instruction[] ts && ts.Contains(target))
                    return true;
            }
            return false;
        }

        #endregion

        #region Control Flow Stack Analysis

        private int GetExpressionStartIndex(List<Instruction> instrs, int index)
        {
            int requiredPushes = 0;
            int scan = index;
            var root = instrs[scan];
            requiredPushes += GetPopCount(root);
            requiredPushes -= GetPushCount(root);

            while (requiredPushes > 0 && scan > 0)
            {
                scan--;
                var instr = instrs[scan];
                if (instr.OpCode == OpCodes.Ret || instr.OpCode == OpCodes.Throw || instr.OpCode == OpCodes.Br || instr.OpCode == OpCodes.Br_S || instr.OpCode == OpCodes.Leave || instr.OpCode == OpCodes.Leave_S)
                    break;
                int pushes = GetPushCount(instr);
                int pops = GetPopCount(instr);
                requiredPushes -= pushes;
                requiredPushes += pops;
            }
            return requiredPushes <= 0 ? scan : -1;
        }

        private int GetPopCount(Instruction instr)
        {
            if (instr.OpCode.StackBehaviourPop == StackBehaviour.Pop0)
                return 0;
            if (instr.OpCode.StackBehaviourPop == StackBehaviour.Pop1)
                return 1;
            if (instr.OpCode.StackBehaviourPop == StackBehaviour.Pop1_pop1)
                return 2;
            if (instr.OpCode.StackBehaviourPop == StackBehaviour.Popi)
                return 1;
            if (instr.OpCode.StackBehaviourPop == StackBehaviour.Popi_pop1)
                return 2;
            if (instr.OpCode.StackBehaviourPop == StackBehaviour.Popi_popi)
                return 2;
            if (instr.OpCode.StackBehaviourPop == StackBehaviour.Popi_popi8)
                return 2;
            if (instr.OpCode.StackBehaviourPop == StackBehaviour.Popi_popr4)
                return 2;
            if (instr.OpCode.StackBehaviourPop == StackBehaviour.Popi_popr8)
                return 2;
            if (instr.OpCode.StackBehaviourPop == StackBehaviour.Popref)
                return 1;
            if (instr.OpCode.StackBehaviourPop == StackBehaviour.Popref_pop1)
                return 2;
            if (instr.OpCode.StackBehaviourPop == StackBehaviour.Popref_popi)
                return 2;
            if (instr.OpCode.StackBehaviourPop == StackBehaviour.Varpop)
            {
                if (instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt || instr.OpCode == OpCodes.Newobj)
                {
                    if (instr.Operand is IMethod m)
                    {
                        int count = m.MethodSig.Params.Count;
                        if (instr.OpCode != OpCodes.Newobj && m.MethodSig.HasThis)
                            count++;
                        return count;
                    }
                    else if (instr.Operand is MethodSig sig)
                    {
                        int count = sig.Params.Count;
                        if (sig.HasThis)
                            count++;
                        return count;
                    }
                }
                if (instr.OpCode == OpCodes.Ret)
                    return 1;
            }
            return 0;
        }

        private int GetPushCount(Instruction instr)
        {
            if (instr.OpCode.StackBehaviourPush == StackBehaviour.Push0)
                return 0;
            if (instr.OpCode.StackBehaviourPush == StackBehaviour.Push1)
                return 1;
            if (instr.OpCode.StackBehaviourPush == StackBehaviour.Push1_push1)
                return 2;
            if (instr.OpCode.StackBehaviourPush == StackBehaviour.Pushi)
                return 1;
            if (instr.OpCode.StackBehaviourPush == StackBehaviour.Pushi8)
                return 1;
            if (instr.OpCode.StackBehaviourPush == StackBehaviour.Pushr4)
                return 1;
            if (instr.OpCode.StackBehaviourPush == StackBehaviour.Pushr8)
                return 1;
            if (instr.OpCode.StackBehaviourPush == StackBehaviour.Pushref)
                return 1;
            if (instr.OpCode.StackBehaviourPush == StackBehaviour.Varpush)
            {
                if (instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt)
                    if (instr.Operand is IMethod m)
                        return m.MethodSig.RetType.ElementType == ElementType.Void ? 0 : 1;
                if (instr.OpCode == OpCodes.Calli)
                    if (instr.Operand is MethodSig sig)
                        return sig.RetType.ElementType == ElementType.Void ? 0 : 1;
            }
            return 0;
        }

        #endregion

        #region Control Flow Helper Methods

        private bool IsLoadLocal(Instruction instr) => Fish.Shared.Utils.IsLoadLocal(instr);

        private bool IsStoreLocal(Instruction instr) => Fish.Shared.Utils.IsStoreLocal(instr);

        private Local GetLocal(Instruction instr, IList<Local> locals) => Fish.Shared.Utils.GetLocal(instr, locals);

        private bool IsLoadArg(Instruction instr, int index = -1)
        {
            int idx = GetArgIndex(instr);
            return idx != -1 && (index == -1 || idx == index) && instr.OpCode.Code.ToString().StartsWith("Ldarg");
        }

        private int GetArgIndex(Instruction instr)
        {
            if (instr.Operand is Parameter p)
                return p.Index;
            if (instr.Operand is int i)
                return i;
            if (instr.OpCode == OpCodes.Ldarg_0)
                return 0;
            if (instr.OpCode == OpCodes.Ldarg_1)
                return 1;
            if (instr.OpCode == OpCodes.Ldarg_2)
                return 2;
            if (instr.OpCode == OpCodes.Ldarg_3)
                return 3;
            if (instr.OpCode == OpCodes.Ldarg_S || instr.OpCode == OpCodes.Ldarg)
                return (instr.Operand is Parameter param) ? param.Index : (int)instr.Operand;
            return -1;
        }

        #endregion
    }
}
