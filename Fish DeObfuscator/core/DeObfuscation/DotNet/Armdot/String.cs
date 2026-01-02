using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Fish.Shared;

namespace Fish_DeObfuscator.core.DeObfuscation.DotNet.Armdot
{
    public class String : IStage
    {
        #region Fields

        private Dictionary<string, byte[]> staticFieldData = new Dictionary<string, byte[]>();
        private int deobfuscatedStrings = 0;

        #endregion

        #region IStage

        public void Execute(IContext context)
        {
            var module = context.ModuleDefinition;
            if (module == null)
                return;

            ExtractStaticFieldData(module);
            DeobfuscateStrings(module);

            if (deobfuscatedStrings > 0)
                Logger.Detail($"Decrypted {deobfuscatedStrings} strings");
        }

        #endregion

        #region String Static Field Data Extraction

        private void ExtractStaticFieldData(ModuleDefMD module)
        {
            try
            {
                foreach (var type in module.GetTypes())
                foreach (var field in type.Fields)
                    if (field.IsStatic && field.InitialValue != null && field.InitialValue.Length > 0)
                        staticFieldData[field.FullName] = field.InitialValue;
            }
            catch { }
        }

        #endregion

        #region String Deobfuscation Core

        private void DeobfuscateStrings(ModuleDefMD module)
        {
            foreach (var type in module.GetTypes())
            foreach (var method in type.Methods)
                if (method.HasBody)
                    DeobfuscateMethodStrings(method);
        }

        private void DeobfuscateMethodStrings(MethodDef method)
        {
            try
            {
                var instructions = method.Body.Instructions.ToList();
                var body = method.Body;
                var patterns = FindStringConstructionPatterns(instructions);

                foreach (var pattern in patterns.OrderByDescending(p => p.StartIndex))
                {
                    var decodedString = DecodeString(pattern);
                    if (!string.IsNullOrEmpty(decodedString))
                    {
                        ReplaceWithString(body, instructions, pattern.StartIndex, pattern.EndIndex, decodedString);
                        deobfuscatedStrings++;
                        instructions = method.Body.Instructions.ToList();
                    }
                }
            }
            catch { }
        }

        #endregion

        #region String Pattern Classes

        private class StringPattern
        {
            public int StartIndex { get; set; }
            public int EndIndex { get; set; }
            public int ArraySize { get; set; }
            public Dictionary<int, CharacterAssignment> CharAssignments { get; set; } = new Dictionary<int, CharacterAssignment>();
            public List<Instruction> Instructions { get; set; }
            public Instruction ArrayVariable { get; set; }
        }

        private class CharacterAssignment
        {
            public int Index { get; set; }
            public int Value1 { get; set; }
            public int Value2 { get; set; }
            public bool IsFieldAccess1 { get; set; }
            public bool IsFieldAccess2 { get; set; }
            public string FieldName1 { get; set; }
            public int FieldOffset1 { get; set; }
            public string FieldName2 { get; set; }
            public int FieldOffset2 { get; set; }
        }

        private class ValueInfo
        {
            public int Value { get; set; }
            public bool IsFieldAccess { get; set; }
            public string FieldName { get; set; }
            public int FieldOffset { get; set; }
        }

        #endregion

        #region String Pattern Detection

        private List<StringPattern> FindStringConstructionPatterns(List<Instruction> instructions)
        {
            var patterns = new List<StringPattern>();

            for (int i = 0; i < instructions.Count - 5; i++)
            {
                if (IsIntegerConstant(instructions[i]) && i + 1 < instructions.Count && instructions[i + 1].OpCode == OpCodes.Newarr)
                {
                    var arrayType = instructions[i + 1].Operand as ITypeDefOrRef;
                    if (arrayType?.FullName == "System.Char")
                    {
                        int arraySize = GetConstantValue(instructions[i]);
                        if (arraySize > 0 && arraySize <= 100)
                        {
                            if (
                                i + 2 < instructions.Count
                                && (
                                    instructions[i + 2].OpCode == OpCodes.Dup
                                    || instructions[i + 2].OpCode == OpCodes.Stloc
                                    || instructions[i + 2].OpCode == OpCodes.Stloc_S
                                    || instructions[i + 2].OpCode == OpCodes.Stloc_0
                                    || instructions[i + 2].OpCode == OpCodes.Stloc_1
                                    || instructions[i + 2].OpCode == OpCodes.Stloc_2
                                    || instructions[i + 2].OpCode == OpCodes.Stloc_3
                                )
                            )
                            {
                                int constructorIndex = FindStringConstructor(instructions, i + 3, arraySize * 20);
                                if (constructorIndex != -1)
                                {
                                    var pattern = AnalyzeStringPattern(instructions, i, constructorIndex, arraySize);
                                    if (pattern != null && pattern.CharAssignments.Count > 0)
                                        patterns.Add(pattern);
                                }
                            }
                        }
                    }
                }
            }
            return patterns;
        }

        private StringPattern AnalyzeStringPattern(List<Instruction> instructions, int arrayCreateIndex, int constructorIndex, int arraySize)
        {
            var pattern = new StringPattern
            {
                StartIndex = arrayCreateIndex,
                EndIndex = constructorIndex,
                ArraySize = arraySize,
                Instructions = instructions,
            };
            ParseCharacterAssignments(instructions, arrayCreateIndex, constructorIndex, pattern);
            return pattern;
        }

        private int FindStringConstructor(List<Instruction> instructions, int startIndex, int maxLookAhead)
        {
            for (int i = startIndex; i < Math.Min(instructions.Count, startIndex + maxLookAhead); i++)
            {
                if (instructions[i].OpCode == OpCodes.Newobj)
                {
                    var constructor = instructions[i].Operand as IMethod;
                    if (constructor?.DeclaringType.FullName == "System.String" && constructor.MethodSig.Params.Count == 1 && constructor.MethodSig.Params[0].FullName == "System.Char[]")
                        return i;
                }
            }
            return -1;
        }

        #endregion

        #region String Character Assignment Parsing

        private void ParseCharacterAssignments(List<Instruction> instructions, int startIndex, int endIndex, StringPattern pattern)
        {
            int searchEnd = Math.Min(instructions.Count, endIndex + 5);
            for (int i = startIndex; i < searchEnd; i++)
            {
                if (IsArrayElementAssignment(instructions, i))
                {
                    var assignment = ParseCompleteAssignment(instructions, i);
                    if (assignment != null && assignment.Index >= 0 && assignment.Index < pattern.ArraySize)
                        if (!pattern.CharAssignments.ContainsKey(assignment.Index))
                            pattern.CharAssignments[assignment.Index] = assignment;
                }
            }
        }

        private bool IsArrayElementAssignment(List<Instruction> instructions, int index)
        {
            if (index < instructions.Count)
            {
                var instr = instructions[index];
                return instr.OpCode == OpCodes.Stelem_I2 || instr.OpCode == OpCodes.Stelem_I4 || instr.OpCode == OpCodes.Stelem_I8 || instr.OpCode == OpCodes.Stelem_I1 || instr.OpCode == OpCodes.Stelem_R4 || instr.OpCode == OpCodes.Stelem_R8 || instr.OpCode == OpCodes.Stelem_Ref;
            }
            return false;
        }

        private CharacterAssignment ParseCompleteAssignment(List<Instruction> instructions, int assignmentIndex)
        {
            try
            {
                int xorIndex = -1;
                for (int i = assignmentIndex - 1; i >= Math.Max(0, assignmentIndex - 15); i--)
                    if (instructions[i].OpCode == OpCodes.Xor)
                    {
                        xorIndex = i;
                        break;
                    }

                if (xorIndex == -1)
                    return null;

                var value2Info = ParseValueEndingAt(instructions, xorIndex - 1);
                if (value2Info == null)
                    return null;

                int value1Start = GetInstructionStart(instructions, xorIndex - 1, value2Info);
                var value1Info = ParseValueEndingAt(instructions, value1Start - 1);
                if (value1Info == null)
                    return null;

                int indexStart = GetInstructionStart(instructions, value1Start - 1, value1Info);
                int arrayIndex = FindArrayIndex(instructions, indexStart - 1);
                if (arrayIndex == -1)
                    return null;

                return new CharacterAssignment
                {
                    Index = arrayIndex,
                    Value1 = value1Info.Value,
                    IsFieldAccess1 = value1Info.IsFieldAccess,
                    FieldName1 = value1Info.FieldName,
                    FieldOffset1 = value1Info.FieldOffset,
                    Value2 = value2Info.Value,
                    IsFieldAccess2 = value2Info.IsFieldAccess,
                    FieldName2 = value2Info.FieldName,
                    FieldOffset2 = value2Info.FieldOffset,
                };
            }
            catch
            {
                return null;
            }
        }

        private int GetInstructionStart(List<Instruction> instructions, int endIndex, ValueInfo valueInfo)
        {
            return valueInfo.IsFieldAccess ? Math.Max(0, endIndex - 5) : endIndex;
        }

        private int FindArrayIndex(List<Instruction> instructions, int searchStart)
        {
            for (int i = searchStart; i >= Math.Max(0, searchStart - 10); i--)
            {
                if (IsIntegerConstant(instructions[i]))
                {
                    int value = GetConstantValue(instructions[i]);
                    if (value >= 0 && value < 100)
                        return value;
                }
            }
            return -1;
        }

        #endregion

        #region String Value Parsing

        private ValueInfo ParseValueEndingAt(List<Instruction> instructions, int endIndex)
        {
            if (endIndex < 0 || endIndex >= instructions.Count)
                return null;

            if (
                endIndex >= 5
                && instructions[endIndex].OpCode == OpCodes.Conv_I4
                && instructions[endIndex - 1].OpCode == OpCodes.Ldind_U1
                && instructions[endIndex - 2].OpCode == OpCodes.Add
                && IsIntegerConstant(instructions[endIndex - 3])
                && instructions[endIndex - 4].OpCode == OpCodes.Conv_I
                && instructions[endIndex - 5].OpCode == OpCodes.Ldsflda
            )
            {
                var field = instructions[endIndex - 5].Operand as IField;
                int offset = GetConstantValue(instructions[endIndex - 3]);

                if (field != null && staticFieldData.ContainsKey(field.FullName))
                {
                    var data = staticFieldData[field.FullName];
                    if (offset >= 0 && offset < data.Length)
                        return new ValueInfo
                        {
                            Value = data[offset],
                            IsFieldAccess = true,
                            FieldName = field.FullName,
                            FieldOffset = offset,
                        };
                }
            }
            else if (IsIntegerConstant(instructions[endIndex]))
            {
                return new ValueInfo { Value = GetConstantValue(instructions[endIndex]), IsFieldAccess = false };
            }
            return null;
        }

        #endregion

        #region String XOR Decryption

        private string DecodeString(StringPattern pattern)
        {
            try
            {
                int actualLength = pattern.ArraySize;
                if (pattern.CharAssignments.Count > 0)
                {
                    int maxAssignmentIndex = pattern.CharAssignments.Keys.Max();
                    actualLength = Math.Max(actualLength, maxAssignmentIndex + 1);
                }

                var chars = new char[actualLength];
                bool hasAllAssignments = true;
                int validCharCount = 0;

                for (int i = 0; i < actualLength; i++)
                {
                    if (pattern.CharAssignments.ContainsKey(i))
                    {
                        var assignment = pattern.CharAssignments[i];
                        chars[i] = (char)(assignment.Value1 ^ assignment.Value2);
                        validCharCount++;
                    }
                    else
                    {
                        hasAllAssignments = false;
                        chars[i] = '\0';
                    }
                }

                if (validCharCount > 0 && (hasAllAssignments || validCharCount >= actualLength * 0.8))
                {
                    string result = new string(chars);
                    return result.TrimEnd('\0');
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region String Instruction Replacement

        private void ReplaceWithString(CilBody body, List<Instruction> instructions, int startIndex, int endIndex, string value)
        {
            try
            {
                var ldstrInstr = new Instruction(OpCodes.Ldstr, value);
                var toRemove = new HashSet<Instruction>();

                for (int i = startIndex; i <= endIndex && i < instructions.Count; i++)
                    toRemove.Add(instructions[i]);

                foreach (var instr in body.Instructions)
                {
                    if (instr.Operand is Instruction target && toRemove.Contains(target))
                        instr.Operand = ldstrInstr;
                    else if (instr.Operand is Instruction[] targets)
                        for (int i = 0; i < targets.Length; i++)
                            if (toRemove.Contains(targets[i]))
                                targets[i] = ldstrInstr;
                }

                foreach (var handler in body.ExceptionHandlers)
                {
                    if (handler.TryStart != null && toRemove.Contains(handler.TryStart))
                        handler.TryStart = ldstrInstr;
                    if (handler.TryEnd != null && toRemove.Contains(handler.TryEnd))
                        handler.TryEnd = ldstrInstr;
                    if (handler.HandlerStart != null && toRemove.Contains(handler.HandlerStart))
                        handler.HandlerStart = ldstrInstr;
                    if (handler.HandlerEnd != null && toRemove.Contains(handler.HandlerEnd))
                        handler.HandlerEnd = ldstrInstr;
                    if (handler.FilterStart != null && toRemove.Contains(handler.FilterStart))
                        handler.FilterStart = ldstrInstr;
                }

                for (int i = endIndex; i >= startIndex && i < body.Instructions.Count; i--)
                    if (i < body.Instructions.Count && toRemove.Contains(body.Instructions[i]))
                        body.Instructions.RemoveAt(i);

                if (startIndex <= body.Instructions.Count)
                    body.Instructions.Insert(startIndex, ldstrInstr);
                else
                    body.Instructions.Add(ldstrInstr);
            }
            catch { }
        }

        #endregion

        #region String Helper Methods

        private bool IsIntegerConstant(Instruction instruction)
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

        private int GetConstantValue(Instruction instruction)
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
                        return Convert.ToInt32(instruction.Operand);
                    default:
                        return 0;
                }
            }
            catch
            {
                return 0;
            }
        }

        #endregion
    }
}
