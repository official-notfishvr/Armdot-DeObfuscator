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
        private Dictionary<IField, IMethod[]> calliArrays;
        private Dictionary<string, byte[]> fieldData = new Dictionary<string, byte[]>();
        private ModuleDefMD module;
        private int patchedCallis = 0;
        private int processedMethods = 0;
        private int resolvedAccesses = 0;
        private int vmStringsDeobfuscated = 0;

        public bool EnableVMStringDecoding { get; set; } = false;

        public void Execute(IContext context)
        {
            module = context.ModuleDefinition;
            calliArrays = Calli.AnalyzeFunctionPointerArrays(module);
            LoadFieldRVAData();

            foreach (var type in module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody)
                        continue;

                    try
                    {
                        bool methodModified = false;

                        if (EnableVMStringDecoding)
                        {
                            var decodedStrings = DecodeVMStrings(method);
                            if (decodedStrings.Count > 0)
                            {
                                vmStringsDeobfuscated += decodedStrings.Count;
                                if (InjectDecodedStrings(method, decodedStrings))
                                {
                                    methodModified = true;
                                }
                            }
                        }

                        int patched = DeobfuscateCalli(method);
                        if (patched > 0)
                        {
                            patchedCallis += patched;
                            methodModified = true;
                        }

                        if (SimplifyVirtualStack(method))
                        {
                            methodModified = true;
                        }

                        if (methodModified)
                            processedMethods++;
                    }
                    catch (Exception ex)
                    {
                        Logger.StageError($"Error in {method.Name}: {ex.Message}");
                    }
                }
            }

            if (patchedCallis > 0 || resolvedAccesses > 0 || vmStringsDeobfuscated > 0)
            {
                if (vmStringsDeobfuscated > 0)
                    Logger.Info($"    Decoded {vmStringsDeobfuscated} VM strings");
                if (patchedCallis > 0)
                    Logger.Info($"    Patched {patchedCallis} calli instructions");
                if (resolvedAccesses > 0)
                    Logger.Info($"    Simplified {resolvedAccesses} virtual stack accesses");
                Logger.Info($"    Processed {processedMethods} methods");
            }
        }

        private void LoadFieldRVAData()
        {
            foreach (var type in module.GetTypes())
            {
                foreach (var field in type.Fields.Where(f => f.IsStatic && f.HasFieldRVA))
                {
                    try
                    {
                        var rva = field.RVA;
                        if ((uint)rva == 0)
                            continue;

                        int size = 16384;
                        if (field.FieldType is ValueTypeSig vts)
                        {
                            var td = vts.TypeDefOrRef.ResolveTypeDef();
                            if (td != null && td.ClassLayout != null)
                                size = (int)td.ClassLayout.ClassSize;
                        }

                        var reader = module.Metadata.PEImage.CreateReader(rva);
                        if (reader.Length > 0)
                        {
                            int toRead = (int)Math.Min(reader.Length, size);
                            fieldData[field.FullName] = reader.ReadBytes(toRead);
                        }
                    }
                    catch { }
                }
            }
        }

        private List<string> DecodeVMStrings(MethodDef method)
        {
            var decoded = new List<string>();
            var instrs = method.Body.Instructions;

            IField bytecodeField = null;
            for (int i = 0; i < Math.Min(instrs.Count, 150); i++)
            {
                if (instrs[i].OpCode == OpCodes.Ldsflda && instrs[i].Operand is IField f)
                {
                    bytecodeField = f;
                    break;
                }
            }
            if (bytecodeField == null)
                return decoded;

            Instruction switchInstr = null;
            int subVal = 0;
            for (int i = 0; i < Math.Min(instrs.Count, 500); i++)
            {
                if (instrs[i].OpCode == OpCodes.Switch)
                {
                    switchInstr = instrs[i];
                    for (int j = i - 1; j >= Math.Max(0, i - 10); j--)
                    {
                        if (instrs[j].OpCode == OpCodes.Sub)
                        {
                            for (int k = j - 1; k >= Math.Max(0, j - 5); k--)
                            {
                                if (Fish.Shared.Utils.IsIntegerConstant(instrs[k]))
                                {
                                    subVal = Fish.Shared.Utils.GetConstantValue(instrs[k]);
                                    break;
                                }
                            }
                            break;
                        }
                    }
                    break;
                }
            }
            if (switchInstr == null)
                return decoded;

            if (!fieldData.TryGetValue(bytecodeField.FullName, out byte[] bytecode))
                return decoded;

            var targets = (Instruction[])switchInstr.Operand;
            int stringOpcode = -1;

            for (int caseIdx = 0; caseIdx < targets.Length; caseIdx++)
            {
                int handlerIdx = instrs.IndexOf(targets[caseIdx]);
                if (handlerIdx < 0)
                    continue;

                int opcode = caseIdx + subVal;
                bool foundXor = false;

                for (int j = handlerIdx; j < Math.Min(handlerIdx + 150, instrs.Count); j++)
                {
                    if (instrs[j].OpCode == OpCodes.Switch)
                        break;

                    if (instrs[j].OpCode == OpCodes.Br || instrs[j].OpCode == OpCodes.Br_S)
                    {
                        if (instrs[j].Operand is Instruction target)
                        {
                            int targetIdx = instrs.IndexOf(target);
                            if (targetIdx != -1 && targetIdx < handlerIdx - 10)
                                break;
                        }
                    }

                    if (instrs[j].OpCode == OpCodes.Xor)
                        foundXor = true;

                    if (instrs[j].OpCode == OpCodes.Newobj && instrs[j].Operand is IMethod ctor)
                    {
                        if (ctor.DeclaringType?.FullName == "System.String")
                        {
                            if (foundXor)
                            {
                                stringOpcode = opcode;
                                break;
                            }
                        }
                    }
                }
                if (stringOpcode != -1)
                    break;
            }

            if (stringOpcode == -1)
                return decoded;

            int ip = 0;
            int iterations = 0;

            while (ip < bytecode.Length && iterations++ < 100000)
            {
                int op = bytecode[ip++];

                if (op != stringOpcode)
                    continue;

                if (ip + 16 > bytecode.Length)
                    break;

                byte xorKey = bytecode[ip];
                int strLen = BitConverter.ToInt32(bytecode, ip + 8);

                if (strLen > 0 && strLen < 4000 && ip + 12 + strLen <= bytecode.Length)
                {
                    if (strLen % 2 == 0)
                    {
                        var chars = new char[strLen / 2];
                        int printableCount = 0;
                        for (int k = 0; k < strLen / 2; k++)
                        {
                            byte b1 = (byte)(bytecode[ip + 12 + k * 2] ^ xorKey);
                            byte b2 = (byte)(bytecode[ip + 12 + k * 2 + 1] ^ xorKey);
                            char c = (char)(b1 | (b2 << 8));
                            chars[k] = c;
                            if (char.IsLetterOrDigit(c) || char.IsPunctuation(c) || char.IsWhiteSpace(c))
                                printableCount++;
                        }

                        if (printableCount > (strLen / 2.0) * 0.7)
                        {
                            string result = new string(chars).TrimEnd('\0');
                            if (result.Length > 1 && IsPrintable(result))
                            {
                                decoded.Add(result);
                                ip += 12 + strLen;
                                continue;
                            }
                        }
                    }

                    var asciiChars = new char[strLen];
                    int asciiPrintable = 0;
                    for (int k = 0; k < strLen; k++)
                    {
                        char c = (char)(bytecode[ip + 12 + k] ^ xorKey);
                        asciiChars[k] = c;
                        if (char.IsLetterOrDigit(c) || char.IsPunctuation(c) || char.IsWhiteSpace(c))
                            asciiPrintable++;
                    }

                    if (asciiPrintable > strLen * 0.7)
                    {
                        string result = new string(asciiChars).TrimEnd('\0');
                        if (result.Length > 1 && IsPrintable(result))
                        {
                            decoded.Add(result);
                            ip += 12 + strLen;
                            continue;
                        }
                    }
                }
            }

            if (decoded.Count > 0)
            {
                Logger.Info($"      {method.Name}: Decoded {decoded.Count} strings");
            }

            return decoded;
        }

        private bool InjectDecodedStrings(MethodDef method, List<string> strings)
        {
            if (strings.Count == 0)
                return false;

            var validStrings = strings.Where(s => IsPrintable(s) && s.Length > 0).ToList();
            if (validStrings.Count == 0)
                return false;

            IMethod targetMethod = null;
            IMethod bestCandidate = null;

            foreach (var instr in method.Body.Instructions)
            {
                if (instr.OpCode == OpCodes.Call && instr.Operand is IMethod m)
                {
                    if (m.DeclaringType != null && m.DeclaringType.FullName != "System.String" && m.DeclaringType.FullName != "System.Object" && m.MethodSig != null)
                    {
                        int stringParamCount = 0;
                        int arrayParamCount = 0;

                        foreach (var param in m.MethodSig.Params)
                        {
                            if (param.FullName == "System.String")
                                stringParamCount++;
                            else if (param is SZArraySig sz && sz.Next.FullName == "System.String")
                                arrayParamCount++;
                        }

                        if (stringParamCount > 0 || arrayParamCount > 0)
                        {
                            bestCandidate = m;

                            if (m.DeclaringType.FullName == method.DeclaringType.FullName)
                            {
                                targetMethod = m;
                                break;
                            }
                        }
                    }
                }
            }

            if (targetMethod == null && bestCandidate != null)
                targetMethod = bestCandidate;

            if (targetMethod == null)
            {
                targetMethod = FindWrapperMethod(method, validStrings.Count);
                if (targetMethod != null) { }
            }

            if (targetMethod == null)
            {
                return false;
            }

            method.Body.Instructions.Clear();
            method.Body.ExceptionHandlers.Clear();
            method.Body.Variables.Clear();

            int strIdx = 0;
            var paramTypes = targetMethod.MethodSig.Params;

            int regularStringParams = 0;
            int arrayParams = 0;

            foreach (var param in paramTypes)
            {
                if (param.FullName == "System.String")
                    regularStringParams++;
                else if (param is SZArraySig sz && sz.Next.FullName == "System.String")
                    arrayParams++;
            }

            int stringsForRegular = Math.Min(regularStringParams, validStrings.Count);
            int remainingStrings = validStrings.Count - stringsForRegular;
            int stringsPerArray = arrayParams > 0 ? remainingStrings / arrayParams : 0;

            foreach (var param in paramTypes)
            {
                if (param.FullName == "System.String")
                {
                    string val = strIdx < validStrings.Count ? validStrings[strIdx++] : string.Empty;
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, val));
                }
                else if (param is SZArraySig sz && sz.Next.FullName == "System.String")
                {
                    int arrSize = Math.Min(stringsPerArray, validStrings.Count - strIdx);

                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, arrSize));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Newarr, method.Module.CorLibTypes.String));

                    for (int i = 0; i < arrSize; i++)
                    {
                        method.Body.Instructions.Add(Instruction.Create(OpCodes.Dup));
                        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4, i));
                        string val = strIdx < validStrings.Count ? validStrings[strIdx++] : string.Empty;
                        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, val));
                        method.Body.Instructions.Add(Instruction.Create(OpCodes.Stelem_Ref));
                    }
                }
                else
                {
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
                }
            }

            method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, targetMethod));

            if (method.ReturnType.FullName != "System.Void")
            {
                if (targetMethod.MethodSig.RetType.FullName == "System.Void")
                {
                    if (method.ReturnType.FullName == "System.String")
                    {
                        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, string.Empty));
                    }
                    else if (method.ReturnType.FullName == "System.Boolean")
                    {
                        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                    }
                    else if (method.ReturnType.FullName == "System.Int32")
                    {
                        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                    }
                    else
                    {
                        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
                    }
                }
            }

            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

            return true;
        }

        private IMethod FindWrapperMethod(MethodDef sourceMethod, int stringCount)
        {
            var declaringType = sourceMethod.DeclaringType;
            if (declaringType == null)
                return null;

            IMethod bestMatch = null;
            int bestScore = 0;

            foreach (var method in declaringType.Methods)
            {
                if (method == sourceMethod || !method.HasBody || method.Parameters.Count == 0)
                    continue;

                int stringParams = 0;
                int arrayParams = 0;

                foreach (var param in method.Parameters)
                {
                    if (param.Type.FullName == "System.String")
                        stringParams++;
                    else if (param.Type is SZArraySig sz && sz.Next.FullName == "System.String")
                        arrayParams++;
                }

                if (stringParams + arrayParams == 0)
                    continue;

                int totalParams = stringParams + arrayParams;
                int score = 0;

                if (totalParams == 4 && stringParams == 2 && arrayParams == 2)
                {
                    score = 1000;
                }
                else if (stringParams > 0 && arrayParams > 0)
                {
                    score = 100 + totalParams;
                }
                else if (totalParams == 1 && arrayParams == 1)
                {
                    score = 10;
                }
                else if (stringParams == stringCount)
                {
                    score = 50;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatch = method;
                }
            }

            return bestMatch;
        }

        private bool SimplifyVirtualStack(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            var locals = method.Body.Variables;
            bool modified = false;

            var stackArrays = IdentifyStackArrays(method);
            if (stackArrays.Count == 0)
                return false;

            var stackPointer = IdentifyStackPointer(method);
            var virtualLocals = new Dictionary<(Local, int), Local>();

            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];

                if (IsElementAccess(instr.OpCode))
                {
                    if (TryResolveStackAccess(instructions, i, locals, stackPointer, out Local arrayLocal, out int index))
                    {
                        if (stackArrays.Contains(arrayLocal))
                        {
                            var vLocal = GetOrCreateVirtualLocal(method, arrayLocal, index, virtualLocals);
                            ReplaceWithLocalAccess(method, i, vLocal);
                            resolvedAccesses++;
                            modified = true;
                        }
                    }
                }
            }

            if (modified)
                LocalCleaner.Clean(method);

            return modified;
        }

        private HashSet<Local> IdentifyStackArrays(MethodDef method)
        {
            var arrays = new HashSet<Local>();
            var instructions = method.Body.Instructions;

            for (int i = 0; i < instructions.Count; i++)
            {
                if (instructions[i].OpCode == OpCodes.Newarr)
                {
                    for (int j = i + 1; j < Math.Min(i + 20, instructions.Count); j++)
                    {
                        if (Fish.Shared.Utils.IsStoreLocal(instructions[j]))
                        {
                            var loc = Fish.Shared.Utils.GetLocal(instructions[j], method.Body.Variables);
                            if (loc != null)
                            {
                                arrays.Add(loc);
                                break;
                            }
                        }
                        if (instructions[j].OpCode.FlowControl != FlowControl.Next && instructions[j].OpCode.FlowControl != FlowControl.Call)
                            break;
                    }
                }
            }
            return arrays;
        }

        private Local IdentifyStackPointer(MethodDef method)
        {
            var candidates = new Dictionary<Local, int>();
            var instructions = method.Body.Instructions;

            for (int i = 0; i < instructions.Count; i++)
            {
                if (IsElementAccess(instructions[i].OpCode))
                {
                    int idx = i - 1;
                    while (idx >= 0 && instructions[idx].OpCode == OpCodes.Nop)
                        idx--;
                    if (idx < 0)
                        continue;

                    if (Fish.Shared.Utils.IsLoadLocal(instructions[idx]))
                    {
                        var l = Fish.Shared.Utils.GetLocal(instructions[idx], method.Body.Variables);
                        if (l != null && l.Type.FullName == "System.Int32")
                        {
                            if (!candidates.ContainsKey(l))
                                candidates[l] = 0;
                            candidates[l]++;
                        }
                    }
                }
            }

            return candidates.OrderByDescending(x => x.Value).Select(x => x.Key).FirstOrDefault();
        }

        private bool IsElementAccess(OpCode op)
        {
            return op == OpCodes.Ldelem_Ref || op == OpCodes.Stelem_Ref || op == OpCodes.Ldelem_I4 || op == OpCodes.Stelem_I4 || op == OpCodes.Ldelem_I8 || op == OpCodes.Stelem_I8 || op == OpCodes.Ldelem_U1 || op == OpCodes.Stelem_I1;
        }

        private bool TryResolveStackAccess(IList<Instruction> instrs, int accessIndex, IList<Local> locals, Local stackPointer, out Local arrayLocal, out int index)
        {
            arrayLocal = null;
            index = -1;
            if (accessIndex < 2)
                return false;

            int idx = accessIndex - 1;
            while (idx >= 0 && instrs[idx].OpCode == OpCodes.Nop)
                idx--;
            if (idx < 0)
                return false;

            int resolvedIndex = -1;
            if (Fish.Shared.Utils.IsIntegerConstant(instrs[idx]))
            {
                resolvedIndex = Fish.Shared.Utils.GetConstantValue(instrs[idx]);
                idx--;
            }
            else if (Fish.Shared.Utils.IsLoadLocal(instrs[idx]))
            {
                var l = Fish.Shared.Utils.GetLocal(instrs[idx], locals);
                if (l != null && (stackPointer == null || l.Index == stackPointer.Index))
                {
                    resolvedIndex = EstimateLocalValueAt(instrs, idx, locals, l);
                    idx--;
                }
            }

            if (resolvedIndex == -1)
                return false;

            while (idx >= 0 && instrs[idx].OpCode == OpCodes.Nop)
                idx--;
            if (idx >= 0 && Fish.Shared.Utils.IsLoadLocal(instrs[idx]))
            {
                arrayLocal = Fish.Shared.Utils.GetLocal(instrs[idx], locals);
                index = resolvedIndex;
                return true;
            }

            return false;
        }

        private int EstimateLocalValueAt(IList<Instruction> instrs, int index, IList<Local> locals, Local local)
        {
            if (local == null)
                return -1;
            for (int i = index - 1; i >= 0; i--)
            {
                if (Fish.Shared.Utils.IsStoreLocal(instrs[i]))
                {
                    var stLocal = Fish.Shared.Utils.GetLocal(instrs[i], locals);
                    if (stLocal != null && stLocal.Index == local.Index)
                    {
                        int lookBack = i - 1;
                        while (lookBack >= 0 && instrs[lookBack].OpCode == OpCodes.Nop)
                            lookBack--;
                        if (lookBack >= 0 && Fish.Shared.Utils.IsIntegerConstant(instrs[lookBack]))
                            return Fish.Shared.Utils.GetConstantValue(instrs[lookBack]);
                        return -1;
                    }
                }
            }
            return -1;
        }

        private Local GetOrCreateVirtualLocal(MethodDef method, Local array, int index, Dictionary<(Local, int), Local> map)
        {
            if (index < 0)
                index = 0;
            if (map.TryGetValue((array, index), out var vLocal))
                return vLocal;

            TypeSig elementType = method.Module.CorLibTypes.Object;
            if (array.Type is SZArraySig sz)
                elementType = sz.Next;

            vLocal = new Local(elementType, $"vstack_{array.Index}_{index}");
            method.Body.Variables.Add(vLocal);
            map[(array, index)] = vLocal;
            return vLocal;
        }

        private void ReplaceWithLocalAccess(MethodDef method, int index, Local vLocal)
        {
            var instrs = method.Body.Instructions;
            var op = instrs[index].OpCode;

            int cursor = index - 1;
            int toRemove = 2;
            while (cursor >= 0 && toRemove > 0)
            {
                if (instrs[cursor].OpCode != OpCodes.Nop)
                {
                    instrs[cursor].OpCode = OpCodes.Nop;
                    instrs[cursor].Operand = null;
                    toRemove--;
                }
                cursor--;
            }

            instrs[index].OpCode = op.Name.StartsWith("ldelem") ? OpCodes.Ldloc : OpCodes.Stloc;
            instrs[index].Operand = vLocal;
        }

        private int DeobfuscateCalli(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            int totalPatched = 0;
            bool modified = true;

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

                    int ldelemIdx = FindPrevOpcode(instructions, i - 1, OpCodes.Ldelem_I, 10);
                    if (ldelemIdx == -1)
                        continue;

                    int ldsfldIdx = FindPrevOpcode(instructions, ldelemIdx - 1, OpCodes.Ldsfld, 10);
                    if (ldsfldIdx == -1)
                        continue;

                    IField arrayField = instructions[ldsfldIdx].Operand as IField;
                    if (arrayField == null)
                        continue;

                    IMethod[] funcPtrs = GetFunctionPointers(arrayField);
                    if (funcPtrs != null)
                    {
                        IMethod target = null;
                        MethodSig sig = calliSig as MethodSig;
                        if (sig != null)
                        {
                            foreach (var fp in funcPtrs)
                            {
                                if (fp != null && SignaturesMatch(sig, fp.MethodSig))
                                {
                                    target = fp;
                                    break;
                                }
                            }
                        }

                        if (target != null)
                        {
                            instructions[ldsfldIdx].OpCode = OpCodes.Nop;
                            for (int k = ldsfldIdx + 1; k < i; k++)
                                instructions[k].OpCode = OpCodes.Nop;
                            instructions[i].OpCode = OpCodes.Call;
                            instructions[i].Operand = target;
                            totalPatched++;
                            modified = true;
                        }
                    }
                }
            }
            return totalPatched;
        }

        private bool SignaturesMatch(MethodSig a, MethodSig b)
        {
            if (a == null || b == null)
                return false;
            if (a.Params.Count != b.Params.Count)
                return false;
            if (a.RetType.FullName != b.RetType.FullName)
                return false;
            for (int i = 0; i < a.Params.Count; i++)
                if (a.Params[i].FullName != b.Params[i].FullName)
                    return false;
            return true;
        }

        private int FindPrevOpcode(IList<Instruction> instrs, int start, OpCode op, int max)
        {
            for (int i = start; i >= Math.Max(0, start - max); i--)
                if (instrs[i].OpCode == op)
                    return i;
            return -1;
        }

        private IMethod[] GetFunctionPointers(IField field)
        {
            foreach (var kvp in calliArrays)
                if (kvp.Key.FullName == field.FullName)
                    return kvp.Value;
            return null;
        }

        private bool IsPrintable(string s)
        {
            if (string.IsNullOrEmpty(s))
                return false;
            foreach (char c in s)
            {
                if (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t')
                    return false;
            }
            return true;
        }
    }
}
