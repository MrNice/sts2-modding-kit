using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

namespace Sts2Inspect;

/// <summary>
/// sts2-inspect: read-only views of Slay the Spire 2's code and game pack, for mod authors.
/// Nothing here runs or modifies the game; it reads files from your own install.
/// </summary>
internal static class Program
{
    private const string Usage = """
        sts2-inspect 0.0.1: look inside Slay the Spire 2 (read-only)

        Game code (sts2.dll, in <game>/data_sts2_windows_x86_64/):
          types   <dll> <filter>...            members of matching types (filter: substring, or =Name for an exact name)
          il      <dll> <typeFilter> [method]  IL of matching types' methods, with member names resolved. With a method
                                               name, an async/iterator method's state machine body is shown too
          has     <dll> <Type.member>...       does each member exist? Warns when a method name has several overloads
          refs    <mod.dll> [prefix]           game members a built mod uses, with signatures (prefix default: MegaCrit)

        Comparing two game versions:
          typediff <old.dll> <new.dll> <filter>...   member lists that differ between versions
          ildiff   <old.dll> <new.dll> <regex>...    methods whose IL changed (regex on Type::Method; compiler-generated
                                                     numbering is ignored). Prints CHANGED / REMOVED / NEW
          audit    <old.dll> <new.dll> <mod.dll> [modSourceDir]
                                                     everything a mod relies on: member references by signature,
                                                     name-based patch/reflection targets found in the source, and IL
                                                     changes in the game methods the mod calls or patches.
                                                     Exit 0: clean; 3: something to fix; 4: no source given

        Game pack (SlayTheSpire2.pck, Godot 4 pack format 2/3, unencrypted):
          pck list    <pck> [regex]                files in the pack (path, size)
          pck extract <pck> <regex> <outDir>       copy matching files out (scenes .tscn, localization .json, ...)
        """;

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0) { Console.WriteLine(Usage); return 1; }
            switch (args[0])
            {
                case "types" when args.Length >= 3: Types(Open(args[1]), args[2..], Console.Out); return 0;
                case "il" when args.Length >= 3: Il(args[1], args[2], args.Length > 3 ? args[3] : null); return 0;
                case "has" when args.Length >= 3: return Has(Open(args[1]), args[2..]);
                case "refs" when args.Length >= 2: foreach (var r in Refs(args[1], args.Length > 2 ? args[2] : "MegaCrit")) Console.WriteLine(r); return 0;
                case "typediff" when args.Length >= 4: return TypeDiff(args[1], args[2], args[3..]);
                case "ildiff" when args.Length >= 4: return IlDiff(args[1], args[2], args[3..].Select(a => new Regex("^(?:" + a + ")$")).ToList(), null);
                case "audit" when args.Length >= 4: return Audit(args[1], args[2], args[3], args.Length > 4 ? args[4] : null);
                case "pck" when args.Length >= 3 && args[1] == "list": PckList(args[2], args.Length > 3 ? args[3] : null); return 0;
                case "pck" when args.Length >= 5 && args[1] == "extract": PckExtract(args[2], args[3], args[4]); return 0;
                default: Console.WriteLine(Usage); return 1;
            }
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or InvalidDataException or UnauthorizedAccessException
                                       or ArgumentException or InvalidOperationException)
        {
            // Wrong file, not a .NET assembly, bad regex, unreadable pack: say so plainly.
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    // ------------------------------------------------------------------ metadata

    private sealed class Dll
    {
        public required PEReader Pe { get; init; }
        public required MetadataReader Md { get; init; }
        public required SigNames Names { get; init; }
    }

    private static Dll Open(string path)
    {
        var pe = new PEReader(File.OpenRead(path));
        var md = pe.GetMetadataReader();
        return new Dll { Pe = pe, Md = md, Names = new SigNames(md) };
    }

    /// <summary>Type and member names as plain strings, so two versions (or a mod and the game) compare by text.</summary>
    private sealed class SigNames : ISignatureTypeProvider<string, object>
    {
        private readonly MetadataReader _md;
        public SigNames(MetadataReader md) => _md = md;

        public string Def(TypeDefinitionHandle h)
        {
            var t = _md.GetTypeDefinition(h);
            var d = t.GetDeclaringType();
            if (!d.IsNil) return Def(d) + "+" + _md.GetString(t.Name);
            var ns = _md.GetString(t.Namespace);
            return ns.Length == 0 ? _md.GetString(t.Name) : ns + "." + _md.GetString(t.Name);
        }

        public string Ref(TypeReferenceHandle h)
        {
            var t = _md.GetTypeReference(h);
            if (t.ResolutionScope.Kind == HandleKind.TypeReference) return Ref((TypeReferenceHandle)t.ResolutionScope) + "+" + _md.GetString(t.Name);
            var ns = _md.GetString(t.Namespace);
            return ns.Length == 0 ? _md.GetString(t.Name) : ns + "." + _md.GetString(t.Name);
        }

        public string Any(EntityHandle h) => h.Kind switch
        {
            HandleKind.TypeDefinition => Def((TypeDefinitionHandle)h),
            HandleKind.TypeReference => Ref((TypeReferenceHandle)h),
            HandleKind.TypeSpecification => Spec((TypeSpecificationHandle)h),
            _ => h.Kind.ToString(),
        };

        private string Spec(TypeSpecificationHandle h)
        {
            try { return _md.GetTypeSpecification(h).DecodeSignature(this, null); }
            catch { return "?"; }
        }

        public string GetArrayType(string e, ArrayShape s) => e + "[" + new string(',', Math.Max(0, s.Rank - 1)) + "]";
        public string GetByReferenceType(string e) => "ref " + e;
        public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
        public string GetGenericInstantiation(string g, ImmutableArray<string> a) => $"{g}<{string.Join(",", a)}>";
        public string GetGenericMethodParameter(object c, int i) => "!!" + i;
        public string GetGenericTypeParameter(object c, int i) => "!" + i;
        public string GetModifiedType(string m, string u, bool r) => u;
        public string GetPinnedType(string e) => e;
        public string GetPointerType(string e) => e + "*";
        public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString();
        public string GetSZArrayType(string e) => e + "[]";
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => Def(h);
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => Ref(h);
        public string GetTypeFromSpecification(MetadataReader r, object c, TypeSpecificationHandle h, byte k) => Spec(h);
    }

    private static bool Matches(string fullName, string filter) =>
        filter.StartsWith('=')
            ? fullName == filter[1..] || fullName.EndsWith("." + filter[1..]) || fullName.EndsWith("+" + filter[1..])
            : fullName.Contains(filter, StringComparison.Ordinal);

    private static string MethodSig(Dll d, MethodDefinition m)
    {
        var s = m.DecodeSignature(d.Names, null);
        return $"{s.ReturnType} {d.Md.GetString(m.Name)}({string.Join(", ", s.ParameterTypes)})";
    }

    private static string Access(MethodAttributes a) => (a & MethodAttributes.MemberAccessMask) switch
    {
        MethodAttributes.Public => "public",
        MethodAttributes.Private => "private",
        MethodAttributes.Family => "protected",
        MethodAttributes.Assembly => "internal",
        MethodAttributes.FamORAssem => "protected internal",
        _ => "private protected",
    };

    /// <summary>Every member of each matching type, one per line (property accessors folded into P lines).</summary>
    private static void Types(Dll d, IReadOnlyList<string> filters, TextWriter o)
    {
        var md = d.Md;
        foreach (var h in md.TypeDefinitions)
        {
            var full = d.Names.Def(h);
            if (!filters.Any(f => Matches(full, f))) continue;
            var t = md.GetTypeDefinition(h);
            o.WriteLine($"== {full} : {(t.BaseType.IsNil ? "" : d.Names.Any(t.BaseType))}");
            foreach (var ih in t.GetInterfaceImplementations()) o.WriteLine($"  I {d.Names.Any(md.GetInterfaceImplementation(ih).Interface)}");
            foreach (var fh in t.GetFields())
            {
                var f = md.GetFieldDefinition(fh);
                o.WriteLine($"  F {((f.Attributes & FieldAttributes.Static) != 0 ? "static " : "")}{f.DecodeSignature(d.Names, null)} {md.GetString(f.Name)}");
            }
            foreach (var ph in t.GetProperties())
            {
                var p = md.GetPropertyDefinition(ph);
                var get = p.GetAccessors().Getter;
                string acc = get.IsNil ? "set-only" : Access(md.GetMethodDefinition(get).Attributes);
                o.WriteLine($"  P {acc} {p.DecodeSignature(d.Names, null).ReturnType} {md.GetString(p.Name)}");
            }
            foreach (var mh in t.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                var name = md.GetString(m.Name);
                if (name.StartsWith("get_") || name.StartsWith("set_") || name.StartsWith("add_") || name.StartsWith("remove_")) continue;
                bool isStatic = (m.Attributes & MethodAttributes.Static) != 0;
                o.WriteLine($"  M {Access(m.Attributes)}{(isStatic ? " static" : "")} {MethodSig(d, m)}");
            }
            foreach (var eh in t.GetEvents()) o.WriteLine($"  E {md.GetString(md.GetEventDefinition(eh).Name)}");
        }
    }

    private static Dictionary<string, List<TypeDefinitionHandle>> TypesBySimpleAndFullName(Dll d)
    {
        var map = new Dictionary<string, List<TypeDefinitionHandle>>();
        foreach (var h in d.Md.TypeDefinitions)
        {
            var full = d.Names.Def(h);
            foreach (var key in new[] { full, full[(full.LastIndexOfAny(new[] { '.', '+' }) + 1)..] }.Distinct())
            {
                if (!map.TryGetValue(key, out var list)) map[key] = list = new();
                list.Add(h);
            }
        }
        return map;
    }

    /// <summary>Kind and signature of each member called <paramref name="name"/> on the type (fields, properties, events, methods).</summary>
    private static List<string> MembersNamed(Dll d, TypeDefinitionHandle th, string name)
    {
        var md = d.Md;
        var t = md.GetTypeDefinition(th);
        var found = new List<string>();
        foreach (var fh in t.GetFields())
        {
            var f = md.GetFieldDefinition(fh);
            if (md.GetString(f.Name) == name) found.Add($"field {f.DecodeSignature(d.Names, null)}");
        }
        foreach (var ph in t.GetProperties())
        {
            var p = md.GetPropertyDefinition(ph);
            if (md.GetString(p.Name) == name) found.Add($"property {p.DecodeSignature(d.Names, null).ReturnType}");
        }
        foreach (var eh in t.GetEvents())
            if (md.GetString(md.GetEventDefinition(eh).Name) == name) found.Add("event");
        foreach (var mh in t.GetMethods())
        {
            var m = md.GetMethodDefinition(mh);
            if (md.GetString(m.Name) == name) found.Add("method " + MethodSig(d, m));
        }
        return found;
    }

    private static int Has(Dll d, IReadOnlyList<string> specs)
    {
        var types = TypesBySimpleAndFullName(d);
        int missing = 0;
        foreach (var spec in specs)
        {
            var (members, typeFound) = Lookup(d, types, spec);
            if (!typeFound) { Console.WriteLine($"MISSING TYPE  {spec}"); missing++; continue; }
            if (members.Count == 0) { Console.WriteLine($"MISSING       {spec}"); missing++; continue; }
            int methods = members.Count(m => m.StartsWith("method "));
            Console.WriteLine($"{(methods > 1 ? "ok (overloads)" : "ok"),-13} {spec}");
            if (methods > 1)
                foreach (var m in members) Console.WriteLine($"                {m}   <- patching by name alone is ambiguous");
        }
        return missing == 0 ? 0 : 3;
    }

    private static (List<string> Members, bool TypeFound) Lookup(Dll d, Dictionary<string, List<TypeDefinitionHandle>> types, string spec)
    {
        int dot = spec.LastIndexOf('.');
        if (dot <= 0) return (new(), false);
        string type = spec[..dot], member = spec[(dot + 1)..];
        if (!types.TryGetValue(type, out var handles)) return (new(), false);
        // Two game types can share a short name; say which one each member belongs to then.
        return (handles.SelectMany(h => MembersNamed(d, h, member).Select(m => handles.Count > 1 ? $"{m}   [in {d.Names.Def(h)}]" : m)).ToList(), true);
    }

    // ------------------------------------------------------------------ mod references

    /// <summary>Game members a compiled mod references (by full type name), with the signature it was compiled against.</summary>
    private static SortedSet<string> Refs(string modPath, string prefix)
    {
        var d = Open(modPath);
        var md = d.Md;
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var h in md.MemberReferences)
        {
            var m = md.GetMemberReference(h);
            string parent = d.Names.Any(m.Parent);
            if (!parent.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string name = md.GetString(m.Name);
            string sig;
            try
            {
                if (m.GetKind() == MemberReferenceKind.Method)
                {
                    var s = m.DecodeMethodSignature(d.Names, null);
                    sig = $"{s.ReturnType} {name}({string.Join(", ", s.ParameterTypes)})";
                }
                else sig = $"field {m.DecodeFieldSignature(d.Names, null)} {name}";
            }
            catch { sig = name + " (undecodable)"; }
            set.Add($"{parent}::{sig}");
        }
        return set;
    }

    /// <summary>Every method/field signature defined in the game, keyed the same way as <see cref="Refs"/>.</summary>
    private static HashSet<string> Definitions(Dll d)
    {
        var md = d.Md;
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            string full = d.Names.Def(th);
            // A generic type's members are referenced through its instantiation; key them by open name too.
            int arity = t.GetGenericParameters().Count;
            string open = arity > 0 ? $"{full}<{string.Join(",", Enumerable.Range(0, arity).Select(i => "!" + i))}>" : full;
            foreach (var mh in t.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                set.Add($"{full}::{MethodSig(d, m)}");
                if (arity > 0) set.Add($"{open}::{MethodSig(d, m)}");
            }
            foreach (var fh in t.GetFields())
            {
                var f = md.GetFieldDefinition(fh);
                set.Add($"{full}::field {f.DecodeSignature(d.Names, null)} {md.GetString(f.Name)}");
                if (arity > 0) set.Add($"{open}::field {f.DecodeSignature(d.Names, null)} {md.GetString(f.Name)}");
            }
        }
        return set;
    }

    // ------------------------------------------------------------------ IL

    private static readonly Dictionary<short, OpCode> OneByte = new(), TwoByte = new();

    private static void LoadOpCodes()
    {
        if (OneByte.Count > 0) return;
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var op = (OpCode)f.GetValue(null)!;
            if (op.Size == 1) OneByte[op.Value] = op; else TwoByte[(short)(op.Value & 0xff)] = op;
        }
    }

    private static string MemberName(Dll d, EntityHandle h)
    {
        var md = d.Md;
        switch (h.Kind)
        {
            case HandleKind.MethodDefinition: { var m = md.GetMethodDefinition((MethodDefinitionHandle)h); return Short(d.Names.Def(m.GetDeclaringType())) + "::" + md.GetString(m.Name); }
            case HandleKind.FieldDefinition: { var f = md.GetFieldDefinition((FieldDefinitionHandle)h); return Short(d.Names.Def(f.GetDeclaringType())) + "::" + md.GetString(f.Name); }
            case HandleKind.MemberReference: { var m = md.GetMemberReference((MemberReferenceHandle)h); return Short(d.Names.Any(m.Parent)) + "::" + md.GetString(m.Name); }
            case HandleKind.MethodSpecification:
            {
                var s = md.GetMethodSpecification((MethodSpecificationHandle)h);
                string a;
                try { a = string.Join(",", s.DecodeSignature(d.Names, null).Select(Short)); } catch { a = ""; }
                return MemberName(d, s.Method) + "<" + a + ">";
            }
            default: return Short(d.Names.Any(h));
        }
    }

    /// <summary>Drops namespaces for readability: MegaCrit.Sts2.Core.Combat.CombatManager → CombatManager.</summary>
    private static string Short(string name) => Regex.Replace(name, @"(?:[A-Za-z_][A-Za-z0-9_]*\.)+(?=[A-Za-z_<])", "");

    /// <summary>IL of each method in matching types, as "Type::Method" → instruction lines.</summary>
    private static IEnumerable<(string Key, List<string> Lines)> Methods(Dll d, Func<string, bool> typeFilter, string method)
    {
        LoadOpCodes();
        var md = d.Md;
        foreach (var th in md.TypeDefinitions)
        {
            string full = d.Names.Def(th);
            if (!typeFilter(full)) continue;
            string shortName = Short(full);
            foreach (var mh in md.GetTypeDefinition(th).GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                string name = md.GetString(m.Name);
                if (method is not null && name != method) continue;
                if (m.RelativeVirtualAddress == 0) continue;
                yield return ($"{shortName}::{name}", Disassemble(d, m));
            }
        }
    }

    private static List<string> Disassemble(Dll d, MethodDefinition m)
    {
        var lines = new List<string>();
        var r = d.Pe.GetMethodBody(m.RelativeVirtualAddress).GetILReader();
        while (r.RemainingBytes > 0)
        {
            int off = r.Offset;
            byte b0 = r.ReadByte();
            if (!(b0 == 0xFE ? TwoByte.TryGetValue(r.ReadByte(), out var op) : OneByte.TryGetValue(b0, out op)))
            {
                lines.Add($"IL_{off:x4} <unknown opcode; rest of method skipped>");
                break;
            }
            string arg = "";
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: { sbyte x = r.ReadSByte(); arg = $"IL_{r.Offset + x:x4}"; break; }
                case OperandType.InlineBrTarget: { int x = r.ReadInt32(); arg = $"IL_{r.Offset + x:x4}"; break; }
                case OperandType.ShortInlineI: case OperandType.ShortInlineVar: arg = r.ReadByte().ToString(); break;
                case OperandType.InlineVar: arg = r.ReadUInt16().ToString(); break;
                case OperandType.InlineI: arg = r.ReadInt32().ToString(); break;
                case OperandType.InlineI8: arg = r.ReadInt64().ToString(); break;
                case OperandType.ShortInlineR: arg = r.ReadSingle().ToString(System.Globalization.CultureInfo.InvariantCulture); break;
                case OperandType.InlineR: arg = r.ReadDouble().ToString(System.Globalization.CultureInfo.InvariantCulture); break;
                case OperandType.InlineString: arg = "\"" + d.Md.GetUserString(MetadataTokens.UserStringHandle(r.ReadInt32() & 0xffffff)) + "\""; break;
                case OperandType.InlineSwitch:
                {
                    int n = r.ReadInt32();
                    var offs = new int[n];
                    for (int i = 0; i < n; i++) offs[i] = r.ReadInt32();
                    int b = r.Offset;
                    arg = string.Join(",", offs.Select(o => $"IL_{b + o:x4}"));
                    break;
                }
                case OperandType.InlineSig: r.ReadInt32(); arg = "sig"; break;
                default:
                {
                    int tok = r.ReadInt32();
                    try { arg = MemberName(d, MetadataTokens.EntityHandle(tok)); } catch { arg = $"tok {tok:x8}"; }
                    break;
                }
            }
            lines.Add($"IL_{off:x4} {op.Name} {arg}".TrimEnd());
        }
        return lines;
    }

    private static void Il(string dll, string typeFilter, string method)
    {
        var d = Open(dll);
        foreach (var (key, lines) in Methods(d, full => Matches(full, typeFilter), method))
            Print(key, lines);
        // An async or iterator method's body lives in a nested state machine: Type+<Method>d__N::MoveNext.
        if (method is null) return;
        foreach (var (key, lines) in Methods(d, full => full.Contains($"+<{method}>d__", StringComparison.Ordinal)
                                                         && Matches(full[..full.IndexOf("+<", StringComparison.Ordinal)], typeFilter), "MoveNext"))
            Print(key, lines);

        static void Print(string key, List<string> lines)
        {
            Console.WriteLine("=== " + key);
            foreach (var l in lines) Console.WriteLine("  " + l);
        }
    }

    /// <summary>Compiler-generated names get renumbered between builds (d__12 → d__15); ignore that, and IL offsets.</summary>
    private static string Normalize(string s) =>
        Regex.Replace(Regex.Replace(s, @"IL_[0-9a-f]{4}", "L"), @"(d__|b__|DisplayClass|<>9__)\d+(_\d+)?", "$1N");

    private static Dictionary<string, List<string>> NormalizedMethods(Dll d, Func<string, bool> typeFilter)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (key, lines) in Methods(d, typeFilter, null))
        {
            var k = Normalize(key);
            if (!map.TryGetValue(k, out var bodies)) map[k] = bodies = new();
            bodies.Add(string.Join("\n", lines.Select(Normalize)));
        }
        foreach (var k in map.Keys.ToList()) map[k].Sort(StringComparer.Ordinal);
        return map;
    }

    private static int IlDiff(string oldDll, string newDll, List<Regex> patterns, HashSet<string> onlyTypes, HashSet<string> focus = null)
    {
        Func<string, bool> filter = full => onlyTypes is null || onlyTypes.Contains(Short(full).Split('+')[0]);
        var a = NormalizedMethods(Open(oldDll), filter);
        var b = NormalizedMethods(Open(newDll), filter);
        int changed = 0;
        var others = new List<string>();
        foreach (var key in a.Keys.Union(b.Keys).Where(k => patterns.Count == 0 || patterns.Any(p => p.IsMatch(k))).OrderBy(k => k, StringComparer.Ordinal))
        {
            bool inA = a.TryGetValue(key, out var x), inB = b.TryGetValue(key, out var y);
            if (inA && inB && x.SequenceEqual(y)) continue;
            changed++;
            // With a focus set (the audit), only the methods the mod calls or patches get the detail.
            if (focus is not null && !focus.Contains(key.Split('+')[0].Split("::")[0] + "::" + key.Split("::")[^1]) && !focus.Contains(FocusKey(key)))
            {
                others.Add(key);
                continue;
            }
            if (!inB) { Console.WriteLine($"REMOVED  {key}"); continue; }
            if (!inA) { Console.WriteLine($"NEW      {key}"); continue; }
            var left = x.SelectMany(s => s.Split('\n')).ToList();
            var right = y.SelectMany(s => s.Split('\n')).ToList();
            var gone = left.Except(right).Take(6).ToList();
            var added = right.Except(left).Take(6).ToList();
            Console.WriteLine($"CHANGED  {key}");
            foreach (var l in gone) Console.WriteLine($"     - {l}");
            foreach (var l in added) Console.WriteLine($"     + {l}");
        }
        if (others.Count > 0)
        {
            Console.WriteLine($"... and {others.Count} other changed method(s) in those types (the mod doesn't call or patch them directly):");
            foreach (var group in others.GroupBy(k => k.Split("::")[0].Split('+')[0]).OrderBy(g => g.Key))
                Console.WriteLine($"     {group.Key}: {group.Count()}");
            Console.WriteLine("   see them with: sts2-inspect ildiff <old.dll> <new.dll> 'TypeName.*'");
        }
        Console.WriteLine($"{changed} method(s) differ");
        return 0;
    }

    /// <summary>"Type+&lt;Method&gt;d__N::MoveNext" (an async/iterator body) belongs to Type::Method.</summary>
    private static string FocusKey(string key)
    {
        var m = Regex.Match(key, @"^([^+:]+)\+<([^>]+)>d__N::MoveNext$");
        return m.Success ? $"{m.Groups[1].Value}::{m.Groups[2].Value}" : key;
    }

    private static int TypeDiff(string oldDll, string newDll, string[] filters)
    {
        // Compare each type's member list on its own, so identical lines in different types can't hide changes.
        Dictionary<string, (string Header, List<string> Lines)> Listing(string dll)
        {
            var w = new StringWriter();
            Types(Open(dll), filters, w);
            var map = new Dictionary<string, (string Header, List<string> Lines)>(StringComparer.Ordinal);
            string type = null;
            foreach (var raw in w.ToString().Split('\n'))
            {
                var line = raw.TrimEnd();
                if (line.Length == 0) continue;
                if (line.StartsWith("== "))
                {
                    var name = line[3..].Split(" : ")[0];
                    type = name;
                    map[name] = (line, new List<string>());
                }
                else if (type is not null) map[type].Lines.Add(line.Trim());
            }
            return map;
        }
        var a = Listing(oldDll);
        var b = Listing(newDll);
        int n = 0;
        foreach (var type in a.Keys.Union(b.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!b.TryGetValue(type, out var now)) { Console.WriteLine($"REMOVED TYPE {type}"); n++; continue; }
            if (!a.TryGetValue(type, out var was)) { Console.WriteLine($"NEW TYPE {type}"); n++; continue; }
            var gone = was.Lines.Except(now.Lines, StringComparer.Ordinal).ToList();
            var added = now.Lines.Except(was.Lines, StringComparer.Ordinal).ToList();
            bool header = was.Header != now.Header;
            if (!header && gone.Count == 0 && added.Count == 0) continue;
            Console.WriteLine(now.Header);
            if (header) Console.WriteLine($"  base type was: {was.Header.Split(" : ").ElementAtOrDefault(1)}");
            foreach (var l in gone) Console.WriteLine("- " + l);
            foreach (var l in added) Console.WriteLine("+ " + l);
            n += gone.Count + added.Count + (header ? 1 : 0);
        }
        Console.WriteLine($"{n} difference(s)");
        return 0;
    }

    // ------------------------------------------------------------------ audit

    private static readonly Regex[] NameTargets =
    {
        // [HarmonyPatch(typeof(T), "Name")] / AccessTools.Method(typeof(T), "Name") / AccessTools.Field / Property
        new(@"(?:HarmonyPatch|AccessTools\.(?:Method|Field|Property|DeclaredMethod|DeclaredField|DeclaredProperty))\(\s*typeof\(\s*([A-Za-z_][\w.]*)\s*\)\s*,\s*""(\w+)"""),
        // [HarmonyPatch(typeof(T), nameof(T.Name))]
        new(@"HarmonyPatch\(\s*typeof\(\s*([A-Za-z_][\w.]*)\s*\)\s*,\s*nameof\(\s*(?:[\w.]*\.)?(\w+)\s*\)"),
        // typeof(T).GetField("Name") / GetMethod / GetProperty
        new(@"typeof\(\s*([A-Za-z_][\w.]*)\s*\)\s*\.\s*Get(?:Field|Method|Property)\(\s*""(\w+)"""),
        // AccessTools.Method(typeof(T), nameof(T.Name)), PropertyGetter/Setter with a string or nameof
        new(@"AccessTools\.(?:Method|Field|Property|DeclaredMethod|PropertyGetter|PropertySetter|DeclaredPropertyGetter)\(\s*typeof\(\s*([A-Za-z_][\w.]*)\s*\)\s*,\s*(?:""(\w+)""|nameof\(\s*(?:[\w.]*\.)?(\w+)\s*\))"),
        // "T:Name" method strings
        new(@"AccessTools\.Method\(\s*""([A-Za-z_][\w.]*):(\w+)"""),
    };

    // [HarmonyPatch(typeof(T))] on the class with [HarmonyPatch("Name")] / [HarmonyPatch(nameof(T.Name))] on a method.
    private static readonly Regex ClassPatch = new(@"HarmonyPatch\(\s*typeof\(\s*([A-Za-z_][\w.]*)\s*\)\s*\)");
    private static readonly Regex MethodPatch = new(@"HarmonyPatch\(\s*(?:""(\w+)""|nameof\(\s*(?:[\w.]*\.)?(\w+)\s*\))");

    private static int Audit(string oldDll, string newDll, string modDll, string sourceDir)
    {
        var oldD = Open(oldDll);
        var newD = Open(newDll);

        Console.WriteLine("# 1. Members the mod was compiled against (signature must still exist)");
        var refs = Refs(modDll, "MegaCrit");
        var newDefs = Definitions(newD);
        var oldDefs = Definitions(oldD);
        int broken = 0;
        foreach (var r in refs)
        {
            if (r.StartsWith("<") || r.Contains("(undecodable)")) continue;
            if (newDefs.Contains(r)) continue;
            // Only report what the old game had: anything else is a reference into a generic instantiation we can't key.
            if (!oldDefs.Contains(r)) continue;
            Console.WriteLine($"  BROKEN  {r}");
            broken++;
        }
        Console.WriteLine(broken == 0 ? "  all present" : $"  {broken} reference(s) would fail at runtime (MissingMethodException / MissingFieldException)");

        Console.WriteLine();
        Console.WriteLine("# 2. Name-based targets in the source (Harmony patches, reflection)");
        int nameIssues = 0;
        var focus = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in refs)
        {
            int sep = r.IndexOf("::", StringComparison.Ordinal);
            string type = Short(r[..sep]).Split('<')[0];
            var name = Regex.Match(r[(sep + 2)..], @"(?:^field \S+ |^\S+ )([^ (]+)").Groups[1].Value;
            focus.Add($"{type}::{name}");
        }
        if (sourceDir is null) Console.WriteLine("  SKIPPED: no source folder given, so patch and reflection targets were not checked");
        else
        {
            var oldTypes = TypesBySimpleAndFullName(oldD);
            var newTypes = TypesBySimpleAndFullName(newD);
            var targets = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                var text = File.ReadAllText(file);
                void Add(string typeName, string member)
                {
                    string type = typeName.Split('.').Last();
                    targets.Add($"{type}.{member}");
                    focus.Add($"{type}::{member}");
                }
                foreach (var rx in NameTargets)
                    foreach (Match m in rx.Matches(text))
                        Add(m.Groups[1].Value, m.Groups[2].Success && m.Groups[2].Length > 0 ? m.Groups[2].Value : m.Groups[3].Value);
                // Split patches: remember the last class-level target and pair it with method-level names after it.
                string classTarget = null;
                foreach (Match m in Regex.Matches(text, ClassPatch + "|" + MethodPatch))
                {
                    var c = ClassPatch.Match(m.Value);
                    if (c.Success && c.Index == 0 && c.Length == m.Length) { classTarget = c.Groups[1].Value; continue; }
                    var mm = MethodPatch.Match(m.Value);
                    if (classTarget is not null && mm.Success && mm.Index == 0)
                        Add(classTarget, mm.Groups[1].Success && mm.Groups[1].Length > 0 ? mm.Groups[1].Value : mm.Groups[2].Value);
                }
            }
            foreach (var t in targets)
            {
                var (before, oldType) = Lookup(oldD, oldTypes, t);
                var (after, newType) = Lookup(newD, newTypes, t);
                if (!oldType && !newType) continue; // not a game type (the mod's own, or a framework type)
                string verdict;
                if (!newType || after.Count == 0) verdict = "MISSING";
                else if (!before.SequenceEqual(after)) verdict = after.Count(x => x.StartsWith("method ")) > 1 ? "CHANGED, NOW OVERLOADED" : "CHANGED";
                else continue;
                nameIssues++;
                Console.WriteLine($"  {verdict,-24} {t}");
                foreach (var b in before.Except(after)) Console.WriteLine($"      was: {b}");
                foreach (var a in after.Except(before)) Console.WriteLine($"      now: {a}");
            }
            Console.WriteLine(nameIssues == 0 ? $"  {targets.Count} target(s) checked, all unchanged" : $"  {nameIssues} target(s) need a look");
        }

        Console.WriteLine();
        Console.WriteLine("# 3. Game methods the mod calls or patches whose code changed (behaviour may differ)");
        var types = refs.Select(r => Short(r[..r.IndexOf("::", StringComparison.Ordinal)]).Split('<')[0].Split('+')[0]).ToHashSet(StringComparer.Ordinal);
        foreach (var f in focus) types.Add(f.Split("::")[0]);
        IlDiff(oldDll, newDll, new List<Regex>(), types, focus);
        if (sourceDir is null) return broken == 0 ? 4 : 3; // 4: references fine, but the source checks never ran
        return broken + nameIssues == 0 ? 0 : 3;
    }

    // ------------------------------------------------------------------ pck

    private sealed record PckEntry(string Path, long Offset, long Size);

    /// <summary>Directory of a Godot 4 .pck (format 2 or 3). Encrypted packs are not supported.</summary>
    private static List<PckEntry> PckIndex(FileStream f)
    {
        var r = new BinaryReader(f, Encoding.UTF8, leaveOpen: true);
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "GDPC") throw new InvalidDataException("not a Godot pack (no GDPC header)");
        uint format = r.ReadUInt32();
        if (format is < 2 or > 3) throw new InvalidDataException($"pack format {format} isn't supported (only Godot 4 formats 2 and 3)");
        r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32(); // engine major, minor, patch
        uint flags = r.ReadUInt32();
        if ((flags & 1) != 0) throw new InvalidDataException("encrypted pack directory; not supported");
        long fileBase = r.ReadInt64();
        if (format >= 3)
        {
            long dirOffset = r.ReadInt64();
            f.Seek(dirOffset, SeekOrigin.Begin);
        }
        else r.ReadBytes(16 * 4); // reserved
        uint count = r.ReadUInt32();
        var list = new List<PckEntry>((int)Math.Min(count, 1_000_000));
        for (uint i = 0; i < count; i++)
        {
            int len = r.ReadInt32();
            string path = Encoding.UTF8.GetString(r.ReadBytes(len)).TrimEnd('\0');
            long offset = r.ReadInt64();
            long size = r.ReadInt64();
            r.ReadBytes(16); // md5
            if (format >= 2) r.ReadUInt32(); // per-file flags
            list.Add(new PckEntry(path.StartsWith("res://") ? path[6..] : path, offset + fileBase, size));
        }
        return list;
    }

    private static void PckList(string pck, string pattern)
    {
        using var f = File.OpenRead(pck);
        var rx = pattern is null ? null : new Regex(pattern);
        foreach (var e in PckIndex(f).Where(e => rx is null || rx.IsMatch(e.Path)))
            Console.WriteLine($"{e.Path}\t{e.Size}");
    }

    private static void PckExtract(string pck, string pattern, string outDir)
    {
        using var f = File.OpenRead(pck);
        var rx = new Regex(pattern);
        int n = 0;
        foreach (var e in PckIndex(f).Where(e => rx.IsMatch(e.Path)))
        {
            string root = Path.GetFullPath(outDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string dest = Path.GetFullPath(Path.Combine(root, e.Path));
            if (!dest.StartsWith(root, StringComparison.Ordinal)) continue; // no escaping the folder
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            f.Seek(e.Offset, SeekOrigin.Begin);
            using var o = File.Create(dest);
            var buf = new byte[81920];
            long left = e.Size;
            while (left > 0)
            {
                int got = f.Read(buf, 0, (int)Math.Min(buf.Length, left));
                if (got <= 0) throw new InvalidDataException($"pack ended early in {e.Path}");
                o.Write(buf, 0, got);
                left -= got;
            }
            n++;
        }
        Console.WriteLine($"{n} file(s) extracted to {outDir}");
    }
}
