using System;
using System.IO;
using System.Linq;
using Mono.Cecil;

class DumpSig
{
    static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: DumpSig <dll> <outfile> <TypeName> [TypeName...]");
            return 2;
        }
        string path = args[0], outFile = args[1];
        var wanted = args.Skip(2).ToArray();

        var sw = new StreamWriter(outFile, false, System.Text.Encoding.UTF8);
        using (var asm = AssemblyDefinition.ReadAssembly(path))
        {
            var all = new System.Collections.Generic.List<TypeDefinition>();
            foreach (var t in asm.MainModule.Types) Collect(t, all);

            foreach (var name in wanted)
            {
                var t = all.FirstOrDefault(x => x.FullName == name || x.Name == name);
                if (t == null) { sw.WriteLine("### NOT FOUND: " + name); continue; }
                sw.WriteLine();
                sw.WriteLine("### " + t.FullName + " : " + (t.BaseType != null ? t.BaseType.FullName : "-"));
                foreach (var f in t.Fields)
                    sw.WriteLine("   FIELD  " + Full(f.FieldType) + "  " + f.Name);
                foreach (var m in t.Methods)
                {
                    string g = m.HasGenericParameters ? "<" + string.Join(",", m.GenericParameters.Select(x => x.Name)) + ">" : "";
                    string ps = string.Join(", ", m.Parameters.Select(p => Full(p.ParameterType) + " " + p.Name));
                    sw.WriteLine("   METHOD " + Acc(m) + (m.IsStatic ? " static" : "") + " " +
                                 Full(m.ReturnType) + " " + m.Name + g + "(" + ps + ")");
                }
            }
        }
        sw.Flush(); sw.Close();
        Console.WriteLine("written " + outFile);
        return 0;
    }

    static void Collect(TypeDefinition t, System.Collections.Generic.List<TypeDefinition> into)
    {
        into.Add(t);
        foreach (var n in t.NestedTypes) Collect(n, into);
    }

    static string Acc(MethodDefinition m)
    {
        if (m.IsPublic) return "public";
        if (m.IsFamily) return "protected";
        return "private";
    }

    static string Full(TypeReference t)
    {
        if (t == null) return "?";
        var g = t as GenericInstanceType;
        if (g != null)
            return g.ElementType.Name + "<" + string.Join(",", g.GenericArguments.Select(Full)) + ">";
        var a = t as ArrayType;
        if (a != null) return Full(a.ElementType) + "[]";
        var b = t as ByReferenceType;
        if (b != null) return "ref " + Full(b.ElementType);
        return t.Name;
    }
}
