using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Fish.Shared
{
    public static class MethodBodyFixer
    {
        public static int FixAllMethods(ModuleDefMD module)
        {
            int fixedCount = 0;
            foreach (var type in module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (method.HasBody && FixMethodBody(method))
                        fixedCount++;
                }
            }
            return fixedCount;
        }

        public static bool FixMethodBody(MethodDef method)
        {
            if (!method.HasBody)
                return false;

            var body = method.Body;
            var instructions = body.Instructions;
            bool modified = false;

            if (instructions.Count == 0)
                return false;

            var validInstructions = new HashSet<Instruction>(instructions);
            modified |= FixBranchTargets(instructions, validInstructions);
            modified |= FixSwitchTargets(instructions, validInstructions);
            modified |= FixExceptionHandlers(body, validInstructions);
            modified |= EnsureProperEnding(body);

            if (modified)
            {
                body.SimplifyBranches();
                body.OptimizeBranches();
            }
            return modified;
        }

        private static bool FixBranchTargets(IList<Instruction> instructions, HashSet<Instruction> validInstructions)
        {
            bool modified = false;
            for (int i = 0; i < instructions.Count; i++)
            {
                var instr = instructions[i];
                if (instr.OpCode.OperandType == OperandType.InlineBrTarget || instr.OpCode.OperandType == OperandType.ShortInlineBrTarget)
                {
                    if (instr.Operand is Instruction target && !validInstructions.Contains(target))
                    {
                        var replacement = i + 1 < instructions.Count ? instructions[i + 1] : instructions[instructions.Count - 1];
                        instr.Operand = replacement;
                        modified = true;
                    }
                    else if (instr.Operand == null && instr.OpCode.FlowControl == FlowControl.Branch)
                    {
                        instr.OpCode = OpCodes.Nop;
                        modified = true;
                    }
                }
            }
            return modified;
        }

        private static bool FixSwitchTargets(IList<Instruction> instructions, HashSet<Instruction> validInstructions)
        {
            bool modified = false;
            foreach (var instr in instructions)
            {
                if (instr.OpCode == OpCodes.Switch && instr.Operand is Instruction[] targets)
                {
                    var newTargets = new Instruction[targets.Length];
                    bool needsUpdate = false;

                    for (int i = 0; i < targets.Length; i++)
                    {
                        if (targets[i] != null && validInstructions.Contains(targets[i]))
                            newTargets[i] = targets[i];
                        else
                        {
                            int switchIdx = instructions.IndexOf(instr);
                            newTargets[i] = switchIdx + 1 < instructions.Count ? instructions[switchIdx + 1] : instructions[0];
                            needsUpdate = true;
                        }
                    }

                    if (needsUpdate)
                    {
                        instr.Operand = newTargets;
                        modified = true;
                    }
                }
            }
            return modified;
        }

        private static bool FixExceptionHandlers(CilBody body, HashSet<Instruction> validInstructions)
        {
            bool modified = false;
            var instructions = body.Instructions;
            var handlersToRemove = new List<ExceptionHandler>();

            foreach (var handler in body.ExceptionHandlers)
            {
                bool handlerValid = true;

                if (handler.TryStart != null && !validInstructions.Contains(handler.TryStart))
                {
                    handler.TryStart = instructions.Count > 0 ? instructions[0] : null;
                    modified = true;
                    if (handler.TryStart == null)
                        handlerValid = false;
                }

                if (handler.TryEnd != null && !validInstructions.Contains(handler.TryEnd))
                {
                    handler.TryEnd = instructions.Count > 0 ? instructions[instructions.Count - 1] : null;
                    modified = true;
                    if (handler.TryEnd == null)
                        handlerValid = false;
                }

                if (handler.HandlerStart != null && !validInstructions.Contains(handler.HandlerStart))
                {
                    handler.HandlerStart = handler.TryEnd ?? (instructions.Count > 0 ? instructions[0] : null);
                    modified = true;
                    if (handler.HandlerStart == null)
                        handlerValid = false;
                }

                if (handler.HandlerEnd != null && !validInstructions.Contains(handler.HandlerEnd))
                {
                    handler.HandlerEnd = instructions.Count > 0 ? instructions[instructions.Count - 1] : null;
                    modified = true;
                    if (handler.HandlerEnd == null)
                        handlerValid = false;
                }

                if (handler.FilterStart != null && !validInstructions.Contains(handler.FilterStart))
                {
                    handler.FilterStart = handler.TryEnd ?? (instructions.Count > 0 ? instructions[0] : null);
                    modified = true;
                    if (handler.FilterStart == null)
                        handlerValid = false;
                }

                if (handlerValid)
                {
                    int tryStartIdx = handler.TryStart != null ? instructions.IndexOf(handler.TryStart) : -1;
                    int tryEndIdx = handler.TryEnd != null ? instructions.IndexOf(handler.TryEnd) : -1;
                    int handlerStartIdx = handler.HandlerStart != null ? instructions.IndexOf(handler.HandlerStart) : -1;
                    int handlerEndIdx = handler.HandlerEnd != null ? instructions.IndexOf(handler.HandlerEnd) : -1;

                    if (tryStartIdx >= tryEndIdx || handlerStartIdx < tryEndIdx || (handlerEndIdx != -1 && handlerEndIdx <= handlerStartIdx))
                    {
                        handlersToRemove.Add(handler);
                        modified = true;
                    }
                }
                else
                {
                    handlersToRemove.Add(handler);
                    modified = true;
                }
            }

            foreach (var handler in handlersToRemove)
                body.ExceptionHandlers.Remove(handler);

            return modified;
        }

        private static bool EnsureProperEnding(CilBody body)
        {
            var instructions = body.Instructions;
            if (instructions.Count == 0)
            {
                instructions.Add(OpCodes.Ret.ToInstruction());
                return true;
            }

            var lastInstr = instructions[instructions.Count - 1];
            var flowControl = lastInstr.OpCode.FlowControl;

            if (flowControl != FlowControl.Return && flowControl != FlowControl.Throw && flowControl != FlowControl.Branch)
            {
                instructions.Add(OpCodes.Ret.ToInstruction());
                return true;
            }
            return false;
        }

        public static void UpdateInstructionReferences(CilBody body, Dictionary<Instruction, Instruction> oldToNew)
        {
            if (oldToNew == null || oldToNew.Count == 0)
                return;

            foreach (var instr in body.Instructions)
            {
                if (instr.Operand is Instruction target && oldToNew.TryGetValue(target, out var newTarget))
                    instr.Operand = newTarget;
                else if (instr.Operand is Instruction[] targets)
                    for (int i = 0; i < targets.Length; i++)
                        if (oldToNew.TryGetValue(targets[i], out var newT))
                            targets[i] = newT;
            }

            foreach (var handler in body.ExceptionHandlers)
            {
                if (handler.TryStart != null && oldToNew.TryGetValue(handler.TryStart, out var newTryStart))
                    handler.TryStart = newTryStart;
                if (handler.TryEnd != null && oldToNew.TryGetValue(handler.TryEnd, out var newTryEnd))
                    handler.TryEnd = newTryEnd;
                if (handler.HandlerStart != null && oldToNew.TryGetValue(handler.HandlerStart, out var newHandlerStart))
                    handler.HandlerStart = newHandlerStart;
                if (handler.HandlerEnd != null && oldToNew.TryGetValue(handler.HandlerEnd, out var newHandlerEnd))
                    handler.HandlerEnd = newHandlerEnd;
                if (handler.FilterStart != null && oldToNew.TryGetValue(handler.FilterStart, out var newFilterStart))
                    handler.FilterStart = newFilterStart;
            }
        }

        public static bool RemoveNopsPreservingTargets(MethodDef method)
        {
            if (!method.HasBody)
                return false;

            var body = method.Body;
            var instructions = body.Instructions;
            bool modified = false;

            var nopRedirects = new Dictionary<Instruction, Instruction>();
            for (int i = 0; i < instructions.Count; i++)
            {
                if (instructions[i].OpCode == OpCodes.Nop)
                {
                    Instruction nextNonNop = null;
                    for (int j = i + 1; j < instructions.Count; j++)
                    {
                        if (instructions[j].OpCode != OpCodes.Nop)
                        {
                            nextNonNop = instructions[j];
                            break;
                        }
                    }
                    if (nextNonNop != null)
                        nopRedirects[instructions[i]] = nextNonNop;
                }
            }

            if (nopRedirects.Count > 0)
                UpdateInstructionReferences(body, nopRedirects);

            for (int i = instructions.Count - 1; i >= 0; i--)
            {
                if (instructions[i].OpCode == OpCodes.Nop && !IsInstructionReferenced(body, instructions[i]))
                {
                    instructions.RemoveAt(i);
                    modified = true;
                }
            }
            return modified;
        }

        private static bool IsInstructionReferenced(CilBody body, Instruction target)
        {
            foreach (var instr in body.Instructions)
            {
                if (instr.Operand == target)
                    return true;
                if (instr.Operand is Instruction[] targets && targets.Contains(target))
                    return true;
            }

            foreach (var handler in body.ExceptionHandlers)
            {
                if (handler.TryStart == target || handler.TryEnd == target || handler.HandlerStart == target || handler.HandlerEnd == target || handler.FilterStart == target)
                    return true;
            }
            return false;
        }
    }
}
