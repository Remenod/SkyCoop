using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// usage: Il2CppDump <assembly.dll> <TypeName|Outer/Nested> [...]
// Lists properties (Il2CppInterop exposes game fields as properties), methods, enum values and nested types.
var path = args[0];
using var fs = File.OpenRead(path);
using var pe = new PEReader(fs);
var md = pe.GetMetadataReader();
var wanted = new HashSet<string>(args.Skip(1));

string FullName(TypeDefinition t)
{
    var name = md.GetString(t.Name);
    var decl = t.GetDeclaringType();
    return decl.IsNil ? name : FullName(md.GetTypeDefinition(decl)) + "/" + name;
}

foreach (var th in md.TypeDefinitions)
{
    var t = md.GetTypeDefinition(th);
    var full = FullName(t);
    if (!wanted.Contains(full) && !wanted.Contains(md.GetString(t.Name))) continue;
    Console.WriteLine($"== {md.GetString(t.Namespace)}.{full}");
    var pprov = new SigProvider(md);
    var props = t.GetProperties().Select(p =>
    {
        var pd = md.GetPropertyDefinition(p);
        string type;
        try { type = pd.DecodeSignature(pprov, null).ReturnType; } catch { type = "?"; }
        return type + " " + md.GetString(pd.Name);
    }).ToList();
    if (props.Count > 0) Console.WriteLine("  props: " + string.Join(", ", props));
    var provider = new SigProvider(md);
    var methods = new List<string>();
    foreach (var mh in t.GetMethods())
    {
        var m = md.GetMethodDefinition(mh);
        var n = md.GetString(m.Name);
        if (n.StartsWith("get_") || n.StartsWith("set_") || n == ".cctor" || n == ".ctor") continue;
        string sig;
        try
        {
            var dec = m.DecodeSignature(provider, null);
            var pnames = m.GetParameters().Select(ph => md.GetParameter(ph)).Where(pp => pp.SequenceNumber > 0)
                .OrderBy(pp => pp.SequenceNumber).Select(pp => md.GetString(pp.Name)).ToList();
            var ps = dec.ParameterTypes.Select((pt, i) => pt + " " + (i < pnames.Count ? pnames[i] : "")).ToList();
            bool isStatic = (m.Attributes & System.Reflection.MethodAttributes.Static) != 0;
            sig = $"{(isStatic ? "static " : "")}{dec.ReturnType} {n}({string.Join(", ", ps)})";
        }
        catch { sig = n + "(?)"; }
        methods.Add(sig);
    }
    if (methods.Count > 0) Console.WriteLine("  methods:\n    " + string.Join("\n    ", methods.Distinct()));
    var consts = new List<string>();
    foreach (var fh in t.GetFields())
    {
        var f = md.GetFieldDefinition(fh);
        var c = f.GetDefaultValue();
        if (c.IsNil) continue;
        var blob = md.GetBlobReader(md.GetConstant(c).Value);
        object v = md.GetConstant(c).TypeCode switch
        {
            ConstantTypeCode.Int32 => blob.ReadInt32(),
            ConstantTypeCode.UInt32 => blob.ReadUInt32(),
            ConstantTypeCode.Int16 => blob.ReadInt16(),
            ConstantTypeCode.Byte => blob.ReadByte(),
            ConstantTypeCode.Int64 => blob.ReadInt64(),
            _ => "?"
        };
        consts.Add($"{md.GetString(f.Name)}={v}");
    }
    if (consts.Count > 0) Console.WriteLine("  consts: " + string.Join(", ", consts));
    var nested = t.GetNestedTypes().Select(n => md.GetString(md.GetTypeDefinition(n).Name)).ToList();
    if (nested.Count > 0) Console.WriteLine("  nested: " + string.Join(", ", nested));
}

class SigProvider : ISignatureTypeProvider<string, object>
{
    readonly MetadataReader md;
    public SigProvider(MetadataReader md) { this.md = md; }
    public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString().ToLowerInvariant();
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => r.GetString(r.GetTypeDefinition(h).Name);
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => r.GetString(r.GetTypeReference(h).Name);
    public string GetTypeFromSpecification(MetadataReader r, object c, TypeSpecificationHandle h, byte k) => "spec";
    public string GetSZArrayType(string e) => e + "[]";
    public string GetArrayType(string e, ArrayShape s) => e + "[,]";
    public string GetByReferenceType(string e) => "ref " + e;
    public string GetPointerType(string e) => e + "*";
    public string GetGenericInstantiation(string g, System.Collections.Immutable.ImmutableArray<string> a) => g.Split('`')[0] + "<" + string.Join(",", a) + ">";
    public string GetGenericTypeParameter(object c, int i) => "T" + i;
    public string GetGenericMethodParameter(object c, int i) => "M" + i;
    public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
    public string GetModifiedType(string m, string u, bool r) => u;
    public string GetPinnedType(string e) => e;
}
