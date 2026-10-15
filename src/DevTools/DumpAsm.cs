using System;
using System.IO;
using System.Linq;
using Mono.Cecil;

class DumpAsm
{
    static int Main(string[] args)
    {
        if (args.Length == 0) { Console.Error.WriteLine("usage: DumpAsm <dll> [filter]"); return 2; }
        string path = args[0];
        string filter = args.Length > 1 ? args[1] : null;
        Console.WriteLine("### file: " + path + "  exists=" + File.Exists(path) +
                          (File.Exists(path) ? "  size=" + new FileInfo(path).Length : ""));
        var rp = new ReaderParameters { ReadSymbols = false };
        using (var asm = AssemblyDefinition.ReadAssembly(path, rp))
        {
            Console.WriteLine("### assembly: " + asm.Name.FullName);
            Console.WriteLine("### modules: " + asm.Modules.Count);
            foreach (var t in asm.MainModule.Types)
            {
                if (filter != null && t.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                    && !t.NestedTypes.Any(n => n.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;
                Console.WriteLine("TYPE " + t.FullName + "  [public=" + t.IsPublic + " base=" + (t.BaseType != null ? t.BaseType.FullName : "-") + "]");
                foreach (var n in t.NestedTypes)
                    Console.WriteLine("   NESTED " + n.FullName + " [public=" + n.IsPublic + " base=" + (n.BaseType != null ? n.BaseType.FullName : "-") + "]");
            }
        }
        return 0;
    }
}
