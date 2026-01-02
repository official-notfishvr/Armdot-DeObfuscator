using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Fish.Shared
{
    public static class Utils
    {
        [ThreadStatic]
        private static Random _random;
        private static Random random => _random ??= new Random(Environment.TickCount ^ Thread.CurrentThread.ManagedThreadId);

        private static readonly object _usedNamesLock = new object();
        private static readonly HashSet<string> usedNames = new HashSet<string>();

        public static string GenerateRandomName(int length = 8)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
            string name;
            int attempts = 0;
            do
            {
                char[] stringChars = new char[length];
                for (int i = 0; i < length; i++)
                {
                    stringChars[i] = chars[random.Next(chars.Length)];
                }
                name = new string(stringChars);
                attempts++;

                if (attempts > 1000)
                {
                    name = name + random.Next(1000, 9999).ToString();
                    break;
                }
            } while (IsNameUsed(name));

            AddUsedName(name);
            return name;
        }

        public static string GenerateSpecialCharName(int length = 8)
        {
            const string specialChars = "#$%^&*()_+-=<>?@!|/";
            string name;
            int attempts = 0;
            do
            {
                char[] stringChars = new char[length];
                for (int i = 0; i < length; i++)
                {
                    stringChars[i] = specialChars[random.Next(specialChars.Length)];
                }
                name = new string(stringChars);
                attempts++;

                if (attempts > 1000)
                {
                    name = name + random.Next(1000, 9999).ToString();
                    break;
                }
            } while (IsNameUsed(name));

            AddUsedName(name);
            return name;
        }

        public static string RandomString(int length) => GenerateRandomName(length);

        public static bool IsUnitySpecialType(TypeDef type)
        {
            if (type == null)
                return false;
            try
            {
                if (type.BaseType?.FullName == "UnityEngine.MonoBehaviour"
                    || type.BaseType?.FullName == "UnityEngine.ScriptableObject"
                    || type.FullName.StartsWith("UnityEngine.")
                    || type.FullName.StartsWith("UnityEditor.")
                    || type.FullName.StartsWith("System.")
                    || type.FullName.StartsWith("Microsoft."))
                    return true;

                if (type.BaseType?.FullName == "BepInEx.BaseUnityPlugin"
                    || type.BaseType?.FullName?.Contains("BaseUnityPlugin") == true)
                    return true;

                var baseType = type.BaseType;
                int depth = 0;
                while (baseType != null && depth < 10)
                {
                    var baseFullName = baseType.FullName;
                    if (baseFullName == "UnityEngine.MonoBehaviour"
                        || baseFullName == "UnityEngine.ScriptableObject"
                        || baseFullName == "BepInEx.BaseUnityPlugin")
                        return true;

                    if (baseType is TypeDef baseDef)
                        baseType = baseDef.BaseType;
                    else
                        break;

                    depth++;
                }

                return false;
            }
            catch
            {
                return true;
            }
        }

        public static bool IsEntryPoint(MethodDef method, ModuleDefMD module)
        {
            if (method == null || module == null)
                return false;
            try
            {
                if (module.EntryPoint != null && method == module.EntryPoint)
                    return true;

                if (method.Name == "Main" && method.IsStatic)
                {
                    bool isMainSignature = false;
                    if (method.Parameters.Count == 0)
                        isMainSignature = method.ReturnType.FullName == "System.Void" || method.ReturnType.FullName == "System.Int32";
                    else if (method.Parameters.Count == 1 && method.Parameters[0].Type.FullName == "System.String[]")
                        isMainSignature = method.ReturnType.FullName == "System.Void" || method.ReturnType.FullName == "System.Int32";
                    if (isMainSignature)
                        return true;
                }

                if (method.HasCustomAttributes)
                {
                    foreach (var attr in method.CustomAttributes)
                    {
                        var attrTypeName = attr.AttributeType.FullName;
                        if (attrTypeName.Contains("DllExport") || attrTypeName.Contains("UnmanagedExport") || attrTypeName.Contains("Export"))
                            return true;
                    }
                }

                if (method.IsPublic && method.DeclaringType.IsPublic)
                {
                    if (method.DeclaringType.HasCustomAttributes)
                    {
                        foreach (var attr in method.DeclaringType.CustomAttributes)
                        {
                            var attrTypeName = attr.AttributeType.FullName;
                            if (attrTypeName.Contains("ComVisible") || attrTypeName.Contains("Guid") || attrTypeName.Contains("ClassInterface"))
                                return true;
                        }
                    }
                }
                return false;
            }
            catch
            {
                return true;
            }
        }

        public static bool IsEntryPointType(TypeDef type, ModuleDefMD module)
        {
            if (type == null || module == null)
                return false;
            try
            {
                if (type.HasMethods)
                    foreach (var method in type.Methods)
                        if (IsEntryPoint(method, module))
                            return true;

                if (type.HasCustomAttributes)
                {
                    foreach (var attr in type.CustomAttributes)
                    {
                        var attrTypeName = attr.AttributeType.FullName;
                        if (attrTypeName.Contains("ComVisible") || attrTypeName.Contains("Guid") || attrTypeName.Contains("ClassInterface") || attrTypeName.Contains("DllExport"))
                            return true;
                    }
                }
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static readonly string[] DefaultUnityMethods =
        {
            "Awake",
            "Start",
            "Update",
            "LateUpdate",
            "FixedUpdate",
            "OnEnable",
            "OnDisable",
            "OnDestroy",
            "OnApplicationPause",
            "OnApplicationFocus",
            "OnApplicationQuit",
            "OnTriggerEnter",
            "OnTriggerExit",
            "OnTriggerStay",
            "OnCollisionEnter",
            "OnCollisionExit",
            "OnCollisionStay",
            "OnMouseDown",
            "OnMouseUp",
            "OnMouseEnter",
            "OnMouseExit",
            "OnMouseOver",
            "OnGUI",
            "OnDrawGizmos",
            "OnDrawGizmosSelected",
        };

        public static string[] UnitySpecialMethods { get; set; } = DefaultUnityMethods;

        public static bool IsUnitySpecialMethod(MethodDef method)
        {
            if (method == null)
                return false;
            return Array.IndexOf(UnitySpecialMethods, method.Name) != -1;
        }

        public static void AddUsedName(string name)
        {
            if (!string.IsNullOrEmpty(name))
            {
                lock (_usedNamesLock)
                {
                    usedNames.Add(name);
                }
            }
        }

        public static bool IsIntegerConstant(Instruction instruction)
        {
            if (instruction == null)
                return false;
            return instruction.OpCode == OpCodes.Ldc_I4
                || instruction.OpCode == OpCodes.Ldc_I4_0
                || instruction.OpCode == OpCodes.Ldc_I4_1
                || instruction.OpCode == OpCodes.Ldc_I4_2
                || instruction.OpCode == OpCodes.Ldc_I4_3
                || instruction.OpCode == OpCodes.Ldc_I4_4
                || instruction.OpCode == OpCodes.Ldc_I4_5
                || instruction.OpCode == OpCodes.Ldc_I4_6
                || instruction.OpCode == OpCodes.Ldc_I4_7
                || instruction.OpCode == OpCodes.Ldc_I4_8
                || instruction.OpCode == OpCodes.Ldc_I4_M1
                || instruction.OpCode == OpCodes.Ldc_I4_S;
        }

        public static int GetConstantValue(Instruction instruction)
        {
            if (instruction == null)
                return 0;
            try
            {
                switch (instruction.OpCode.Code)
                {
                    case Code.Ldc_I4_M1:
                        return -1;
                    case Code.Ldc_I4_0:
                        return 0;
                    case Code.Ldc_I4_1:
                        return 1;
                    case Code.Ldc_I4_2:
                        return 2;
                    case Code.Ldc_I4_3:
                        return 3;
                    case Code.Ldc_I4_4:
                        return 4;
                    case Code.Ldc_I4_5:
                        return 5;
                    case Code.Ldc_I4_6:
                        return 6;
                    case Code.Ldc_I4_7:
                        return 7;
                    case Code.Ldc_I4_8:
                        return 8;
                    case Code.Ldc_I4:
                    case Code.Ldc_I4_S:
                        return instruction.Operand != null ? Convert.ToInt32(instruction.Operand) : 0;
                    default:
                        return 0;
                }
            }
            catch
            {
                return 0;
            }
        }

        public static bool IsSafeToProcess(MethodDef method)
        {
            if (method == null)
                return false;
            try
            {
                if (method.IsSpecialName || method.IsConstructor || !method.HasBody)
                    return false;
                if (method.IsRuntimeSpecialName || method.IsAbstract || method.IsVirtual)
                    return false;
                if (IsUnitySpecialMethod(method))
                    return false;
                if (HasComplexControlFlow(method))
                    return false;

                if (method.HasCustomAttributes)
                {
                    foreach (var attr in method.CustomAttributes)
                    {
                        var attrName = attr.AttributeType.Name;
                        if (attrName.Contains("Serialize") || attrName.Contains("DllImport") || attrName.Contains("MonoPInvoke") || attrName.Contains("Unity"))
                            return false;
                    }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool IsSafeToProcess(MethodDef method, ModuleDefMD module)
        {
            if (!IsSafeToProcess(method))
                return false;
            if (IsEntryPoint(method, module))
                return false;
            return true;
        }

        public static bool IsSafeToObfuscate(MethodDef method) => IsSafeToProcess(method);

        public static bool IsSafeToObfuscate(MethodDef method, ModuleDefMD module) => IsSafeToProcess(method, module);

        public static bool HasComplexControlFlow(MethodDef method)
        {
            if (method?.Body?.Instructions == null)
                return true;
            try
            {
                int branchCount = 0;
                int switchCount = 0;
                int exceptionHandlerCount = method.Body.ExceptionHandlers?.Count ?? 0;

                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode.FlowControl == FlowControl.Branch || instruction.OpCode.FlowControl == FlowControl.Cond_Branch)
                        branchCount++;
                    if (instruction.OpCode == OpCodes.Switch)
                        switchCount++;
                }
                return branchCount > 8 || switchCount > 0 || exceptionHandlerCount > 0;
            }
            catch
            {
                return true;
            }
        }

        public static bool IsInControlFlowContext(List<Instruction> instructions, int index)
        {
            if (instructions == null || index < 0 || index >= instructions.Count)
                return false;
            try
            {
                for (int offset = 1; offset <= 3 && index + offset < instructions.Count; offset++)
                {
                    var nextInstruction = instructions[index + offset];
                    if (nextInstruction.OpCode == OpCodes.Nop || nextInstruction.OpCode == OpCodes.Pop)
                        continue;

                    if (nextInstruction.OpCode.FlowControl == FlowControl.Branch || nextInstruction.OpCode.FlowControl == FlowControl.Cond_Branch || nextInstruction.OpCode == OpCodes.Switch)
                        return true;

                    if (nextInstruction.OpCode == OpCodes.Ceq || nextInstruction.OpCode == OpCodes.Cgt || nextInstruction.OpCode == OpCodes.Clt || nextInstruction.OpCode == OpCodes.Cgt_Un || nextInstruction.OpCode == OpCodes.Clt_Un)
                        return true;
                    break;
                }
                return false;
            }
            catch
            {
                return true;
            }
        }

        public static bool IsConstantSafeToReplace(int value, List<Instruction> instructions, int index)
        {
            if (instructions == null || index < 0)
                return false;
            try
            {
                if (value == 0 || value == 1 || value == -1)
                    return false;
                if (Math.Abs(value) > 10000)
                    return false;
                if (IsInControlFlowContext(instructions, index))
                    return false;

                if (index + 1 < instructions.Count)
                {
                    var nextInstruction = instructions[index + 1];
                    if (
                        nextInstruction.OpCode == OpCodes.Ldelem_I1
                        || nextInstruction.OpCode == OpCodes.Ldelem_I2
                        || nextInstruction.OpCode == OpCodes.Ldelem_I4
                        || nextInstruction.OpCode == OpCodes.Ldelem_I8
                        || nextInstruction.OpCode == OpCodes.Ldelem_Ref
                        || nextInstruction.OpCode == OpCodes.Stelem_I1
                        || nextInstruction.OpCode == OpCodes.Stelem_I2
                        || nextInstruction.OpCode == OpCodes.Stelem_I4
                        || nextInstruction.OpCode == OpCodes.Stelem_I8
                        || nextInstruction.OpCode == OpCodes.Stelem_Ref
                    )
                        return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void SafeReplaceInstruction(List<Instruction> instructions, Instruction oldInstruction, List<Instruction> newInstructions)
        {
            if (oldInstruction == null || newInstructions == null || newInstructions.Count == 0)
                return;
            try
            {
                int index = instructions.IndexOf(oldInstruction);
                if (index >= 0)
                {
                    instructions.RemoveAt(index);
                    for (int i = 0; i < newInstructions.Count; i++)
                        instructions.Insert(index + i, newInstructions[i]);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error replacing instruction: {ex.Message}");
            }
        }

        public static Random GetRandom() => random;

        public static void ClearUsedNames()
        {
            lock (_usedNamesLock)
            {
                usedNames.Clear();
            }
        }

        public static bool IsNameUsed(string name)
        {
            lock (_usedNamesLock)
            {
                return usedNames.Contains(name);
            }
        }

        public static bool IsLoadLocal(Instruction instr)
        {
            if (instr == null)
                return false;
            return instr.OpCode == OpCodes.Ldloc || instr.OpCode == OpCodes.Ldloc_0 || instr.OpCode == OpCodes.Ldloc_1 || instr.OpCode == OpCodes.Ldloc_2 || instr.OpCode == OpCodes.Ldloc_3 || instr.OpCode == OpCodes.Ldloc_S;
        }

        public static bool IsStoreLocal(Instruction instr)
        {
            if (instr == null)
                return false;
            return instr.OpCode == OpCodes.Stloc || instr.OpCode == OpCodes.Stloc_0 || instr.OpCode == OpCodes.Stloc_1 || instr.OpCode == OpCodes.Stloc_2 || instr.OpCode == OpCodes.Stloc_3 || instr.OpCode == OpCodes.Stloc_S;
        }

        public static Local GetLocal(Instruction instr, IList<Local> locals)
        {
            if (instr == null)
                return null;
            if (instr.Operand is Local l)
                return l;
            if (instr.OpCode == OpCodes.Ldloc_0 || instr.OpCode == OpCodes.Stloc_0)
                return locals.Count > 0 ? locals[0] : null;
            if (instr.OpCode == OpCodes.Ldloc_1 || instr.OpCode == OpCodes.Stloc_1)
                return locals.Count > 1 ? locals[1] : null;
            if (instr.OpCode == OpCodes.Ldloc_2 || instr.OpCode == OpCodes.Stloc_2)
                return locals.Count > 2 ? locals[2] : null;
            if (instr.OpCode == OpCodes.Ldloc_3 || instr.OpCode == OpCodes.Stloc_3)
                return locals.Count > 3 ? locals[3] : null;
            return null;
        }

        public static Instruction FindInstruction(IList<Instruction> instrs, OpCode opCode)
        {
            foreach (var instr in instrs)
                if (instr.OpCode == opCode)
                    return instr;
            return null;
        }

        public static int EmulateAndResolveLocal(MethodDef method, byte[] blob, Local blobPtr, Local targetLocal, Instruction targetInstr)
        {
            var instrs = method.Body.Instructions;
            var locals = new int[method.Body.Variables.Count];

            int offsetShiftLocalIdx = -1;
            foreach (var instr in instrs)
            {
                if (instr.OpCode == OpCodes.Ldind_I4)
                {
                    int idx = instrs.IndexOf(instr);
                    if (idx >= 2 && instrs[idx - 1].OpCode == OpCodes.Add && IsLoadLocal(instrs[idx - 2]))
                    {
                        var l = GetLocal(instrs[idx - 2], method.Body.Variables);
                        if (l != null)
                        {
                            offsetShiftLocalIdx = l.Index;
                            break;
                        }
                    }
                }
            }

            if (offsetShiftLocalIdx != -1 && offsetShiftLocalIdx < locals.Length)
                locals[offsetShiftLocalIdx] = 4;

            for (int i = 0; i < instrs.Count; i++)
            {
                if (instrs[i] == targetInstr)
                    break;
                if (IsStoreLocal(instrs[i]))
                {
                    int idx = instrs.IndexOf(instrs[i]);
                    if (idx > 0 && IsIntegerConstant(instrs[idx - 1]))
                    {
                        var l = GetLocal(instrs[i], method.Body.Variables);
                        if (l != null && l.Index < locals.Length)
                            locals[l.Index] = GetConstantValue(instrs[idx - 1]);
                    }
                }
            }

            int maxSteps = 20000,
                ip = 0;
            var stack = new Stack<int>();

            while (ip < instrs.Count && maxSteps-- > 0)
            {
                var instr = instrs[ip];
                if (instr == targetInstr)
                    return targetLocal.Index < locals.Length ? locals[targetLocal.Index] : -1;

                try
                {
                    if (IsIntegerConstant(instr))
                        stack.Push(GetConstantValue(instr));
                    else if (IsLoadLocal(instr))
                    {
                        var l = GetLocal(instr, method.Body.Variables);
                        stack.Push(l != null && l.Index < locals.Length ? locals[l.Index] : 0);
                    }
                    else if (IsStoreLocal(instr))
                    {
                        var l = GetLocal(instr, method.Body.Variables);
                        if (stack.Count > 0 && l != null && l.Index < locals.Length)
                            locals[l.Index] = stack.Pop();
                    }
                    else if (instr.OpCode == OpCodes.Add && stack.Count >= 2)
                    {
                        int b = stack.Pop(),
                            a = stack.Pop();
                        stack.Push(a + b);
                    }
                    else if (instr.OpCode == OpCodes.Sub && stack.Count >= 2)
                    {
                        int b = stack.Pop(),
                            a = stack.Pop();
                        stack.Push(a - b);
                    }
                    else if (instr.OpCode == OpCodes.Mul && stack.Count >= 2)
                    {
                        int b = stack.Pop(),
                            a = stack.Pop();
                        stack.Push(a * b);
                    }
                    else if (instr.OpCode == OpCodes.Div && stack.Count >= 2)
                    {
                        int b = stack.Pop(),
                            a = stack.Pop();
                        stack.Push(b == 0 ? 0 : a / b);
                    }
                    else if (instr.OpCode == OpCodes.Ldind_I4 && stack.Count >= 1)
                    {
                        int addr = stack.Pop();
                        stack.Push(blob != null && addr >= 0 && addr + 4 <= blob.Length ? BitConverter.ToInt32(blob, addr) : 0);
                    }
                    else if (instr.OpCode == OpCodes.Pop && stack.Count > 0)
                        stack.Pop();
                    else if (instr.OpCode == OpCodes.Dup && stack.Count > 0)
                        stack.Push(stack.Peek());
                    else if (instr.OpCode == OpCodes.Br || instr.OpCode == OpCodes.Br_S)
                    {
                        var tgt = instr.Operand as Instruction;
                        int tgtIdx = instrs.IndexOf(tgt);
                        if (tgtIdx != -1)
                        {
                            ip = tgtIdx;
                            continue;
                        }
                    }
                    else if (instr.OpCode == OpCodes.Bne_Un || instr.OpCode == OpCodes.Bne_Un_S)
                    {
                        if (stack.Count >= 2)
                        {
                            int b = stack.Pop(),
                                a = stack.Pop();
                            if (a != b)
                            {
                                var tgt = instr.Operand as Instruction;
                                int tgtIdx = instrs.IndexOf(tgt);
                                if (tgtIdx != -1)
                                {
                                    ip = tgtIdx;
                                    continue;
                                }
                            }
                        }
                    }
                    else if ((instr.OpCode == OpCodes.Beq || instr.OpCode == OpCodes.Beq_S) && stack.Count >= 2)
                    {
                        int b = stack.Pop(),
                            a = stack.Pop();
                        if (a == b)
                        {
                            var tgt = instr.Operand as Instruction;
                            int tgtIdx = instrs.IndexOf(tgt);
                            if (tgtIdx != -1)
                            {
                                ip = tgtIdx;
                                continue;
                            }
                        }
                    }
                    else if (instr.OpCode == OpCodes.Ldsflda)
                        stack.Push(0);
                }
                catch { }
                ip++;
            }
            return -1;
        }

        public static int EmulateStackTop(MethodDef method, Instruction targetInstr)
        {
            var instrs = method.Body.Instructions;
            var locals = new int[method.Body.Variables.Count];
            int maxSteps = 20000,
                ip = 0;
            var stack = new Stack<int>();

            while (ip < instrs.Count && maxSteps-- > 0)
            {
                var instr = instrs[ip];
                if (instr == targetInstr)
                    return stack.Count > 0 ? stack.Peek() : -1;

                try
                {
                    if (IsIntegerConstant(instr))
                        stack.Push(GetConstantValue(instr));
                    else if (IsLoadLocal(instr))
                    {
                        var l = GetLocal(instr, method.Body.Variables);
                        stack.Push(l != null && l.Index < locals.Length ? locals[l.Index] : 0);
                    }
                    else if (IsStoreLocal(instr))
                    {
                        var l = GetLocal(instr, method.Body.Variables);
                        if (stack.Count > 0 && l != null && l.Index < locals.Length)
                            locals[l.Index] = stack.Pop();
                    }
                    else if (instr.OpCode == OpCodes.Add && stack.Count >= 2)
                        stack.Push(stack.Pop() + stack.Pop());
                    else if (instr.OpCode == OpCodes.Sub && stack.Count >= 2)
                    {
                        int b = stack.Pop(),
                            a = stack.Pop();
                        stack.Push(a - b);
                    }
                    else if (instr.OpCode == OpCodes.Mul && stack.Count >= 2)
                        stack.Push(stack.Pop() * stack.Pop());
                    else if (instr.OpCode == OpCodes.Div && stack.Count >= 2)
                    {
                        int b = stack.Pop(),
                            a = stack.Pop();
                        stack.Push(b == 0 ? 0 : a / b);
                    }
                    else if (instr.OpCode == OpCodes.Pop && stack.Count > 0)
                        stack.Pop();
                    else if (instr.OpCode == OpCodes.Dup && stack.Count > 0)
                        stack.Push(stack.Peek());
                    else if (instr.OpCode == OpCodes.Br || instr.OpCode == OpCodes.Br_S)
                    {
                        var tgt = instr.Operand as Instruction;
                        int tgtIdx = instrs.IndexOf(tgt);
                        if (tgtIdx != -1)
                        {
                            ip = tgtIdx;
                            continue;
                        }
                    }
                }
                catch { }
                ip++;
            }
            return -1;
        }
    }
}
