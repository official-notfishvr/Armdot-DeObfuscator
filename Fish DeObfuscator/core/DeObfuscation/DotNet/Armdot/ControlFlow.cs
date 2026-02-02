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
        private int totalSimplified = 0;

        public void Execute(IContext context)
        {
            var module = context.ModuleDefinition;
            foreach (var type in module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || method.Body.Instructions.Count < 30)
                        continue;
                    if (ProcessMethod(method))
                        totalSimplified++;
                }
            }

            if (totalSimplified > 0)
                Logger.Info($"    Successfully reconstructed control flow in {totalSimplified} methods");
            else
                Logger.Info("    No methods were simplified by ControlFlow.");
        }

        private bool ProcessMethod(MethodDef method)
        {
            var headers = FindLoopHeaders(method);
            bool modified = false;

            if (headers.Count == 0 && method.Body.Instructions.Count > 50)
            {
                return TryAnalyzeWithoutLoop(method);
            }

            foreach (var head in headers)
            {
                var opcodeVar = IdentifyOpcodeVariable(method, head);
                if (opcodeVar == null)
                    continue;

                var root = FindDispatcherRoot(method, head, opcodeVar);
                if (root == null)
                    continue;

                var mappings = new Dictionary<int, Instruction>();
                var bstInstructions = new HashSet<Instruction>();
                AnalyzeDispatcherTree(method, root, opcodeVar, mappings, bstInstructions, new HashSet<Instruction>());

                if (mappings.Count > 1)
                {
                    ReconstructDispatcher(method, head, root, opcodeVar, mappings, bstInstructions);
                    modified = true;
                }
            }

            return modified;
        }

        private bool TryAnalyzeWithoutLoop(MethodDef method)
        {
            var instrs = method.Body.Instructions;
            Local opcodeVar = null;
            Instruction potentialHead = null;

            for (int i = 0; i < Math.Min(instrs.Count, 300); i++)
            {
                if (IsOpcodeFetch(instrs, i, out int skip, out Local foundVar))
                {
                    if (IsUsedInComparison(instrs, i + skip, foundVar))
                    {
                        opcodeVar = foundVar;
                        potentialHead = instrs[i];
                        break;
                    }
                }
            }

            if (opcodeVar == null)
                return false;

            var root = FindDispatcherRoot(method, potentialHead, opcodeVar);
            if (root == null)
                return false;

            Instruction loopHead = instrs.FirstOrDefault(i => i.Operand is Instruction t && instrs.IndexOf(t) < instrs.IndexOf(i));
            if (loopHead == null)
                loopHead = potentialHead;

            var mappings = new Dictionary<int, Instruction>();
            var bstInstructions = new HashSet<Instruction>();
            AnalyzeDispatcherTree(method, root, opcodeVar, mappings, bstInstructions, new HashSet<Instruction>());

            if (mappings.Count > 1)
            {
                ReconstructDispatcher(method, loopHead, root, opcodeVar, mappings, bstInstructions);
                return true;
            }
            return false;
        }

        private bool IsOpcodeFetch(IList<Instruction> instrs, int i, out int skip, out Local variable)
        {
            skip = 0;
            variable = null;
            if (i >= instrs.Count)
                return false;

            if (instrs[i].OpCode == OpCodes.Ldind_I1 || instrs[i].OpCode == OpCodes.Ldind_U1 || instrs[i].OpCode == OpCodes.Ldind_I4 || instrs[i].OpCode == OpCodes.Ldind_U4)
            {
                int next = i + 1;
                while (next < i + 5 && next < instrs.Count)
                {
                    if (Fish.Shared.Utils.IsStoreLocal(instrs[next]))
                    {
                        variable = instrs[next].Operand as Local;
                        if (variable == null)
                            return false;
                        skip = next - i + 1;
                        return true;
                    }
                    if (instrs[next].OpCode == OpCodes.Xor || instrs[next].OpCode == OpCodes.Add || instrs[next].OpCode == OpCodes.Sub)
                    {
                        next++;
                        continue;
                    }
                    if (Fish.Shared.Utils.IsIntegerConstant(instrs[next]))
                    {
                        next++;
                        continue;
                    }
                    break;
                }
            }
            return false;
        }

        private HashSet<Instruction> FindLoopHeaders(MethodDef method)
        {
            var headers = new HashSet<Instruction>();
            var instrs = method.Body.Instructions;
            for (int i = 0; i < instrs.Count; i++)
            {
                if (instrs[i].Operand is Instruction target && instrs.Contains(target) && instrs.IndexOf(target) < i)
                    headers.Add(target);
            }
            return headers;
        }

        private Local IdentifyOpcodeVariable(MethodDef method, Instruction head)
        {
            var instrs = method.Body.Instructions;
            int startIndex = instrs.IndexOf(head);
            for (int i = startIndex; i < startIndex + 80 && i < instrs.Count; i++)
            {
                if (IsOpcodeFetch(instrs, i, out int skip, out Local found))
                {
                    if (IsUsedInComparison(instrs, i + skip, found))
                        return found;
                }
            }
            return null;
        }

        private bool IsUsedInComparison(IList<Instruction> instrs, int startIndex, Local candidate)
        {
            for (int i = startIndex; i < startIndex + 50 && i < instrs.Count; i++)
            {
                if (IsOpcodeCheck(instrs, i, candidate, out _))
                    return true;
            }
            return false;
        }

        private Instruction FindDispatcherRoot(MethodDef method, Instruction head, Local opcodeVar)
        {
            var instrs = method.Body.Instructions;
            int startIndex = instrs.IndexOf(head);
            for (int i = startIndex; i < startIndex + 150 && i < instrs.Count; i++)
            {
                if (IsOpcodeCheck(instrs, i, opcodeVar, out _))
                    return instrs[i];
            }
            return null;
        }

        private bool IsOpcodeCheck(IList<Instruction> instrs, int index, Local opcodeVar, out int consumed)
        {
            consumed = 0;
            if (index + 2 >= instrs.Count)
                return false;

            if (IsStandardPivot(instrs, index, opcodeVar, out consumed))
                return true;
            if (IsCeqPivot(instrs, index, opcodeVar, out consumed))
                return true;

            return false;
        }

        private bool IsStandardPivot(IList<Instruction> instrs, int index, Local opcodeVar, out int consumed)
        {
            consumed = 0;
            Instruction a = instrs[index];
            Instruction b = instrs[index + 1];
            Instruction branch = instrs[index + 2];

            if (!IsConditionalBranch(branch.OpCode))
                return false;

            bool hasOpcode = IsLoadOf(a, opcodeVar) || IsLoadOf(b, opcodeVar);
            bool hasConst = Fish.Shared.Utils.IsIntegerConstant(a) || Fish.Shared.Utils.IsIntegerConstant(b);

            if (hasOpcode && hasConst)
            {
                consumed = 3;
                return true;
            }
            return false;
        }

        private bool IsCeqPivot(IList<Instruction> instrs, int index, Local opcodeVar, out int consumed)
        {
            consumed = 0;
            if (index + 3 >= instrs.Count)
                return false;

            Instruction a = instrs[index];
            Instruction b = instrs[index + 1];
            Instruction ceq = instrs[index + 2];
            Instruction branch = instrs[index + 3];

            if (ceq.OpCode != OpCodes.Ceq)
                return false;
            if (branch.OpCode != OpCodes.Brtrue && branch.OpCode != OpCodes.Brtrue_S && branch.OpCode != OpCodes.Brfalse && branch.OpCode != OpCodes.Brfalse_S)
                return false;

            bool hasOpcode = IsLoadOf(a, opcodeVar) || IsLoadOf(b, opcodeVar);
            bool hasConst = Fish.Shared.Utils.IsIntegerConstant(a) || Fish.Shared.Utils.IsIntegerConstant(b);

            if (hasOpcode && hasConst)
            {
                consumed = 4;
                return true;
            }
            return false;
        }

        private bool IsLoadOf(Instruction inst, Local var)
        {
            if (var == null || !Fish.Shared.Utils.IsLoadLocal(inst))
                return false;
            var l = inst.Operand as Local;
            return l != null && l.Index == var.Index;
        }

        private bool IsConditionalBranch(OpCode op)
        {
            return op == OpCodes.Beq
                || op == OpCodes.Beq_S
                || op == OpCodes.Bne_Un
                || op == OpCodes.Bne_Un_S
                || op == OpCodes.Bge
                || op == OpCodes.Bge_S
                || op == OpCodes.Bgt
                || op == OpCodes.Bgt_S
                || op == OpCodes.Ble
                || op == OpCodes.Ble_S
                || op == OpCodes.Blt
                || op == OpCodes.Blt_S
                || op == OpCodes.Brtrue
                || op == OpCodes.Brtrue_S
                || op == OpCodes.Brfalse
                || op == OpCodes.Brfalse_S;
        }

        private void AnalyzeDispatcherTree(MethodDef method, Instruction instr, Local opcodeVar, Dictionary<int, Instruction> maps, HashSet<Instruction> bstSet, HashSet<Instruction> visited)
        {
            if (instr == null || !visited.Add(instr))
                return;
            var instrs = method.Body.Instructions;
            int index = instrs.IndexOf(instr);
            if (index == -1)
                return;

            if (CheckEqualityPivot(instrs, index, opcodeVar, out int val, out Instruction low, out Instruction high, out Instruction equal))
            {
                for (int i = 0; i < 6; i++)
                    bstSet.Add(instrs[index + i]);
                if (index + 6 < instrs.Count && instrs[index + 6].OpCode == OpCodes.Br)
                    bstSet.Add(instrs[index + 6]);

                maps[val] = equal;
                AnalyzeDispatcherTree(method, low, opcodeVar, maps, bstSet, visited);
                AnalyzeDispatcherTree(method, high, opcodeVar, maps, bstSet, visited);
                return;
            }

            if (CheckRangePattern(instrs, index, opcodeVar, out Instruction next))
            {
                for (int i = 0; i < 6; i++)
                    bstSet.Add(instrs[index + i]);
                AnalyzeDispatcherTree(method, next, opcodeVar, maps, bstSet, visited);
                return;
            }

            if (CheckMatchPattern(instrs, index, opcodeVar, out int mv, out Instruction target))
            {
                IsOpcodeCheck(instrs, index, opcodeVar, out int consumed);
                for (int i = 0; i < consumed; i++)
                    bstSet.Add(instrs[index + i]);
                maps[mv] = target;
                if (index + consumed < instrs.Count)
                    AnalyzeDispatcherTree(method, instrs[index + consumed], opcodeVar, maps, bstSet, visited);
                return;
            }

            if (instr.OpCode == OpCodes.Br && instr.Operand is Instruction branchTarget)
            {
                bstSet.Add(instr);
                AnalyzeDispatcherTree(method, branchTarget, opcodeVar, maps, bstSet, visited);
            }
        }

        private bool CheckEqualityPivot(IList<Instruction> instrs, int index, Local opcodeVar, out int value, out Instruction low, out Instruction high, out Instruction equal)
        {
            value = 0;
            low = null;
            high = null;
            equal = null;
            if (index + 6 >= instrs.Count)
                return false;

            if (IsOpcodeCheck(instrs, index, opcodeVar, out int c1) && IsOpcodeCheck(instrs, index + c1, opcodeVar, out int c2))
            {
                int v1 = GetCheckValue(instrs[index], instrs[index + 1]);
                int v2 = GetCheckValue(instrs[index + c1], instrs[index + c1 + 1]);

                if (v1 != v2)
                    return false;
                value = v1;

                var op1 = instrs[index + c1 - 1].OpCode;
                var op2 = instrs[index + c1 + c2 - 1].OpCode;

                Instruction t1 = instrs[index + c1 - 1].Operand as Instruction;
                Instruction t2 = instrs[index + c1 + c2 - 1].Operand as Instruction;

                if (op1 == OpCodes.Blt || op1 == OpCodes.Blt_S)
                    high = t1;
                else if (op1 == OpCodes.Bgt || op1 == OpCodes.Bgt_S)
                    low = t1;
                else
                    return false;

                if (op2 == OpCodes.Blt || op2 == OpCodes.Blt_S)
                    high = t2;
                else if (op2 == OpCodes.Bgt || op2 == OpCodes.Bgt_S)
                    low = t2;
                else
                    return false;

                if (low == null || high == null)
                    return false;

                Instruction res = instrs[index + c1 + c2];
                if (res.OpCode == OpCodes.Br && res.Operand is Instruction tEqual)
                    equal = tEqual;
                else
                    equal = res;

                return true;
            }
            return false;
        }

        private int GetCheckValue(Instruction i1, Instruction i2)
        {
            if (Fish.Shared.Utils.IsIntegerConstant(i1))
                return Fish.Shared.Utils.GetConstantValue(i1);
            if (Fish.Shared.Utils.IsIntegerConstant(i2))
                return Fish.Shared.Utils.GetConstantValue(i2);
            return 0;
        }

        private bool CheckRangePattern(IList<Instruction> instrs, int index, Local opcodeVar, out Instruction next)
        {
            next = null;
            if (index + 5 >= instrs.Count)
                return false;

            if (IsOpcodeCheck(instrs, index, opcodeVar, out int c1) && IsOpcodeCheck(instrs, index + c1, opcodeVar, out int c2))
            {
                next = instrs[index + c1 + c2];
                return true;
            }
            return false;
        }

        private bool CheckMatchPattern(IList<Instruction> instrs, int index, Local opcodeVar, out int value, out Instruction target)
        {
            value = 0;
            target = null;
            if (!IsOpcodeCheck(instrs, index, opcodeVar, out int consumed))
                return false;

            Instruction branch = instrs[index + consumed - 1];
            bool isMatch = false;

            if (branch.OpCode == OpCodes.Beq || branch.OpCode == OpCodes.Beq_S)
                isMatch = true;
            else if (branch.OpCode == OpCodes.Brtrue || branch.OpCode == OpCodes.Brtrue_S)
            {
                Instruction prev = instrs[index + consumed - 2];
                if (prev.OpCode == OpCodes.Ceq)
                    isMatch = true;
            }

            if (!isMatch)
                return false;

            value = GetCheckValue(instrs[index], instrs[index + 1]);
            target = branch.Operand as Instruction;
            return target != null;
        }

        private void ReconstructDispatcher(MethodDef method, Instruction head, Instruction root, Local bVar, Dictionary<int, Instruction> maps, HashSet<Instruction> bstSet)
        {
            var instrs = method.Body.Instructions;

            foreach (var instr in bstSet)
            {
                instr.OpCode = OpCodes.Nop;
                instr.Operand = null;
            }

            int minOp = maps.Keys.Min();
            int maxOp = maps.Keys.Max();

            if (maxOp - minOp < 1000000)
            {
                var jumpTable = new Instruction[maxOp - minOp + 1];
                for (int i = 0; i < jumpTable.Length; i++)
                    jumpTable[i] = head;
                foreach (var entry in maps)
                    jumpTable[entry.Key - minOp] = entry.Value;

                int rootIndex = instrs.IndexOf(root);

                instrs[rootIndex].OpCode = OpCodes.Ldloc;
                instrs[rootIndex].Operand = bVar;

                instrs.Insert(rootIndex + 1, new Instruction(OpCodes.Ldc_I4, minOp));
                instrs.Insert(rootIndex + 2, new Instruction(OpCodes.Sub));
                instrs.Insert(rootIndex + 3, new Instruction(OpCodes.Switch, jumpTable));

                instrs.Insert(rootIndex + 4, new Instruction(OpCodes.Br, head));
            }
        }
    }
}
