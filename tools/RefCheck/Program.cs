using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

namespace RefCheck
{
    /// <summary>
    /// Prints the assembly references of a plugin DLL, then walks every type
    /// reference in the assembly and reports which assembly actually defines it.
    ///
    /// The second part is the point: a build can compile cleanly and still be
    /// unloadable when a well-known type is bound to the wrong facade, which is
    /// exactly how the .NET 8 reference assemblies broke this plugin.
    /// </summary>
    internal static class Program
    {
        /// <summary>Types that must NOT be resolved through System.Runtime / mscorlib.</summary>
        private static readonly Dictionary<string, string> FacadeTraps =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "System.Runtime.InteropServices.RuntimeInformation",
                  "lives in System.Runtime.InteropServices.RuntimeInformation.dll on .NET 6" },
                { "System.Environment", "core type: must come from System.Runtime / mscorlib" },
                { "System.OperatingSystem", "core type: must come from System.Runtime / mscorlib" },
            };

        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("usage: RefCheck <assembly.dll> [...]");
                return 2;
            }

            int problems = 0;
            foreach (var path in args)
            {
                Console.WriteLine("=== " + path);
                try
                {
                    using (var asm = AssemblyDefinition.ReadAssembly(path))
                    {
                        problems += CheckAssemblies(asm);
                        problems += CheckTypeRefs(asm);
                    }
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine("    FAILED to read: " + e.GetType().Name + ": " + e.Message);
                    problems++;
                }
            }

            Console.WriteLine();
            Console.WriteLine(problems == 0
                ? "OK: no suspicious type bindings"
                : "PROBLEMS: " + problems + " suspicious binding(s)");
            return problems == 0 ? 0 : 1;
        }

        private static int CheckAssemblies(AssemblyDefinition asm)
        {
            Console.WriteLine("    assembly references:");
            foreach (var r in asm.MainModule.AssemblyReferences.OrderBy(x => x.Name))
                Console.WriteLine(string.Format("      {0,-50} {1}", r.Name, r.Version));
            return 0;
        }

        private static int CheckTypeRefs(AssemblyDefinition asm)
        {
            var scopes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            int problems = 0;

            foreach (var tr in asm.MainModule.GetTypeReferences())
            {
                var full = tr.FullName;
                var scope = tr.Scope != null ? tr.Scope.Name : "<none>";

                if (FacadeTraps.ContainsKey(full))
                {
                    // RuntimeInformation is legitimate only from its own assembly.
                    bool wrong = scope == "System.Runtime" || scope == "mscorlib" ||
                                 scope == "netstandard" || scope == "System.Private.CoreLib";
                    if (full.StartsWith("System.Runtime.InteropServices.RuntimeInformation", StringComparison.Ordinal)
                        ? wrong
                        : false)
                    {
                        Console.WriteLine(string.Format(
                            "      !! {0} bound to '{1}' -- {2}", full, scope, FacadeTraps[full]));
                        problems++;
                    }
                    else if (full.StartsWith("System.Runtime.InteropServices.RuntimeInformation", StringComparison.Ordinal))
                    {
                        Console.WriteLine(string.Format("      ok {0} -> {1}", full, scope));
                    }
                }

                if (!scopes.TryGetValue(scope, out var list))
                {
                    list = new List<string>();
                    scopes[scope] = list;
                }
                list.Add(full);
            }

            // A published summary is more useful than thousands of lines.
            Console.WriteLine("    type references by scope (top 12):");
            foreach (var kv in scopes.OrderByDescending(x => x.Value.Count).Take(12))
                Console.WriteLine(string.Format("      {0,-50} {1}", kv.Key, kv.Value.Count));

            // Any reference from a scope the plugin does not declare is a latent
            // TypeLoadException, so list those scopes explicitly.
            var declared = new HashSet<string>(
                asm.MainModule.AssemblyReferences.Select(x => x.Name), StringComparer.Ordinal);
            foreach (var kv in scopes)
            {
                if (!declared.Contains(kv.Key) && !kv.Key.StartsWith("System.", StringComparison.Ordinal))
                    Console.WriteLine(string.Format(
                        "      ?? scope '{0}' is NOT an assembly reference ({1} types)",
                        kv.Key, kv.Value.Count));
            }

            return problems;
        }
    }
}
