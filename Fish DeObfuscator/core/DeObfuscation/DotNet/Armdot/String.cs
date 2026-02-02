using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using Fish.Shared;

namespace Fish_DeObfuscator.core.DeObfuscation.DotNet.Armdot
{
    public class String : IStage
    {
        private readonly Dictionary<string, byte[]> _fieldData = new Dictionary<string, byte[]>();
        private int _deobfCount;

        public void Execute(IContext context)
        {
            var module = context.ModuleDefinition;
            if (module == null)
                return;

            LoadStaticFieldData(module);

            foreach (var type in module.GetTypes())
            {
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    try
                    {
                        DeobfuscateMethodStrings(method);
                    }
                    catch { }
                }
            }

            if (_deobfCount > 0)
                Logger.Info($"    Deobfuscated {_deobfCount} char-array string patterns");
        }

        private void LoadStaticFieldData(ModuleDefMD module)
        {
            foreach (var type in module.GetTypes())
            {
                foreach (var field in type.Fields.Where(f => f.IsStatic))
                {
                    try
                    {
                        if (field.InitialValue != null && field.InitialValue.Length > 0)
                            _fieldData[field.FullName] = field.InitialValue;
                    }
                    catch { }
                }
            }
        }

        private void DeobfuscateMethodStrings(MethodDef method)
        {
            var instrs = method.Body.Instructions.ToList();
            var patterns = FindPatterns(instrs);
            foreach (var p in patterns.OrderByDescending(x => x.Start))
            {
                var val = Decode(p);
                if (!string.IsNullOrEmpty(val))
                {
                    var ldstr = new Instruction(OpCodes.Ldstr, val);
                    for (int i = p.End; i >= p.Start; i--)
                        method.Body.Instructions.RemoveAt(i);
                    method.Body.Instructions.Insert(p.Start, ldstr);
                    _deobfCount++;
                    instrs = method.Body.Instructions.ToList();
                }
            }
        }

        private List<P> FindPatterns(List<Instruction> instrs)
        {
            var res = new List<P>();
            for (int i = 0; i < instrs.Count - 5; i++)
            {
                if (Fish.Shared.Utils.IsIntegerConstant(instrs[i]) && instrs[i + 1].OpCode == OpCodes.Newarr)
                {
                    var t = instrs[i + 1].Operand as ITypeDefOrRef;
                    if (t?.FullName == "System.Char")
                    {
                        int sz = Fish.Shared.Utils.GetConstantValue(instrs[i]);
                        if (sz > 0 && sz <= 1000)
                        {
                            int end = -1;
                            for (int j = i + 3; j < Math.Min(instrs.Count, i + sz * 50); j++)
                                if (instrs[j].OpCode == OpCodes.Newobj && instrs[j].Operand is IMethod m && m.DeclaringType.FullName == "System.String")
                                {
                                    end = j;
                                    break;
                                }

                            if (end != -1)
                            {
                                var p = new P
                                {
                                    Start = i,
                                    End = end,
                                    Size = sz,
                                };
                                for (int j = i; j < end + 5 && j < instrs.Count; j++)
                                {
                                    if (instrs[j].OpCode == OpCodes.Stelem_I2)
                                    {
                                        int x = -1;
                                        for (int k = j - 1; k >= j - 15 && k >= 0; k--)
                                            if (instrs[k].OpCode == OpCodes.Xor)
                                            {
                                                x = k;
                                                break;
                                            }
                                        if (xor(instrs, x, out int a, out int b, out int idx))
                                            p.Assigns[idx] = (a, b);
                                    }
                                }
                                if (p.Assigns.Count > 0)
                                    res.Add(p);
                            }
                        }
                    }
                }
            }
            return res;
        }

        private bool xor(List<Instruction> ins, int x, out int a, out int b, out int idx)
        {
            a = b = idx = -1;
            if (x == -1)
                return false;
            a = val(ins, x - 1);
            b = val(ins, x - 2);
            for (int i = x - 3; i >= x - 15 && i >= 0; i--)
                if (Fish.Shared.Utils.IsIntegerConstant(ins[i]))
                {
                    idx = Fish.Shared.Utils.GetConstantValue(ins[i]);
                    break;
                }
            return idx != -1;
        }

        private int val(List<Instruction> ins, int i)
        {
            if (i < 0)
                return 0;
            if (Fish.Shared.Utils.IsIntegerConstant(ins[i]))
                return Fish.Shared.Utils.GetConstantValue(ins[i]);
            if (i > 5 && ins[i].OpCode == OpCodes.Conv_I4 && ins[i - 1].OpCode == OpCodes.Ldind_U1 && ins[i - 5].OpCode == OpCodes.Ldsflda)
            {
                var f = ins[i - 5].Operand as IField;
                int o = Fish.Shared.Utils.GetConstantValue(ins[i - 3]);
                if (f != null && _fieldData.TryGetValue(f.FullName, out var d) && o < d.Length)
                    return d[o];
            }
            return 0;
        }

        private string Decode(P p)
        {
            var cs = new char[p.Size];
            int v = 0;
            for (int i = 0; i < p.Size; i++)
                if (p.Assigns.TryGetValue(i, out var a))
                {
                    cs[i] = (char)(a.Item1 ^ a.Item2);
                    v++;
                }
            return v > 0 ? new string(cs).TrimEnd('\0') : null;
        }

        private class P
        {
            public int Start,
                End,
                Size;
            public Dictionary<int, (int, int)> Assigns = new Dictionary<int, (int, int)>();
        }
    }
}
