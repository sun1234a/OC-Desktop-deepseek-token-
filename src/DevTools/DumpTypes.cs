using System;
using System.IO;
using System.Linq;
using Mono.Cecil;

class DumpTypes
{
    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: DumpTypes <dll> <outfile> [keywordFilter]");
            return 2;
        }
        string path = args[0], outFile = args[1];
        string filter = args.Length > 2 ? args[2] : null;

        var sw = new StreamWriter(outFile, false, System.Text.Encoding.UTF8);
        using (var asm = AssemblyDefinition.ReadAssembly(path))
        {
            sw.WriteLine("# assembly: " + asm.Name.FullName);
            foreach (var t in asm.MainModule.Types)
                DumpType(sw, t, filter, "");
        }
        sw.Flush();
        sw.Close();
        Console.WriteLine("written: " + outFile + " (" + new FileInfo(outFile).Length + " bytes)");
        return 0;
    }

    static bool Match(string name, string filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        return name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static void DumpType(StreamWriter sw, TypeDefinition t, string filter, string indent)
    {
        bool self = Match(t.Name, filter);
        bool anyChild = t.NestedTypes.Any(n => Match(n.Name, filter));
        if (filter != null && !self && !anyChild) return;

        sw.WriteLine();
        sw.WriteLine(indent + "TYPE " + t.FullName +
                     "  [base=" + (t.BaseType != null ? t.BaseType.Name : "-") +
                     " public=" + t.IsPublic + "]");

        foreach (var f in t.Fields)
            sw.WriteLine(indent + "   FIELD  " + f.FieldType.Name + " " + f.Name);

        foreach (var p in t.Properties)
            sw.WriteLine(indent + "   PROP   " + p.PropertyType.Name + " " + p.Name);

        foreach (var m in t.Methods)
        {
            if (m.IsGetter || m.IsSetter || m.IsAddOn || m.IsRemoveOn) continue;
            string ps = string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name + " " + p.Name));
            sw.WriteLine(indent + "   METHOD " + (m.IsPublic ? "public" : "nonpub") + " " +
                         (m.IsStatic ? "static " : "") + m.ReturnType.Name + " " + m.Name + "(" + ps + ")");
        }

        foreach (var n in t.NestedTypes)
            DumpType(sw, n, filter, indent + "   ");
    }
}
