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
                        Logger.Info(string.Format("[LocalCleaner] Error in {0}: {1}", method.FullName, ex.Message));
                    }
                }
            }

            Logger.Info(string.Format("[LocalCleaner] Removed {0} locals, resolved {1} aliases in {2} methods", totalRemovedLocals, totalResolvedAliases, processedMethods));
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

            var copyMap = new Dictionary<Local, Local>();

            for (int i = 0; i < instructions.Count - 1; i++)
            {
                var load = instructions[i];
                var store = instructions[i + 1];

                if (!IsLoadLocal(load) || !IsStoreLocal(store))
                    continue;

                var sourceLocal = GetLocal(load, locals);
                var destLocal = GetLocal(store, locals);

                if (sourceLocal == null || destLocal == null || sourceLocal == destLocal)
                    continue;

                if (!AreTypesCompatible(sourceLocal.Type, destLocal.Type))
                    continue;

                if (IsStoredToViaAddress(instructions, destLocal, locals))
                    continue;

                if (CountStores(instructions, destLocal, locals) == 1)
                {
                    copyMap[destLocal] = sourceLocal;
                }
            }

            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var dest in copyMap.Keys.ToList())
                {
                    var source = copyMap[dest];
                    if (copyMap.TryGetValue(source, out var ultimateSource))
                    {
                        copyMap[dest] = ultimateSource;
                        changed = true;
                    }
                }
            }

            foreach (var kvp in copyMap)
            {
                var destLocal = kvp.Key;
                var sourceLocal = kvp.Value;

                for (int i = 0; i < instructions.Count; i++)
                {
                    if (IsLoadLocal(instructions[i]) && GetLocal(instructions[i], locals) == destLocal)
                    {
                        instructions[i].OpCode = GetLoadOpcode(sourceLocal.Index);
                        instructions[i].Operand = sourceLocal.Index > 3 ? sourceLocal : null;
                        modified = true;
                    }
                }
            }

            for (int i = 0; i < instructions.Count - 1; i++)
            {
                var load = instructions[i];
                var store = instructions[i + 1];

                if (!IsLoadLocal(load) || !IsStoreLocal(store))
                    continue;

                var sourceLocal = GetLocal(load, locals);
                var destLocal = GetLocal(store, locals);

                if (sourceLocal == null || destLocal == null || sourceLocal == destLocal)
                    continue;

                if (copyMap.ContainsKey(destLocal))
                    continue;

                if (IsStoredToViaAddress(instructions, destLocal, locals) || IsStoredToViaAddress(instructions, sourceLocal, locals))
                    continue;

                for (int j = i + 2; j < instructions.Count; j++)
                {
                    var instr = instructions[j];

                    if (IsStoreLocal(instr) && GetLocal(instr, locals) == destLocal)
                        break;

                    if (IsStoreLocal(instr) && GetLocal(instr, locals) == sourceLocal)
                        break;

                    if (IsLoadLocal(instr) && GetLocal(instr, locals) == destLocal)
                    {
                        instr.OpCode = GetLoadOpcode(sourceLocal.Index);
                        instr.Operand = sourceLocal.Index > 3 ? sourceLocal : null;
                        modified = true;
                    }
                }
            }

            return modified;
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

        private int CountStores(IList<Instruction> instructions, Local local, IList<Local> locals)
        {
            int count = 0;
            foreach (var instr in instructions)
            {
                if (IsStoreLocal(instr) && GetLocal(instr, locals) == local)
                    count++;
            }
            return count;
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
            var locals = method.Body.Variables;
            bool modified = false;

            for (int i = instructions.Count - 1; i >= 1; i--)
            {
                if (instructions[i].OpCode == OpCodes.Pop)
                {
                    var prev = instructions[i - 1];
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
                        var readLocal = GetLocal(instr, locals);
                        if (readLocal == local)
                        {
                            isRead = true;
                            break;
                        }
                    }

                    if (IsStoreLocal(instr))
                    {
                        var storeLocal = GetLocal(instr, locals);
                        if (storeLocal == local && !isRead)
                        {
                            if (!IsTarget(method, instructions[i]))
                            {
                                instructions[i].OpCode = OpCodes.Pop;
                                instructions[i].Operand = null;
                                modified = true;
                            }
                            break;
                        }
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
                        if (IsByRefType(aliasLocal.Type))
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

            for (int i = 0; i < instructions.Count - 1; i++)
            {
                if (IsLoadLocalAddress(instructions[i]) && IsStoreLocal(instructions[i + 1]))
                {
                    var aliasLocal = GetLocal(instructions[i + 1], locals);
                    if (aliasLocal != null && resolvedAliases.Contains(aliasLocal))
                    {
                        if (!IsLocalUsedAfter(instructions, i + 2, aliasLocal, locals))
                        {
                            instructions[i].OpCode = OpCodes.Nop;
                            instructions[i].Operand = null;
                            instructions[i + 1].OpCode = OpCodes.Nop;
                            instructions[i + 1].Operand = null;
                        }
                    }
                }
            }

            return resolved;
        }

        private bool IsLocalUsedAfter(IList<Instruction> instrs, int startIndex, Local local, IList<Local> locals)
        {
            for (int i = startIndex; i < instrs.Count; i++)
            {
                var l = GetLocal(instrs[i], locals);
                if (l == local)
                    return true;
            }
            return false;
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

        private bool IsLoadLocalAddress(Instruction instr)
        {
            return instr.OpCode == OpCodes.Ldloca || instr.OpCode == OpCodes.Ldloca_S;
        }

        private bool IsTarget(MethodDef method, Instruction instr)
        {
            if (method.Body.HasExceptionHandlers)
            {
                foreach (var eh in method.Body.ExceptionHandlers)
                {
                    if (eh.TryStart == instr || eh.TryEnd == instr || eh.HandlerStart == instr || eh.HandlerEnd == instr || eh.FilterStart == instr)
                        return true;
                }
            }

            foreach (var i in method.Body.Instructions)
            {
                if (i.Operand is Instruction target && target == instr)
                    return true;
                if (i.Operand is Instruction[] targets)
                {
                    foreach (var t in targets)
                        if (t == instr)
                            return true;
                }
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

        private bool IsIndirectStore(Instruction instr)
        {
            return instr.OpCode == OpCodes.Stind_I
                || instr.OpCode == OpCodes.Stind_I1
                || instr.OpCode == OpCodes.Stind_I2
                || instr.OpCode == OpCodes.Stind_I4
                || instr.OpCode == OpCodes.Stind_I8
                || instr.OpCode == OpCodes.Stind_R4
                || instr.OpCode == OpCodes.Stind_R8
                || instr.OpCode == OpCodes.Stind_Ref;
        }

        private bool IsIndirectLoad(Instruction instr)
        {
            return instr.OpCode == OpCodes.Ldind_I
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
        }

        private bool IsBranch(Instruction instr)
        {
            return instr.OpCode.FlowControl == FlowControl.Branch || instr.OpCode.FlowControl == FlowControl.Cond_Branch;
        }

        private bool IsByRefType(TypeSig type)
        {
            return type.IsByRef;
        }

        private bool IsLoadLocal(Instruction instr)
        {
            if (instr == null)
                return false;
            return instr.OpCode == OpCodes.Ldloc || instr.OpCode == OpCodes.Ldloc_0 || instr.OpCode == OpCodes.Ldloc_1 || instr.OpCode == OpCodes.Ldloc_2 || instr.OpCode == OpCodes.Ldloc_3 || instr.OpCode == OpCodes.Ldloc_S;
        }

        private bool IsStoreLocal(Instruction instr)
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
    }
}
