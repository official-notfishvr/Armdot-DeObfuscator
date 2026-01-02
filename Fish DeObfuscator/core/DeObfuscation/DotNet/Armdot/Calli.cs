using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Fish.Shared;

namespace Fish_DeObfuscator.core.DeObfuscation.DotNet.Armdot
{
    public class Calli : IStage
    {
        #region Fields

        private Dictionary<IField, IMethod[]> functionPointerArrays = new Dictionary<IField, IMethod[]>();
        private Dictionary<IMethod, int> methodToIndexMap = new Dictionary<IMethod, int>();
        private int deobfuscatedCalls = 0;

        #endregion

        #region IStage

        public void Execute(IContext context)
        {
            var module = context.ModuleDefinition;
            if (module == null)
                return;

            functionPointerArrays = AnalyzeFunctionPointerArrays(module);
            BuildMethodIndexMapping(module);
            DeobfuscateCalliInstructions(module);
            CleanupObfuscationArtifacts(module);

            if (deobfuscatedCalls > 0)
                Logger.Detail($"Resolved {deobfuscatedCalls} calli instructions");
        }

        #endregion

        #region Calli Function Pointer Array Analysis

        public static Dictionary<IField, IMethod[]> AnalyzeFunctionPointerArrays(ModuleDefMD module)
        {
            var functionPointerArrays = new Dictionary<IField, IMethod[]>();
            try
            {
                foreach (var type in module.GetTypes())
                {
                    foreach (var field in type.Fields)
                    {
                        if (field.IsStatic && field.FieldType.IsSZArray)
                        {
                            var arrayType = field.FieldType as SZArraySig;
                            if (arrayType != null)
                            {
                                var elementType = arrayType.Next;
                                if (elementType != null && elementType.FullName == "System.IntPtr")
                                {
                                    var initializationMethod = FindArrayInitializationMethod(field, type);
                                    if (initializationMethod != null)
                                    {
                                        var functionPointers = ExtractFunctionPointers(initializationMethod);
                                        if (functionPointers.Any())
                                        {
                                            int maxIndex = functionPointers.Keys.Max();
                                            var methodArray = new IMethod[maxIndex + 1];
                                            foreach (var kvp in functionPointers)
                                                methodArray[kvp.Key] = kvp.Value;
                                            functionPointerArrays[field] = methodArray;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return functionPointerArrays;
        }

        public static MethodDef FindArrayInitializationMethod(IField field, TypeDef type)
        {
            var candidates = new List<MethodDef>();
            var staticCtor = type.FindStaticConstructor();
            if (staticCtor != null && ReferencesField(staticCtor, field))
                candidates.Add(staticCtor);

            foreach (var method in type.Methods.Where(m => m.IsStatic && m.HasBody))
                if (ReferencesField(method, field) && !candidates.Contains(method))
                    candidates.Add(method);

            return candidates.FirstOrDefault(m => InitializesArray(m, field));
        }

        private static bool ReferencesField(MethodDef method, IField field)
        {
            if (!method.HasBody)
                return false;
            return method.Body.Instructions.Any(instr => instr.Operand is IField f && f.FullName == field.FullName);
        }

        private static bool InitializesArray(MethodDef method, IField field)
        {
            if (!method.HasBody)
                return false;
            bool createsArray = false,
                assignsField = false;
            foreach (var instr in method.Body.Instructions)
            {
                if (instr.OpCode == OpCodes.Newarr)
                    createsArray = true;
                else if (instr.OpCode == OpCodes.Stsfld && instr.Operand is IField f && f.FullName == field.FullName)
                    assignsField = true;
            }
            return createsArray && assignsField;
        }

        public static Dictionary<int, IMethod> ExtractFunctionPointers(MethodDef method)
        {
            var functionPointers = new Dictionary<int, IMethod>();
            var instructions = method.Body.Instructions;
            for (int i = 0; i < instructions.Count; i++)
            {
                if (instructions[i].OpCode == OpCodes.Stelem_I && i >= 3)
                {
                    var ldftn = instructions[i - 1];
                    var ldc = instructions[i - 2];
                    var ldsfld = instructions[i - 3];
                    if (ldftn.OpCode == OpCodes.Ldftn && Fish.Shared.Utils.IsIntegerConstant(ldc) && ldsfld.OpCode == OpCodes.Ldsfld)
                    {
                        int index = Fish.Shared.Utils.GetConstantValue(ldc);
                        var targetMethod = ldftn.Operand as IMethod;
                        if (targetMethod != null)
                            functionPointers[index] = targetMethod;
                    }
                }
            }
            return functionPointers;
        }

        private void BuildMethodIndexMapping(ModuleDefMD module)
        {
            foreach (var kvp in functionPointerArrays)
            {
                var methodArray = kvp.Value;
                for (int i = 0; i < methodArray.Length; i++)
                    if (methodArray[i] != null && !methodToIndexMap.ContainsKey(methodArray[i]))
                        methodToIndexMap[methodArray[i]] = i;
            }
        }

        #endregion

        #region Calli Pattern Detection

        private class CalliPattern
        {
            public int CalliIndex { get; set; }
            public int LdelemIndex { get; set; }
            public int IndexLoadIndex { get; set; }
            public int ArrayLoadIndex { get; set; }
            public IField ArrayField { get; set; }
            public int ArrayIndex { get; set; }
        }

        private CalliPattern AnalyzeCalliPattern(IList<Instruction> instructions, int calliIndex)
        {
            int ldelemIndex = GetPreviousInstructionIndex(instructions, calliIndex - 1);
            if (ldelemIndex == -1 || instructions[ldelemIndex].OpCode != OpCodes.Ldelem_I)
                return null;

            int indexLoadIndex = GetPreviousInstructionIndex(instructions, ldelemIndex - 1);
            if (indexLoadIndex == -1)
                return null;

            int indexValue = -1;
            if (Fish.Shared.Utils.IsIntegerConstant(instructions[indexLoadIndex]))
                indexValue = Fish.Shared.Utils.GetConstantValue(instructions[indexLoadIndex]);
            else if (IsVariableLoad(instructions[indexLoadIndex]))
                indexValue = TraceVariableValue(instructions, indexLoadIndex);

            if (indexValue == -1)
                return null;

            int arrayLoadIndex = GetPreviousInstructionIndex(instructions, indexLoadIndex - 1);
            if (arrayLoadIndex == -1 || instructions[arrayLoadIndex].OpCode != OpCodes.Ldsfld)
                return null;

            var arrayField = instructions[arrayLoadIndex].Operand as IField;
            if (arrayField == null || !functionPointerArrays.ContainsKey(arrayField))
                return null;

            return new CalliPattern
            {
                CalliIndex = calliIndex,
                LdelemIndex = ldelemIndex,
                IndexLoadIndex = indexLoadIndex,
                ArrayLoadIndex = arrayLoadIndex,
                ArrayField = arrayField,
                ArrayIndex = indexValue,
            };
        }

        private int GetPreviousInstructionIndex(IList<Instruction> instructions, int startIndex)
        {
            for (int i = startIndex; i >= 0; i--)
                if (instructions[i].OpCode != OpCodes.Nop)
                    return i;
            return -1;
        }

        #endregion

        #region Calli Variable Tracing

        private bool IsVariableLoad(Instruction instruction)
        {
            return instruction.OpCode == OpCodes.Ldloc
                || instruction.OpCode == OpCodes.Ldloc_S
                || instruction.OpCode == OpCodes.Ldloc_0
                || instruction.OpCode == OpCodes.Ldloc_1
                || instruction.OpCode == OpCodes.Ldloc_2
                || instruction.OpCode == OpCodes.Ldloc_3
                || instruction.OpCode == OpCodes.Ldarg
                || instruction.OpCode == OpCodes.Ldarg_S
                || instruction.OpCode == OpCodes.Ldarg_0
                || instruction.OpCode == OpCodes.Ldarg_1
                || instruction.OpCode == OpCodes.Ldarg_2
                || instruction.OpCode == OpCodes.Ldarg_3;
        }

        private int TraceVariableValue(IList<Instruction> instructions, int loadIndex)
        {
            var loadInstr = instructions[loadIndex];
            int variableIndex = GetVariableIndex(loadInstr);
            for (int i = loadIndex - 1; i >= 0; i--)
            {
                var instr = instructions[i];
                if (IsVariableStore(instr) && GetVariableIndex(instr) == variableIndex)
                {
                    int prevIdx = GetPreviousInstructionIndex(instructions, i - 1);
                    if (prevIdx != -1 && Fish.Shared.Utils.IsIntegerConstant(instructions[prevIdx]))
                        return Fish.Shared.Utils.GetConstantValue(instructions[prevIdx]);
                    return -1;
                }
            }
            return -1;
        }

        private bool IsVariableStore(Instruction instruction)
        {
            return instruction.OpCode == OpCodes.Stloc || instruction.OpCode == OpCodes.Stloc_S || instruction.OpCode == OpCodes.Stloc_0 || instruction.OpCode == OpCodes.Stloc_1 || instruction.OpCode == OpCodes.Stloc_2 || instruction.OpCode == OpCodes.Stloc_3;
        }

        private int GetVariableIndex(Instruction instruction)
        {
            if (instruction.Operand is Local local)
                return local.Index;
            switch (instruction.OpCode.Code)
            {
                case Code.Ldloc_0:
                case Code.Stloc_0:
                    return 0;
                case Code.Ldloc_1:
                case Code.Stloc_1:
                    return 1;
                case Code.Ldloc_2:
                case Code.Stloc_2:
                    return 2;
                case Code.Ldloc_3:
                case Code.Stloc_3:
                    return 3;
            }
            return -1;
        }

        #endregion

        #region Calli Target Resolution

        private IMethod ResolveCalliTarget(CalliPattern pattern)
        {
            if (!functionPointerArrays.ContainsKey(pattern.ArrayField))
                return null;
            var methodArray = functionPointerArrays[pattern.ArrayField];
            if (pattern.ArrayIndex < 0 || pattern.ArrayIndex >= methodArray.Length)
                return null;
            return methodArray[pattern.ArrayIndex];
        }

        #endregion

        #region Calli Instruction Patching

        private void DeobfuscateCalliInstructions(ModuleDefMD module)
        {
            foreach (var type in module.GetTypes())
            foreach (var method in type.Methods)
                if (method.HasBody)
                    DeobfuscateMethodCalli(method);
        }

        private void DeobfuscateMethodCalli(MethodDef method)
        {
            try
            {
                bool modified = true;
                while (modified)
                {
                    modified = false;
                    var instructions = method.Body.Instructions;
                    for (int i = 0; i < instructions.Count; i++)
                    {
                        if (instructions[i].OpCode == OpCodes.Calli)
                        {
                            var pattern = AnalyzeCalliPattern(instructions, i);
                            if (pattern != null)
                            {
                                var targetMethod = ResolveCalliTarget(pattern);
                                if (targetMethod != null)
                                {
                                    ReplaceCalliWithCall(method.Body, instructions, pattern, targetMethod);
                                    deobfuscatedCalls++;
                                    modified = true;
                                    break;
                                }
                            }
                        }
                    }
                    if (DeobfuscateCalliVirtualization(method))
                        modified = true;
                }
            }
            catch { }
        }

        private void ReplaceCalliWithCall(CilBody body, IList<Instruction> instructions, CalliPattern pattern, IMethod targetMethod)
        {
            int calliIdx = instructions.IndexOf(instructions[pattern.CalliIndex]);
            if (calliIdx == -1)
                return;
            instructions[calliIdx].OpCode = OpCodes.Call;
            instructions[calliIdx].Operand = targetMethod;
            body.Instructions.Remove(instructions[pattern.LdelemIndex]);
            body.Instructions.Remove(instructions[pattern.IndexLoadIndex]);
            body.Instructions.Remove(instructions[pattern.ArrayLoadIndex]);
        }

        #endregion

        #region Calli Virtualization Deobfuscation

        private bool DeobfuscateCalliVirtualization(MethodDef method)
        {
            if (!method.HasBody)
                return false;
            var instrs = method.Body.Instructions;
            IField dataField = null;
            Local dataPtrLocal = null;

            for (int i = 0; i < Math.Min(instrs.Count, 50); i++)
            {
                if (instrs[i].OpCode == OpCodes.Ldsflda && instrs[i].Operand is IField f)
                {
                    if (i + 1 < instrs.Count && IsStoreLocal(instrs[i + 1]))
                    {
                        dataField = f;
                        dataPtrLocal = GetLocal(instrs[i + 1], method.Body.Variables);
                        break;
                    }
                }
            }
            if (dataField == null || dataPtrLocal == null)
                return false;

            byte[] blobData = null;
            if (dataField is FieldDef fd && fd.HasFieldRVA)
                blobData = fd.InitialValue;
            if (blobData == null)
                return false;

            bool modified = false;
            for (int i = 0; i < instrs.Count; i++)
            {
                if (instrs[i].OpCode == OpCodes.Calli)
                {
                    int arrayIdx = -1,
                        indexIdx = -1;
                    for (int k = i - 1; k >= Math.Max(0, i - 20); k--)
                    {
                        if (instrs[k].OpCode == OpCodes.Ldelem_I || instrs[k].OpCode == OpCodes.Ldelem_Ref)
                        {
                            if (k > 0 && IsLoadLocal(instrs[k - 1]))
                            {
                                indexIdx = k - 1;
                                if (k > 1 && instrs[k - 2].OpCode == OpCodes.Ldsfld)
                                {
                                    arrayIdx = k - 2;
                                    break;
                                }
                            }
                        }
                    }
                    if (arrayIdx != -1 && indexIdx != -1)
                    {
                        var arrayField = instrs[arrayIdx].Operand as IField;
                        var indexLocal = GetLocal(instrs[indexIdx], method.Body.Variables);
                        IField knownField = null;
                        foreach (var key in functionPointerArrays.Keys)
                            if (key.FullName == arrayField.FullName)
                            {
                                knownField = key;
                                break;
                            }

                        if (knownField != null)
                        {
                            int resolvedIndex = Fish.Shared.Utils.EmulateAndResolveLocal(method, blobData, dataPtrLocal, indexLocal, instrs[i]);
                            if (resolvedIndex != -1)
                            {
                                var targets = functionPointerArrays[knownField];
                                if (resolvedIndex >= 0 && resolvedIndex < targets.Length && targets[resolvedIndex] != null)
                                {
                                    instrs[i].OpCode = OpCodes.Call;
                                    instrs[i].Operand = targets[resolvedIndex];
                                    instrs[arrayIdx].OpCode = OpCodes.Nop;
                                    instrs[arrayIdx].Operand = null;
                                    instrs[indexIdx].OpCode = OpCodes.Nop;
                                    instrs[arrayIdx + 2].OpCode = OpCodes.Nop;
                                    modified = true;
                                }
                            }
                        }
                    }
                }
            }
            if (modified)
                RemoveNops(method);
            return modified;
        }

        #endregion

        #region Calli Cleanup

        private void CleanupObfuscationArtifacts(ModuleDefMD module)
        {
            try
            {
                foreach (var kvp in functionPointerArrays)
                {
                    var field = kvp.Key;
                    if (!IsFieldStillReferenced(field, module))
                    {
                        var type = field.DeclaringType as TypeDef;
                        if (type != null)
                        {
                            var fieldDef = type.Fields.FirstOrDefault(f => f.FullName == field.FullName);
                        }
                    }
                }
            }
            catch { }
        }

        private bool IsFieldStillReferenced(IField field, ModuleDefMD module)
        {
            foreach (var type in module.GetTypes())
            foreach (var method in type.Methods.Where(m => m.HasBody))
                if (method.Body.Instructions.Any(instr => instr.Operand is IField fr && fr.FullName == field.FullName))
                    return true;
            return false;
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

        #region Calli Helper Methods

        private bool IsLoadLocal(Instruction instr) => Fish.Shared.Utils.IsLoadLocal(instr);

        private bool IsStoreLocal(Instruction instr) => Fish.Shared.Utils.IsStoreLocal(instr);

        private Local GetLocal(Instruction instr, IList<Local> locals) => Fish.Shared.Utils.GetLocal(instr, locals);

        #endregion
    }
}
