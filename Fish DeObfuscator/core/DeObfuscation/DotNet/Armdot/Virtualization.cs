using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Fish.Shared;

namespace Fish_DeObfuscator.core.DeObfuscation.DotNet.Armdot
{
    public class Virtualization : IStage
    {
        #region Fields

        private Dictionary<IField, IMethod[]> calliArrays;
        private ModuleDefMD module;
        private int patchedCount = 0;
        private int processedMethods = 0;

        #endregion

        #region IStage

        public void Execute(IContext context)
        {
            module = context.ModuleDefinition;
            calliArrays = Calli.AnalyzeFunctionPointerArrays(module);

            foreach (var type in module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody)
                        continue;

                    try
                    {
                        int patched = DeobfuscateMethod(method);
                        if (patched > 0)
                        {
                            patchedCount += patched;
                            processedMethods++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.StageError($"Error in {method.Name}: {ex.Message}");
                    }
                }
            }

            if (patchedCount > 0)
                Logger.Detail($"Patched {patchedCount} calli in {processedMethods} methods");
        }

        #endregion

        #region VM Calli Deobfuscation

        private int DeobfuscateMethod(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            int totalPatched = 0;
            bool modified = true;

            byte[] bytecode = ExtractBytecode(method);
            IMethod[] functionPointers = null;

            for (int i = 0; i < instructions.Count; i++)
            {
                if (instructions[i].OpCode == OpCodes.Ldsfld && instructions[i].Operand is IField arrField)
                {
                    functionPointers = GetFunctionPointers(arrField);
                    if (functionPointers != null)
                        break;
                }
            }

            Dictionary<byte, HashSet<int>> opcodeToFuncIndices = null;
            if (bytecode != null && bytecode.Length >= 8)
            {
                opcodeToFuncIndices = new Dictionary<byte, HashSet<int>>();
                for (int pc = 0; pc + 8 <= bytecode.Length; pc += 8)
                {
                    byte opcode = bytecode[pc];
                    int funcIdx = BitConverter.ToInt32(bytecode, pc + 4);

                    if (!opcodeToFuncIndices.ContainsKey(opcode))
                        opcodeToFuncIndices[opcode] = new HashSet<int>();

                    if (funcIdx >= 0 && functionPointers != null && funcIdx < functionPointers.Length)
                        opcodeToFuncIndices[opcode].Add(funcIdx);
                }
            }

            while (modified)
            {
                modified = false;

                for (int i = 0; i < instructions.Count; i++)
                {
                    if (instructions[i].OpCode != OpCodes.Calli)
                        continue;

                    var calliSig = instructions[i].Operand as CallingConventionSig;
                    if (calliSig == null)
                        continue;

                    int ldelemIdx = FindPrevOpcode(instructions, i - 1, OpCodes.Ldelem_I, 5);
                    if (ldelemIdx == -1)
                        continue;

                    int ldindIdx = FindPrevOpcode(instructions, ldelemIdx - 1, OpCodes.Ldind_I4, 3);
                    if (ldindIdx == -1)
                        continue;

                    int ldsfldIdx = FindPrevOpcode(instructions, ldindIdx - 1, OpCodes.Ldsfld, 5);
                    if (ldsfldIdx == -1)
                        continue;

                    IField arrayField = instructions[ldsfldIdx].Operand as IField;
                    if (arrayField == null)
                        continue;

                    IMethod[] funcPtrs = GetFunctionPointers(arrayField);
                    if (funcPtrs == null)
                        continue;

                    IMethod targetMethod = null;

                    // Try VM opcode-based resolution first
                    if (opcodeToFuncIndices != null)
                    {
                        byte? vmOpcode = FindVmOpcode(method, i);
                        if (vmOpcode != null && opcodeToFuncIndices.TryGetValue(vmOpcode.Value, out var indices))
                        {
                            if (indices.Count == 1)
                            {
                                int idx = indices.First();
                                if (idx >= 0 && idx < funcPtrs.Length)
                                    targetMethod = funcPtrs[idx];
                            }
                        }
                    }

                    // Fallback: signature matching against all function pointers
                    if (targetMethod == null)
                    {
                        MethodSig calliMethodSig = calliSig as MethodSig;
                        if (calliMethodSig != null)
                        {
                            var matchingMethods = new Dictionary<string, IMethod>();
                            foreach (var fp in funcPtrs)
                            {
                                if (fp != null && fp.MethodSig != null && SignaturesMatch(calliMethodSig, fp.MethodSig))
                                {
                                    string key = fp.FullName;
                                    if (!matchingMethods.ContainsKey(key))
                                        matchingMethods[key] = fp;
                                }
                            }

                            if (matchingMethods.Count == 1)
                                targetMethod = matchingMethods.Values.First();
                        }
                    }

                    // Fallback: bytecode-based index resolution
                    if (targetMethod == null && bytecode != null)
                    {
                        MethodSig calliMethodSig = calliSig as MethodSig;
                        if (calliMethodSig != null)
                        {
                            var usedIndices = new HashSet<int>();
                            for (int pc = 0; pc + 8 <= bytecode.Length; pc += 8)
                            {
                                int idx = BitConverter.ToInt32(bytecode, pc + 4);
                                if (idx >= 0 && idx < funcPtrs.Length)
                                    usedIndices.Add(idx);
                            }

                            var matchingMethods = new Dictionary<string, IMethod>();
                            foreach (int idx in usedIndices)
                            {
                                var fp = funcPtrs[idx];
                                if (fp != null && fp.MethodSig != null && SignaturesMatch(calliMethodSig, fp.MethodSig))
                                {
                                    string key = fp.FullName;
                                    if (!matchingMethods.ContainsKey(key))
                                        matchingMethods[key] = fp;
                                }
                            }

                            if (matchingMethods.Count == 1)
                                targetMethod = matchingMethods.Values.First();
                        }
                    }

                    // Fallback: VM opcode with signature matching
                    if (targetMethod == null && opcodeToFuncIndices != null)
                    {
                        MethodSig calliMethodSig = calliSig as MethodSig;
                        if (calliMethodSig != null)
                        {
                            byte? vmOpcode = FindVmOpcode(method, i);
                            if (vmOpcode != null && opcodeToFuncIndices.TryGetValue(vmOpcode.Value, out var indices))
                            {
                                var matchingMethods = new Dictionary<string, IMethod>();
                                foreach (int idx in indices)
                                {
                                    if (idx >= 0 && idx < funcPtrs.Length)
                                    {
                                        var fp = funcPtrs[idx];
                                        if (fp != null && fp.MethodSig != null && SignaturesMatch(calliMethodSig, fp.MethodSig))
                                        {
                                            string key = fp.FullName;
                                            if (!matchingMethods.ContainsKey(key))
                                                matchingMethods[key] = fp;
                                        }
                                    }
                                }

                                if (matchingMethods.Count == 1)
                                    targetMethod = matchingMethods.Values.First();
                            }
                        }
                    }

                    if (targetMethod == null)
                        continue;

                    MethodSig targetSig = targetMethod.MethodSig;
                    if (targetSig == null)
                        continue;

                    MethodSig finalCalliMethodSig = calliSig as MethodSig;
                    if (finalCalliMethodSig == null)
                        continue;

                    if (!SignaturesMatch(finalCalliMethodSig, targetSig))
                        continue;

                    // Patch the calli instruction
                    instructions[ldsfldIdx].OpCode = OpCodes.Nop;
                    instructions[ldsfldIdx].Operand = null;

                    for (int j = ldsfldIdx + 1; j <= ldindIdx; j++)
                    {
                        if (instructions[j].OpCode == OpCodes.Ldloc || instructions[j].OpCode == OpCodes.Ldloc_S || instructions[j].OpCode.Code >= Code.Ldloc_0 && instructions[j].OpCode.Code <= Code.Ldloc_3 || instructions[j].OpCode == OpCodes.Add)
                        {
                            instructions[j].OpCode = OpCodes.Nop;
                            instructions[j].Operand = null;
                        }
                    }

                    instructions[ldindIdx].OpCode = OpCodes.Nop;
                    instructions[ldindIdx].Operand = null;
                    instructions[ldelemIdx].OpCode = OpCodes.Nop;
                    instructions[ldelemIdx].Operand = null;

                    instructions[i].OpCode = OpCodes.Call;
                    instructions[i].Operand = targetMethod;

                    totalPatched++;
                    modified = true;
                    break;
                }
            }

            return totalPatched;
        }

        #endregion

        #region VM Signature Matching

        private bool SignaturesMatch(MethodSig calliSig, MethodSig targetSig)
        {
            if (calliSig.RetType.FullName != targetSig.RetType.FullName)
                return false;

            if (calliSig.Params.Count != targetSig.Params.Count)
                return false;

            for (int i = 0; i < calliSig.Params.Count; i++)
            {
                if (calliSig.Params[i].FullName != targetSig.Params[i].FullName)
                    return false;
            }

            return true;
        }

        #endregion

        #region VM Function Pointer Array Analysis

        private IMethod[] GetFunctionPointers(IField field)
        {
            foreach (var kvp in calliArrays)
            {
                if (kvp.Key.FullName == field.FullName)
                    return kvp.Value;
            }
            return null;
        }

        #endregion

        #region VM Opcode Detection

        private byte? FindVmOpcode(MethodDef method, int calliIdx)
        {
            var instructions = method.Body.Instructions;

            for (int i = calliIdx - 1; i >= Math.Max(0, calliIdx - 100); i--)
            {
                var instr = instructions[i];

                if (instr.OpCode == OpCodes.Br || instr.OpCode == OpCodes.Br_S)
                {
                    var target = instr.Operand as Instruction;
                    if (target != null)
                    {
                        int targetIdx = instructions.IndexOf(target);
                        if (targetIdx > i && targetIdx <= calliIdx + 10)
                        {
                            for (int j = i - 1; j >= Math.Max(0, i - 20); j--)
                            {
                                if (
                                    instructions[j].OpCode == OpCodes.Bgt
                                    || instructions[j].OpCode == OpCodes.Bgt_S
                                    || instructions[j].OpCode == OpCodes.Blt
                                    || instructions[j].OpCode == OpCodes.Blt_S
                                    || instructions[j].OpCode == OpCodes.Beq
                                    || instructions[j].OpCode == OpCodes.Beq_S
                                    || instructions[j].OpCode == OpCodes.Bne_Un
                                    || instructions[j].OpCode == OpCodes.Bne_Un_S
                                )
                                {
                                    for (int k = j - 1; k >= Math.Max(0, j - 5); k--)
                                    {
                                        if (Fish.Shared.Utils.IsIntegerConstant(instructions[k]))
                                        {
                                            int val = Fish.Shared.Utils.GetConstantValue(instructions[k]);
                                            if (val >= 0 && val <= 255)
                                                return (byte)val;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return null;
        }

        private int FindPrevOpcode(IList<Instruction> instructions, int start, OpCode opcode, int maxSearch)
        {
            for (int i = start; i >= Math.Max(0, start - maxSearch); i--)
            {
                if (instructions[i].OpCode == opcode)
                    return i;
            }
            return -1;
        }

        #endregion

        #region VM Bytecode Extraction

        private byte[] ExtractBytecode(MethodDef method)
        {
            foreach (var instr in method.Body.Instructions)
            {
                if (instr.OpCode == OpCodes.Ldsflda && instr.Operand is IField fieldRef)
                {
                    byte[] data = TryGetFieldData(fieldRef);
                    if (data != null && data.Length > 20)
                        return data;
                }
            }
            return null;
        }

        private byte[] TryGetFieldData(IField fieldRef)
        {
            var fieldDef = fieldRef.ResolveFieldDef();

            if (fieldDef != null && fieldDef.HasFieldRVA && fieldDef.InitialValue != null && fieldDef.InitialValue.Length > 0)
                return fieldDef.InitialValue;

            string targetFieldFullName = fieldRef.FullName;

            foreach (var t in module.GetTypes())
            {
                foreach (var f in t.Fields)
                {
                    if (f.FullName == targetFieldFullName && f.HasFieldRVA && f.InitialValue != null)
                        return f.InitialValue;
                }
            }

            if (fieldDef != null && fieldDef.FieldType is ValueTypeSig vtSig)
            {
                var typeDef = vtSig.TypeDefOrRef.ResolveTypeDef();
                if (typeDef != null)
                {
                    foreach (var innerField in typeDef.Fields)
                    {
                        if (innerField.HasFieldRVA && innerField.InitialValue != null && innerField.InitialValue.Length > 0)
                            return innerField.InitialValue;
                    }

                    if (typeDef.ClassLayout != null && typeDef.ClassLayout.ClassSize > 0)
                    {
                        foreach (var t in module.GetTypes())
                        {
                            foreach (var f in t.Fields)
                            {
                                if (f.HasFieldRVA && f.InitialValue != null && f.FieldType is ValueTypeSig vts && vts.TypeDefOrRef.FullName == typeDef.FullName && f.Name == fieldRef.Name)
                                {
                                    return f.InitialValue;
                                }
                            }
                        }
                    }
                }
            }

            return null;
        }

        #endregion
    }
}
