using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UWGame.SimSide;

namespace UW.Tools.XmlProxyGen;

internal static class Program
{
    /// <summary>
    /// Namespace the generated proxies live in. Must match
    /// CustomXmlSerializer.GeneratedProxyNamespace - the generator asserts that below rather
    /// than assuming it, because a mismatch would produce files the runtime cannot find.
    /// </summary>
    private const string ProxyNamespace = "UWGame.Generated.XmlProxies";

    private const string RegistryFileName = "XmlProxyRegistry.g.cs";

    private static int Main(string[] args)
    {
        bool check = args.Contains("--check");
        bool roundtrip = args.Contains("--roundtrip");
        string outDir = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));

        if (roundtrip) return RoundTrip();

        if (outDir == null)
        {
            Console.Error.WriteLine("usage: xmlproxygen <output-dir> [--check]");
            Console.Error.WriteLine("       xmlproxygen --roundtrip");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  Writes one <Type>.g.cs per proxied type plus " + RegistryFileName + ".");
            Console.Error.WriteLine("  --check     regenerates in memory and fails if what is on disk differs,");
            Console.Error.WriteLine("              which is how a stale proxy gets caught instead of silently");
            Console.Error.WriteLine("              serializing the wrong shape.");
            Console.Error.WriteLine("  --roundtrip serializes and deserializes each proxied type through the");
            Console.Error.WriteLine("              COMPILED proxies, proving they work and not merely compile.");
            return 2;
        }

        if (CustomXmlSerializer.GeneratedProxyNamespace != ProxyNamespace)
        {
            Console.Error.WriteLine(
                $"FATAL: CustomXmlSerializer.GeneratedProxyNamespace is " +
                $"'{CustomXmlSerializer.GeneratedProxyNamespace}' but this generator emits into " +
                $"'{ProxyNamespace}'. The runtime would not find the generated proxies.");
            return 1;
        }

        List<(Type Declaring, CustomXmlSerializer.XmlProxyData Data)> declared = Discover();
        Console.WriteLine($"==> {declared.Count} type(s) declare a _proxyData field");

        // Group by the PROXIED type, not the declaring type. Those are usually the same, but
        // DefendActionType and ScareActionType both declare
        // `new XmlProxyData(typeof(AttackType))` - a copy-paste bug that is present in the
        // untouched decompiler output, so it is the shipped game's, not the port's. The runtime
        // keys its cache on proxyData.ProxiedType, so all three share one proxy there and this
        // has to do the same or the generated set would not match what the game asks for.
        var groups = declared
            .GroupBy(d => d.Data.ProxiedType)
            .OrderBy(g => g.Key.FullName, StringComparer.Ordinal)
            .ToList();

        foreach (var g in groups.Where(g => g.Count() > 1))
        {
            Console.WriteLine($"    aliased: {g.Key.FullName} is the proxied type for " +
                              string.Join(", ", g.Select(x => x.Declaring.Name)));
        }

        // The proxy class is named after the proxied type's SIMPLE name and they all share one
        // namespace, so two proxied types with the same simple name in different namespaces
        // would collide. None do today; fail loudly rather than emit a file that silently
        // overwrites another.
        var collisions = groups.GroupBy(g => g.Key.Name).Where(g => g.Count() > 1).ToList();
        if (collisions.Count > 0)
        {
            foreach (var c in collisions)
                Console.Error.WriteLine($"FATAL: proxy name collision on '{c.Key}': " +
                                        string.Join(", ", c.Select(x => x.Key.FullName)));
            return 1;
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            Type proxied = group.Key;

            // Generate from every declaring type's XmlProxyData and require they agree. They do
            // today - the aliased three differ only in a flag passed to GetListOfTypeMappings,
            // which changes neither the number nor the order of the mappings the generated code
            // indexes into. If that ever stops being true the proxy would be ambiguous, and
            // guessing is worse than stopping.
            string body = null;
            Type bodyFrom = null;
            foreach (var (declaring, data) in group.OrderBy(x => x.Declaring.Name, StringComparer.Ordinal))
            {
                string candidate;
                try
                {
                    candidate = Normalize(new ProxyCodeGenerator().GetProxyCode(data));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"FATAL: {declaring.FullName}: " +
                        ex.GetBaseException().GetType().Name + ": " + ex.GetBaseException().Message);
                    return 1;
                }

                if (body == null) { body = candidate; bodyFrom = declaring; continue; }
                if (candidate != body)
                {
                    Console.Error.WriteLine(
                        $"FATAL: {proxied.FullName} is proxied by both {bodyFrom.Name} and " +
                        $"{declaring.Name}, and they generate DIFFERENT proxy source. The game " +
                        "caches one proxy per proxied type, so there is no correct choice here.");
                    return 1;
                }
            }

            files[proxied.Name + ".g.cs"] = Preamble(proxied, group.Select(x => x.Declaring)) + body;
        }
        files[RegistryFileName] = BuildRegistry(groups.Select(g => g.Key).ToList());

        return check ? Check(outDir, files) : Write(outDir, files);
    }

    /// <summary>
    /// Serializes and deserializes an instance of each proxied type through the proxies that
    /// are compiled into the game assembly this tool is running against.
    ///
    /// This is the gate that --check cannot give you: --check proves the generated source is
    /// current, and the build proves it compiles, but neither proves XmlSerializer can actually
    /// drive it. Round-tripping does, and it is what the dormant data/BaseData and
    /// data/Scenarios mod hooks depend on.
    ///
    /// The comparison is write -> read -> write, so it needs no reference XML: if the second
    /// write differs from the first, something was lost in between.
    /// </summary>
    private static int RoundTrip()
    {
        var groups = Discover()
            .GroupBy(d => d.Data.ProxiedType)
            .OrderBy(g => g.Key.FullName, StringComparer.Ordinal)
            .ToList();

        int ok = 0, skipped = 0, failed = 0;

        foreach (var group in groups)
        {
            Type t = group.Key;
            ConstructorInfo ctor = t.GetConstructor(Type.EmptyTypes);
            if (ctor == null)
            {
                Console.WriteLine($"  SKIP {t.FullName,-52} no parameterless constructor");
                skipped++;
                continue;
            }

            object instance;
            try
            {
                instance = ctor.Invoke(new object[0]);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  SKIP {t.FullName,-52} ctor threw " +
                                  ex.GetBaseException().GetType().Name);
                skipped++;
                continue;
            }

            try
            {
                var serializer = new System.Xml.Serialization.XmlSerializer(t);

                string first;
                using (var sw = new StringWriter())
                {
                    serializer.Serialize(sw, instance);
                    first = sw.ToString();
                }

                object read;
                using (var sr = new StringReader(first))
                    read = serializer.Deserialize(sr);

                string second;
                using (var sw = new StringWriter())
                {
                    serializer.Serialize(sw, read);
                    second = sw.ToString();
                }

                if (first == second)
                {
                    Console.WriteLine($"  OK   {t.FullName,-52} {first.Length,7} chars round-tripped");
                    ok++;
                }
                else
                {
                    Console.WriteLine($"  FAIL {t.FullName,-52} re-serialized output differs");
                    Console.WriteLine($"       {FirstDifference(first, second)}");
                    failed++;
                }
            }
            catch (Exception ex)
            {
                Exception root = ex.GetBaseException();
                Console.WriteLine($"  FAIL {t.FullName,-52} {root.GetType().Name}: {root.Message}");
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{ok} round-tripped, {skipped} skipped, {failed} failed, " +
                          $"of {groups.Count} proxied type(s).");
        return failed == 0 ? 0 : 1;
    }

    private static string FirstDifference(string a, string b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        int from = Math.Max(0, i - 40);
        return $"at char {i}: ...{Excerpt(a, from)} | ...{Excerpt(b, from)}";
    }

    private static string Excerpt(string s, int from) =>
        s.Substring(from, Math.Min(90, s.Length - from)).Replace("\r", "").Replace("\n", " ");

    /// <summary>
    /// Finds every proxied type. Each declares
    /// <c>public static readonly CustomXmlSerializer.XmlProxyData _proxyData</c>, and that
    /// field IS the registration - there is no list to keep in sync, which is why discovery is
    /// by reflection rather than by a hand-maintained table.
    /// </summary>
    private static List<(Type Declaring, CustomXmlSerializer.XmlProxyData Data)> Discover()
    {
        Assembly game = typeof(CustomXmlSerializer).Assembly;

        // GetTypes() throws wholesale if any one type fails to load; take what resolved.
        Type[] all;
        try
        {
            all = game.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            all = ex.Types.Where(t => t != null).ToArray();
            Console.WriteLine($"    (note: {ex.Types.Length - all.Length} type(s) failed to load; continuing)");
        }

        var result = new List<(Type, CustomXmlSerializer.XmlProxyData)>();
        foreach (Type t in all.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            // The generated proxies each carry a static _proxyData of their own, initialised by
            // GetXmlProxyData at class-init time and therefore null until the type is registered.
            // They are output, not input - skip them, or every run reports 27 phantom types.
            if (t.Namespace == ProxyNamespace) continue;

            FieldInfo f = t.GetField("_proxyData",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (f == null || f.FieldType != typeof(CustomXmlSerializer.XmlProxyData)) continue;

            var data = (CustomXmlSerializer.XmlProxyData)f.GetValue(null);
            if (data == null)
            {
                Console.Error.WriteLine($"    !! {t.FullName}: _proxyData is null; skipped");
                continue;
            }
            result.Add((t, data));
        }
        return result;
    }

    private static string Preamble(Type proxied, IEnumerable<Type> declaringTypes)
    {
        var declarers = declaringTypes.Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();

        var sb = new StringBuilder();
        sb.Append("// XML serialization proxy for ").Append(proxied.FullName).Append(".\n");
        sb.Append("//\n");
        sb.Append("// GENERATED FILE - do not edit. Regenerate with build/20-generate-xml-proxies.sh.\n");
        sb.Append("//\n");
        sb.Append("// The retail game built this class at runtime with CodeDom; .NET 8 removed runtime C#\n");
        sb.Append("// compilation, so it is generated at build time instead (PORT DEVIATION 12). The body\n");
        sb.Append("// below is CustomXmlSerializer's own CodeDom output, verbatim, so XmlSerializer sees\n");
        sb.Append("// exactly the shape it saw in the retail build and the on-disk XML format is unchanged.\n");
        if (declarers.Count > 1)
        {
            sb.Append("//\n");
            sb.Append("// NOTE: ").Append(string.Join(", ", declarers))
              .Append(" all name this as their proxied type,\n");
            sb.Append("// so they share this one proxy. That is retail behaviour - see PORTING-NOTES.md,\n");
            sb.Append("// \"The AttackType proxy is shared by three unrelated types\".\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// CodeDom emits CRLF and a trailing blank line inconsistently. Normalising to LF with a
    /// single trailing newline is what makes --check a meaningful comparison rather than one
    /// that trips over line endings after a git checkout.
    /// </summary>
    private static string Normalize(string code) =>
        code.Replace("\r\n", "\n").TrimEnd('\n') + "\n";

    private static string BuildRegistry(List<Type> proxiedTypes)
    {
        var sb = new StringBuilder();
        sb.Append("// Maps each proxied type to its generated XML serialization proxy.\n");
        sb.Append("//\n");
        sb.Append("// GENERATED FILE - do not edit. Regenerate with build/20-generate-xml-proxies.sh.\n");
        sb.Append("//\n");
        sb.Append("// CustomXmlSerializer.GenerateProxyAssembly looks the proxy up here instead of\n");
        sb.Append("// compiling one (PORT DEVIATION 12). A compile-time reference rather than a\n");
        sb.Append("// reflection-by-name lookup, so a missing or renamed proxy is a build error and not\n");
        sb.Append("// a first-time-you-load-a-mod error.\n");
        sb.Append("\n");
        sb.Append("using System;\n");
        sb.Append("using System.Collections.Generic;\n");
        sb.Append("\n");
        sb.Append("namespace ").Append(ProxyNamespace).Append(";\n");
        sb.Append("\n");
        sb.Append("internal static class XmlProxyRegistry\n");
        sb.Append("{\n");
        sb.Append("\tinternal static readonly Dictionary<Type, Type> ProxyTypes = new()\n");
        sb.Append("\t{\n");
        foreach (Type type in proxiedTypes)
            sb.Append("\t\t{ typeof(global::").Append(type.FullName)
              .Append("), typeof(global::").Append(ProxyNamespace).Append('.').Append(type.Name)
              .Append(") },\n");
        sb.Append("\t};\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    private static int Write(string outDir, Dictionary<string, string> files)
    {
        Directory.CreateDirectory(outDir);

        // Remove proxies for types that no longer exist, so a stale file cannot keep compiling.
        foreach (string existing in Directory.GetFiles(outDir, "*.g.cs"))
        {
            if (!files.ContainsKey(Path.GetFileName(existing)))
            {
                File.Delete(existing);
                Console.WriteLine("    - " + Path.GetFileName(existing) + " (no longer proxied)");
            }
        }

        int written = 0, unchanged = 0;
        foreach (var kv in files.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            string path = Path.Combine(outDir, kv.Key);
            if (File.Exists(path) && Normalize(File.ReadAllText(path)) == Normalize(kv.Value))
            {
                unchanged++;
                continue;
            }
            File.WriteAllText(path, kv.Value);
            written++;
        }
        Console.WriteLine($"==> {written} written, {unchanged} unchanged, in {outDir}");
        return 0;
    }

    private static int Check(string outDir, Dictionary<string, string> files)
    {
        var problems = new List<string>();

        foreach (var kv in files.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            string path = Path.Combine(outDir, kv.Key);
            if (!File.Exists(path)) { problems.Add(kv.Key + ": missing"); continue; }
            if (Normalize(File.ReadAllText(path)) != Normalize(kv.Value))
                problems.Add(kv.Key + ": out of date");
        }
        foreach (string existing in Directory.Exists(outDir)
                     ? Directory.GetFiles(outDir, "*.g.cs")
                     : Array.Empty<string>())
        {
            if (!files.ContainsKey(Path.GetFileName(existing)))
                problems.Add(Path.GetFileName(existing) + ": orphaned (type no longer proxied)");
        }

        if (problems.Count == 0)
        {
            Console.WriteLine($"==> up to date: {files.Count} generated file(s) match the current types.");
            return 0;
        }

        Console.Error.WriteLine($"==> {problems.Count} problem(s):");
        foreach (string p in problems) Console.Error.WriteLine("    " + p);
        Console.Error.WriteLine();
        Console.Error.WriteLine("Run build/20-generate-xml-proxies.sh to regenerate.");
        return 1;
    }
}
