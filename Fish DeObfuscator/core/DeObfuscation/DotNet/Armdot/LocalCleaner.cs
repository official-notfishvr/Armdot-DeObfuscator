using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Fish.Shared;

namespace Fish_DeObfuscator.core.DeObfuscation.DotNet.Armdot
{
    public class LocalCleaner : IStage
    {
        private int totalRemovedLocals = 0;
        private int totalResolvedAliases = 0;
        private int processedMethods = 0;

        public static int Clean(MethodDef method)
        {
            if (!method.HasBody || method.Body.Variables.Count == 0)
                return 0;
            return new LocalCleaner().CleanupMethod(method);
        }

        public void Execute(IContext context)
        {
            var module = context.ModuleDefinition;

            foreach (var type in module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody || method.Body.Variables.Count == 0)
                        continue;

                    try
                    {
                        int cleaned = CleanupMethod(method);
                        if (cleaned > 0)
                        {
                            totalRemovedLocals += cleaned;
                            processedMethods++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Info($"    Error in {method.FullName}: {ex.Message}");
                    }
                }
            }

            Logger.Info($"    Removed {totalRemovedLocals} locals, resolved {totalResolvedAliases} aliases in {processedMethods} methods");
        }

        private int CleanupMethod(MethodDef method)
        {
            int originalCount = method.Body.Variables.Count;
            bool modified = true;
            int iterations = 0;
            const int maxIterations = 20;

            while (modified && iterations++ < maxIterations)
            {
                modified = false;

                var aliasMap = BuildAliasMap(method);
                if (aliasMap.Count > 0)
                {
                    int resolved = ResolveByrefAliases(method, aliasMap);
                    if (resolved > 0)
                    {
                        totalResolvedAliases += resolved;
                        modified = true;
                    }
                }

                modified |= CoalesceCopyLocals(method);

                var usedLocals = FindUsedLocals(method);
                modified |= RemoveUnusedLocals(method, usedLocals);

                modified |= RemoveDeadStores(method);
                modified |= RemoveNopSequences(method);
            }

            MergeLocalsWithNonOverlappingLiveRanges(method);
            CompactLocals(method);

            return originalCount - method.Body.Variables.Count;
        }

        private bool CoalesceCopyLocals(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            var locals = method.Body.Variables;
            bool modified = false;
            bool globalModified = true;

            while (globalModified)
            {
                globalModified = false;
                for (int i = 0; i < instructions.Count - 1; i++)
                {
                    if (IsLoadLocal(instructions[i]) && IsStoreLocal(instructions[i + 1]))
                    {
                        var sourceLocal = GetLocal(instructions[i], locals);
                        var destLocal = GetLocal(instructions[i + 1], locals);

                        if (sourceLocal != null && destLocal != null && sourceLocal != destLocal)
                        {
                            if (AreTypesCompatible(sourceLocal.Type, destLocal.Type) && CanCoalesce(method, sourceLocal, destLocal, i + 1))
                            {
                                ReplaceLocalUsage(method, destLocal, sourceLocal);
                                instructions[i].OpCode = OpCodes.Nop;
                                instructions[i + 1].OpCode = OpCodes.Nop;
                                modified = true;
                                globalModified = true;
                            }
                        }
                    }
                }
            }
            return modified;
        }

        private bool CanCoalesce(MethodDef method, Local source, Local dest, int storeIndex)
        {
            var instructions = method.Body.Instructions;
            for (int i = storeIndex + 1; i < instructions.Count; i++)
            {
                if (IsStoreLocal(instructions[i]) && GetLocal(instructions[i], method.Body.Variables) == source)
                    return false;
                if (IsAddressTaken(instructions[i], dest) || IsAddressTaken(instructions[i], source))
                    return false;
            }
            return true;
        }

        private bool IsAddressTaken(Instruction instr, Local local)
        {
            return (instr.OpCode == OpCodes.Ldloca || instr.OpCode == OpCodes.Ldloca_S) && (instr.Operand as Local == local);
        }

        private void ReplaceLocalUsage(MethodDef method, Local oldLocal, Local newLocal)
        {
            foreach (var instr in method.Body.Instructions)
            {
                if (instr.Operand == oldLocal)
                    instr.Operand = newLocal;
                else if (instr.OpCode == OpCodes.Ldloc_0 && oldLocal.Index == 0)
                {
                    instr.OpCode = OpCodes.Ldloc;
                    instr.Operand = newLocal;
                }
                else if (instr.OpCode == OpCodes.Ldloc_1 && oldLocal.Index == 1)
                {
                    instr.OpCode = OpCodes.Ldloc;
                    instr.Operand = newLocal;
                }
                else if (instr.OpCode == OpCodes.Ldloc_2 && oldLocal.Index == 2)
                {
                    instr.OpCode = OpCodes.Ldloc;
                    instr.Operand = newLocal;
                }
                else if (instr.OpCode == OpCodes.Ldloc_3 && oldLocal.Index == 3)
                {
                    instr.OpCode = OpCodes.Ldloc;
                    instr.Operand = newLocal;
                }
                else if (instr.OpCode == OpCodes.Stloc_0 && oldLocal.Index == 0)
                {
                    instr.OpCode = OpCodes.Stloc;
                    instr.Operand = newLocal;
                }
                else if (instr.OpCode == OpCodes.Stloc_1 && oldLocal.Index == 1)
                {
                    instr.OpCode = OpCodes.Stloc;
                    instr.Operand = newLocal;
                }
                else if (instr.OpCode == OpCodes.Stloc_2 && oldLocal.Index == 2)
                {
                    instr.OpCode = OpCodes.Stloc;
                    instr.Operand = newLocal;
                }
                else if (instr.OpCode == OpCodes.Stloc_3 && oldLocal.Index == 3)
                {
                    instr.OpCode = OpCodes.Stloc;
                    instr.Operand = newLocal;
                }
            }
        }

        private void MergeLocalsWithNonOverlappingLiveRanges(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            var locals = method.Body.Variables;

            if (locals.Count <= 1)
                return;

            var liveRanges = ComputeLiveRanges(method);

            var safeLocals = new HashSet<Local>();
            foreach (var local in locals)
            {
                if (!IsStoredToViaAddress(instructions, local, locals))
                    safeLocals.Add(local);
            }

            var mergeMap = new Dictionary<Local, Local>();
            var sortedLocals = safeLocals.Where(l => liveRanges.ContainsKey(l)).OrderBy(l => liveRanges[l].start).ToList();

            for (int i = 0; i < sortedLocals.Count; i++)
            {
                var localA = sortedLocals[i];
                if (mergeMap.ContainsKey(localA))
                    continue;

                var rangeA = liveRanges[localA];

                for (int j = i + 1; j < sortedLocals.Count; j++)
                {
                    var localB = sortedLocals[j];
                    if (mergeMap.ContainsKey(localB))
                        continue;

                    if (!AreTypesCompatible(localA.Type, localB.Type))
                        continue;

                    var rangeB = liveRanges[localB];

                    if (rangeA.end < rangeB.start || rangeB.end < rangeA.start)
                    {
                        mergeMap[localB] = localA;
                        liveRanges[localA] = (Math.Min(rangeA.start, rangeB.start), Math.Max(rangeA.end, rangeB.end));
                        rangeA = liveRanges[localA];
                    }
                }
            }

            if (mergeMap.Count > 0)
            {
                foreach (var instr in instructions)
                {
                    var local = GetLocal(instr, locals);
                    if (local != null && mergeMap.TryGetValue(local, out var targetLocal))
                    {
                        if (IsLoadLocal(instr))
                        {
                            instr.OpCode = GetLoadOpcode(targetLocal.Index);
                            instr.Operand = targetLocal.Index > 3 ? targetLocal : null;
                        }
                        else if (IsStoreLocal(instr))
                        {
                            instr.OpCode = GetStoreOpcode(targetLocal.Index);
                            instr.Operand = targetLocal.Index > 3 ? targetLocal : null;
                        }
                    }
                }
            }
        }

        private Dictionary<Local, (int start, int end)> ComputeLiveRanges(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            var locals = method.Body.Variables;
            var ranges = new Dictionary<Local, (int start, int end)>();

            var firstWrite = new Dictionary<Local, int>();
            var lastRead = new Dictionary<Local, int>();

            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                var local = GetLocal(instr, locals);
                if (local == null)
                    continue;

                if (IsStoreLocal(instr))
                {
                    if (!firstWrite.ContainsKey(local))
                        firstWrite[local] = i;
                }
                else if (IsLoadLocal(instr))
                {
                    lastRead[local] = i;
                }
            }

            foreach (var local in locals)
            {
                if (firstWrite.TryGetValue(local, out var start))
                {
                    int end = lastRead.TryGetValue(local, out var readEnd) ? readEnd : start;
                    ranges[local] = (start, end);
                }
            }

            return ranges;
        }

        private bool IsStoredToViaAddress(IList<Instruction> instructions, Local local, IList<Local> locals)
        {
            foreach (var instr in instructions)
            {
                if (IsLoadLocalAddress(instr) && GetLocal(instr, locals) == local)
                    return true;
            }
            return false;
        }

        private bool AreTypesCompatible(TypeSig a, TypeSig b)
        {
            if (a == null || b == null)
                return false;
            return a.FullName == b.FullName || (a.ElementType == b.ElementType && a.IsPrimitive && b.IsPrimitive);
        }

        private HashSet<Local> FindUsedLocals(MethodDef method)
        {
            var used = new HashSet<Local>();
            var instructions = method.Body.Instructions;
            var locals = method.Body.Variables;

            foreach (var instr in instructions)
            {
                if (IsLoadLocal(instr) || IsLoadLocalAddress(instr))
                {
                    var local = GetLocal(instr, locals);
                    if (local != null)
                        used.Add(local);
                }
            }

            return used;
        }

        private bool RemoveUnusedLocals(MethodDef method, HashSet<Local> usedLocals)
        {
            var instructions = method.Body.Instructions;
            var locals = method.Body.Variables;
            bool modified = false;

            for (int i = instructions.Count - 1; i >= 0; i--)
            {
                var instr = instructions[i];
                if (IsStoreLocal(instr))
                {
                    var local = GetLocal(instr, locals);
                    if (local != null && !usedLocals.Contains(local))
                    {
                        instr.OpCode = OpCodes.Pop;
                        instr.Operand = null;
                        modified = true;
                    }
                }
            }

            return modified;
        }

        private bool RemoveDeadStores(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            bool modified = false;

            for (int i = instructions.Count - 1; i >= 1; i--)
            {
                if (instructions[i].OpCode == OpCodes.Pop)
                {
                    var prev = instructions[i - 1];
                    if (prev.OpCode == OpCodes.Ldstr)
                        continue;

                    if (IsSinglePush(prev) && !IsTarget(method, prev))
                    {
                        instructions.RemoveAt(i);
                        instructions.RemoveAt(i - 1);
                        modified = true;
                        i--;
                    }
                }
            }

            modified |= RemoveConsecutiveDeadStores(method);
            return modified;
        }

        private bool RemoveConsecutiveDeadStores(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            var locals = method.Body.Variables;
            bool modified = false;

            for (int i = 0; i < instructions.Count; i++)
            {
                if (!IsStoreLocal(instructions[i]))
                    continue;

                var local = GetLocal(instructions[i], locals);
                if (local == null)
                    continue;

                bool isRead = false;
                for (int j = i + 1; j < instructions.Count; j++)
                {
                    var instr = instructions[j];
                    if (IsLoadLocal(instr) || IsLoadLocalAddress(instr))
                    {
                        if (GetLocal(instr, locals) == local)
                        {
                            isRead = true;
                            break;
                        }
                    }
                    if (IsStoreLocal(instr) && GetLocal(instr, locals) == local && !isRead)
                    {
                        if (!IsTarget(method, instructions[i]))
                        {
                            instructions[i].OpCode = OpCodes.Pop;
                            instructions[i].Operand = null;
                            modified = true;
                        }
                        break;
                    }
                    if (IsBranch(instr) || instr.OpCode == OpCodes.Ret)
                        break;
                }
            }
            return modified;
        }

        private bool RemoveNopSequences(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            bool modified = false;

            for (int i = instructions.Count - 1; i >= 0; i--)
            {
                if (instructions[i].OpCode == OpCodes.Nop && !IsTarget(method, instructions[i]))
                {
                    instructions.RemoveAt(i);
                    modified = true;
                }
            }

            return modified;
        }

        private Dictionary<Local, Local> BuildAliasMap(MethodDef method)
        {
            var aliasMap = new Dictionary<Local, Local>();
            var instructions = method.Body.Instructions;
            var locals = method.Body.Variables;

            for (int i = 0; i < instructions.Count - 1; i++)
            {
                var instr = instructions[i];
                var next = instructions[i + 1];

                if (IsLoadLocalAddress(instr) && IsStoreLocal(next))
                {
                    var targetLocal = GetLocal(instr, locals);
                    var aliasLocal = GetLocal(next, locals);

                    if (targetLocal != null && aliasLocal != null && targetLocal != aliasLocal)
                    {
                        if (aliasLocal.Type.IsByRef)
                        {
                            if (!aliasMap.ContainsKey(aliasLocal))
                                aliasMap[aliasLocal] = targetLocal;
                        }
                    }
                }
            }

            return aliasMap;
        }

        private int ResolveByrefAliases(MethodDef method, Dictionary<Local, Local> aliasMap)
        {
            var instructions = method.Body.Instructions;
            var locals = method.Body.Variables;
            int resolved = 0;
            var resolvedAliases = new HashSet<Local>();

            for (int i = 0; i < instructions.Count - 2; i++)
            {
                if (!IsLoadLocal(instructions[i]))
                    continue;
                var aliasLocal = GetLocal(instructions[i], locals);
                if (aliasLocal == null || !aliasMap.TryGetValue(aliasLocal, out var targetLocal))
                    continue;

                var pushInstr = instructions[i + 1];
                var stindInstr = instructions[i + 2];

                if (IsSinglePush(pushInstr) && IsIndirectStore(stindInstr))
                {
                    instructions[i].OpCode = OpCodes.Nop;
                    instructions[i].Operand = null;
                    instructions[i + 2].OpCode = GetStoreOpcode(targetLocal.Index);
                    instructions[i + 2].Operand = targetLocal.Index > 3 ? targetLocal : null;
                    resolvedAliases.Add(aliasLocal);
                    resolved++;
                }
            }

            for (int i = 0; i < instructions.Count - 1; i++)
            {
                if (!IsLoadLocal(instructions[i]))
                    continue;
                var aliasLocal = GetLocal(instructions[i], locals);
                if (aliasLocal == null || !aliasMap.TryGetValue(aliasLocal, out var targetLocal))
                    continue;

                if (IsIndirectLoad(instructions[i + 1]))
                {
                    instructions[i].OpCode = GetLoadOpcode(targetLocal.Index);
                    instructions[i].Operand = targetLocal.Index > 3 ? targetLocal : null;
                    instructions[i + 1].OpCode = OpCodes.Nop;
                    instructions[i + 1].Operand = null;
                    resolvedAliases.Add(aliasLocal);
                    resolved++;
                }
            }
            return resolved;
        }

        private void CompactLocals(MethodDef method)
        {
            var instructions = method.Body.Instructions;
            var locals = method.Body.Variables;
            var usedLocals = new HashSet<Local>();

            foreach (var instr in instructions)
            {
                var local = GetLocal(instr, locals);
                if (local != null)
                    usedLocals.Add(local);
            }

            var oldToNew = new Dictionary<Local, Local>();
            var newLocals = new List<Local>();

            foreach (var local in locals)
            {
                if (usedLocals.Contains(local))
                {
                    var newLocal = new Local(local.Type, local.Name);
                    oldToNew[local] = newLocal;
                    newLocals.Add(newLocal);
                }
            }

            foreach (var instr in instructions)
            {
                var oldLocal = GetLocal(instr, locals);
                if (oldLocal != null && oldToNew.TryGetValue(oldLocal, out var newLocal))
                {
                    UpdateLocalReference(instr, newLocal, newLocals.IndexOf(newLocal));
                }
            }

            locals.Clear();
            foreach (var local in newLocals)
                locals.Add(local);
        }

        private void UpdateLocalReference(Instruction instr, Local newLocal, int newIndex)
        {
            if (IsLoadLocal(instr))
            {
                instr.OpCode = GetLoadOpcode(newIndex);
                instr.Operand = newIndex > 3 ? newLocal : null;
            }
            else if (IsStoreLocal(instr))
            {
                instr.OpCode = GetStoreOpcode(newIndex);
                instr.Operand = newIndex > 3 ? newLocal : null;
            }
        }

        private OpCode GetLoadOpcode(int index)
        {
            switch (index)
            {
                case 0:
                    return OpCodes.Ldloc_0;
                case 1:
                    return OpCodes.Ldloc_1;
                case 2:
                    return OpCodes.Ldloc_2;
                case 3:
                    return OpCodes.Ldloc_3;
                default:
                    return index <= 255 ? OpCodes.Ldloc_S : OpCodes.Ldloc;
            }
        }

        private OpCode GetStoreOpcode(int index)
        {
            switch (index)
            {
                case 0:
                    return OpCodes.Stloc_0;
                case 1:
                    return OpCodes.Stloc_1;
                case 2:
                    return OpCodes.Stloc_2;
                case 3:
                    return OpCodes.Stloc_3;
                default:
                    return index <= 255 ? OpCodes.Stloc_S : OpCodes.Stloc;
            }
        }

        private bool IsLoadLocalAddress(Instruction instr) => instr.OpCode == OpCodes.Ldloca || instr.OpCode == OpCodes.Ldloca_S;

        private bool IsTarget(MethodDef method, Instruction instr)
        {
            if (method.Body.HasExceptionHandlers)
            {
                foreach (var eh in method.Body.ExceptionHandlers)
                    if (eh.TryStart == instr || eh.TryEnd == instr || eh.HandlerStart == instr || eh.HandlerEnd == instr || eh.FilterStart == instr)
                        return true;
            }
            foreach (var i in method.Body.Instructions)
            {
                if (i.Operand is Instruction target && target == instr)
                    return true;
                if (i.Operand is Instruction[] targets && targets.Contains(instr))
                    return true;
            }
            return false;
        }

        private bool IsSinglePush(Instruction instr)
        {
            var code = instr.OpCode.Code;
            if (code == Code.Ldc_I4 || code == Code.Ldc_I4_S || code == Code.Ldc_I4_M1 || (code >= Code.Ldc_I4_0 && code <= Code.Ldc_I4_8) || code == Code.Ldc_I8 || code == Code.Ldc_R4 || code == Code.Ldc_R8 || code == Code.Ldnull || code == Code.Ldstr)
                return true;
            if (IsLoadLocal(instr) || IsLoadLocalAddress(instr))
                return true;
            if (code == Code.Ldarg || code == Code.Ldarg_S || code == Code.Ldarg_0 || code == Code.Ldarg_1 || code == Code.Ldarg_2 || code == Code.Ldarg_3 || code == Code.Ldarga || code == Code.Ldarga_S)
                return true;
            return false;
        }

        private bool IsIndirectStore(Instruction instr) =>
            instr.OpCode == OpCodes.Stind_I || instr.OpCode == OpCodes.Stind_I1 || instr.OpCode == OpCodes.Stind_I2 || instr.OpCode == OpCodes.Stind_I4 || instr.OpCode == OpCodes.Stind_I8 || instr.OpCode == OpCodes.Stind_R4 || instr.OpCode == OpCodes.Stind_R8 || instr.OpCode == OpCodes.Stind_Ref;

        private bool IsIndirectLoad(Instruction instr) =>
            instr.OpCode == OpCodes.Ldind_I
            || instr.OpCode == OpCodes.Ldind_I1
            || instr.OpCode == OpCodes.Ldind_I2
            || instr.OpCode == OpCodes.Ldind_I4
            || instr.OpCode == OpCodes.Ldind_I8
            || instr.OpCode == OpCodes.Ldind_R4
            || instr.OpCode == OpCodes.Ldind_R8
            || instr.OpCode == OpCodes.Ldind_Ref
            || instr.OpCode == OpCodes.Ldind_U1
            || instr.OpCode == OpCodes.Ldind_U2
            || instr.OpCode == OpCodes.Ldind_U4;

        private bool IsBranch(Instruction instr) => instr.OpCode.FlowControl == FlowControl.Branch || instr.OpCode.FlowControl == FlowControl.Cond_Branch;

        private bool IsLoadLocal(Instruction instr) => instr != null && (instr.OpCode == OpCodes.Ldloc || instr.OpCode == OpCodes.Ldloc_0 || instr.OpCode == OpCodes.Ldloc_1 || instr.OpCode == OpCodes.Ldloc_2 || instr.OpCode == OpCodes.Ldloc_3 || instr.OpCode == OpCodes.Ldloc_S);

        private bool IsStoreLocal(Instruction instr) => instr != null && (instr.OpCode == OpCodes.Stloc || instr.OpCode == OpCodes.Stloc_0 || instr.OpCode == OpCodes.Stloc_1 || instr.OpCode == OpCodes.Stloc_2 || instr.OpCode == OpCodes.Stloc_3 || instr.OpCode == OpCodes.Stloc_S);

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
    }
}
