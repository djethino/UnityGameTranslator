// The Unity API floor: does the mod's Core use anything the oldest supported Unity does not have?
//
// 🔴 **Why this exists** (2026-09-22). The mod compiles against a recent Unity, so the compiler only
// ever sees today's API. v0.13.4 named `ColorBlock.selectedColor` and `InputField.onEndEdit` typed as
// `EndEditEvent` — both absent before Unity 2019.1 — and on such a game the first panel threw, the
// window stayed empty and the whole mod stopped. Nothing said so until a player launched an old game.
//
// ⚠ **Names are not enough, and that is the defect it was built for.** `onEndEdit` EXISTS in 2018:
// it returns `SubmitEvent`, not `EndEditEvent`. So every reference is matched on its full signature
// — parameter types, return type, field type — against the floor's assemblies, walking base types.
//
// What it reads:
//   · the built Core (UnityGameTranslator.Core/bin/UnityGameTranslator.Core.dll) — the mod's own code.
//     Not the merged DLL: UniverseLib is written to adapt to every Unity version at run time, and
//     TextMeshPro follows the game, not the Unity version;
//   · extlibs/UnityFloor/<version>/ — UnityEngine*.dll and UnityEngine.UI.dll of the floor version
//     (2018.1, the user's decision: the first Unity whose .NET 4 runtime is stable; games on the older
//     .NET 3.5 runtime cannot run the mod at all). Not in git: Unity's files are not redistributable;
//   · allowed.txt — references that are absent from the floor ON PURPOSE, each with its reason (a call
//     kept in a method of its own and guarded by its caller, like the inspector's physics pick).
//
// A reference absent from the floor and not allowed fails. An allowance that no longer matches any
// reference fails too, so the list never keeps excuses for code that is gone.
//
// Usage: dotnet run -c Release [-- <core.dll> <floor folder>]

using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

const string FloorVersion = "2018.1.0f2";

var repo = FindRepo();
var corePath = args.Length > 0 ? args[0] : Path.Combine(repo, "UnityGameTranslator.Core", "bin", "UnityGameTranslator.Core.dll");
var floorPath = args.Length > 1 ? args[1] : Path.Combine(repo, "extlibs", "UnityFloor", FloorVersion);
var allowedPath = Path.Combine(repo, "tests", "UnityGameTranslator.UnityApiFloor", "allowed.txt");

Console.WriteLine($"Unity API floor: Unity {FloorVersion}");

if (!File.Exists(corePath))
    return Fail($"the Core is not built: {corePath}");

if (!Directory.Exists(floorPath) || !File.Exists(Path.Combine(floorPath, "UnityEngine.CoreModule.dll"))
    || !File.Exists(Path.Combine(floorPath, "UnityEngine.UI.dll")))
{
    return Fail($"the floor's assemblies are missing: {floorPath}\n"
                + $"  It needs Unity {FloorVersion}'s UnityEngine.dll, UnityEngine.*Module.dll (a Windows player's Managed\n"
                + "  folder, or the Windows build support package) and UnityEngine.UI.dll (the editor's\n"
                + "  UnityExtensions/Unity/GUISystem/Standalone). Not redistributable, so not in git.");
}

var floor = new Floor(floorPath);
var findings = new SortedSet<string>(StringComparer.Ordinal);

using (var pe = new PEReader(File.OpenRead(corePath)))
{
    var reader = pe.GetMetadataReader();
    var names = new Names();

    // Every type the Core names in a Unity assembly must exist in the floor.
    foreach (var handle in reader.TypeReferences)
    {
        if (!IsUnity(reader, handle)) continue;
        var name = Names.Of(reader, handle);
        if (!floor.HasType(name)) findings.Add($"type {name}");
    }

    // And every member it calls or reads there, with its exact signature — reported with the
    // method of the mod that names it. ⚠ Per method, on purpose: an allowance covers ONE place
    // where the absence is guarded, so the same member named again somewhere unguarded still fails.
    var users = Users.Of(pe, reader);

    foreach (var handle in reader.MemberReferences)
    {
        var member = reader.GetMemberReference(handle);
        if (DeclaringUnityType(reader, member.Parent) is not { } declaring) continue;
        if (!floor.HasType(declaring)) continue; // already reported as a missing type

        var memberName = reader.GetString(member.Name);
        var key = member.GetKind() == MemberReferenceKind.Method
            ? Names.Method(memberName, member.DecodeMethodSignature(names, null))
            : Names.Field(memberName, member.DecodeFieldSignature(names, null));

        if (floor.HasMember(declaring, key)) continue;

        var where = users.TryGetValue(handle, out var methods) ? methods : new SortedSet<string> { "(no method body)" };
        foreach (var user in where) findings.Add($"{declaring}::{key} @ {user}");
    }
}

var allowed = ReadAllowed(allowedPath);
var unexpected = findings.Where(f => !allowed.ContainsKey(f)).ToList();
var stale = allowed.Keys.Where(a => !findings.Contains(a)).ToList();

foreach (var finding in findings.Where(allowed.ContainsKey))
    Console.WriteLine($"  allowed  {finding}  — {allowed[finding]}");

foreach (var finding in unexpected)
    Console.WriteLine($"  MISSING  {finding}");

foreach (var entry in stale)
    Console.WriteLine($"  STALE    {entry}  — allowed in allowed.txt, no longer used: remove the line");

if (unexpected.Count > 0 || stale.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine(unexpected.Count > 0
        ? $"FAILED: the mod uses {unexpected.Count} thing(s) Unity {FloorVersion} does not have. On a game made with it, "
          + "the method naming one of them cannot even load. Reach it by reflection (see Compat), or keep it in a "
          + "method of its own guarded by its caller and list it in allowed.txt with the reason."
        : "FAILED: allowed.txt keeps entries for code that is gone.");
    return 1;
}

Console.WriteLine($"OK: nothing the Core uses is missing from Unity {FloorVersion} ({allowed.Count} allowed on purpose).");
return 0;

static int Fail(string why)
{
    Console.WriteLine($"FAILED: {why}");
    return 1;
}

static string FindRepo()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("The mod's repository was not found above this check.");
}

// A Unity assembly: the engine and its modules, and UnityEngine.UI. Not Unity.TextMeshPro — it
// follows the game's package, not the Unity version.
static bool IsUnity(MetadataReader reader, TypeReferenceHandle handle)
{
    var type = reader.GetTypeReference(handle);
    while (type.ResolutionScope.Kind == HandleKind.TypeReference)
        type = reader.GetTypeReference((TypeReferenceHandle)type.ResolutionScope);

    if (type.ResolutionScope.Kind != HandleKind.AssemblyReference) return false;

    var assembly = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name);
    return assembly == "UnityEngine" || assembly.StartsWith("UnityEngine.", StringComparison.Ordinal);
}

// The Unity type a member reference is declared on — a generic instance counts as its definition,
// since the member's own signature is written against the definition's parameters (!0).
static string? DeclaringUnityType(MetadataReader reader, EntityHandle parent)
{
    switch (parent.Kind)
    {
        case HandleKind.TypeReference:
            var typeRef = (TypeReferenceHandle)parent;
            return IsUnity(reader, typeRef) ? Names.Of(reader, typeRef) : null;

        case HandleKind.TypeSpecification:
            var blob = reader.GetBlobReader(reader.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
            if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return null;
            blob.ReadSignatureTypeCode(); // class or value type
            var generic = blob.ReadTypeHandle();
            return generic.Kind == HandleKind.TypeReference && IsUnity(reader, (TypeReferenceHandle)generic)
                ? Names.Of(reader, (TypeReferenceHandle)generic)
                : null;

        default:
            return null;
    }
}

static Dictionary<string, string> ReadAllowed(string path)
{
    var allowed = new Dictionary<string, string>(StringComparer.Ordinal);
    if (!File.Exists(path)) return allowed;

    foreach (var raw in File.ReadAllLines(path))
    {
        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#')) continue;

        var hash = line.IndexOf("  #", StringComparison.Ordinal);
        var key = (hash < 0 ? line : line[..hash]).Trim();
        var reason = hash < 0 ? "" : line[(hash + 3)..].Trim();
        if (reason.Length == 0) throw new InvalidDataException($"allowed.txt: every entry says why — '{key}' does not.");
        allowed[key] = reason;
    }

    return allowed;
}

/// <summary>
/// Which of the Core's methods name each member reference — read from their IL, opcode by opcode.
/// A call through a generic method instantiation (MethodSpec) counts for the member it instantiates.
/// </summary>
static class Users
{
    private static readonly Dictionary<short, System.Reflection.Emit.OperandType> Operands = BuildOperands();

    public static Dictionary<MemberReferenceHandle, SortedSet<string>> Of(PEReader pe, MetadataReader reader)
    {
        var users = new Dictionary<MemberReferenceHandle, SortedSet<string>>();

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var typeName = Names.Of(reader, typeHandle);

            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0) continue;

                var user = typeName + "." + reader.GetString(method.Name);
                var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader();

                while (il.RemainingBytes > 0)
                {
                    short code = il.ReadByte();
                    if (code == 0xFE) code = (short)(0xFE00 | il.ReadByte());

                    if (!Operands.TryGetValue(code, out var operand))
                        throw new InvalidDataException($"Unknown IL opcode 0x{code:X} in {user}.");

                    switch (operand)
                    {
                        case System.Reflection.Emit.OperandType.InlineMethod:
                        case System.Reflection.Emit.OperandType.InlineField:
                        case System.Reflection.Emit.OperandType.InlineTok:
                            var token = il.ReadInt32();
                            var handle = MetadataTokens.EntityHandle(token);
                            if (handle.Kind == HandleKind.MethodSpecification)
                                handle = reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
                            if (handle.Kind == HandleKind.MemberReference)
                            {
                                var member = (MemberReferenceHandle)handle;
                                if (!users.TryGetValue(member, out var set)) users[member] = set = new SortedSet<string>(StringComparer.Ordinal);
                                set.Add(user);
                            }
                            break;

                        case System.Reflection.Emit.OperandType.InlineSwitch:
                            var count = il.ReadInt32();
                            il.Offset += count * 4;
                            break;

                        default:
                            il.Offset += Size(operand);
                            break;
                    }
                }
            }
        }

        return users;
    }

    private static int Size(System.Reflection.Emit.OperandType operand) => operand switch
    {
        System.Reflection.Emit.OperandType.InlineNone => 0,
        System.Reflection.Emit.OperandType.ShortInlineBrTarget or System.Reflection.Emit.OperandType.ShortInlineI
            or System.Reflection.Emit.OperandType.ShortInlineVar => 1,
        System.Reflection.Emit.OperandType.InlineVar => 2,
        System.Reflection.Emit.OperandType.InlineI8 or System.Reflection.Emit.OperandType.InlineR => 8,
        _ => 4,
    };

    private static Dictionary<short, System.Reflection.Emit.OperandType> BuildOperands()
    {
        var table = new Dictionary<short, System.Reflection.Emit.OperandType>();
        foreach (var field in typeof(System.Reflection.Emit.OpCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (field.GetValue(null) is System.Reflection.Emit.OpCode op) table[op.Value] = op.OperandType;
        }
        return table;
    }
}

/// <summary>The floor's types by full name ("Outer+Inner"), with their members' signatures.</summary>
sealed class Floor
{
    private readonly Dictionary<string, (MetadataReader Reader, TypeDefinitionHandle Handle)> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _members = new(StringComparer.Ordinal);
    private readonly List<PEReader> _open = new();
    private readonly Names _names = new();

    public Floor(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*.dll"))
        {
            var pe = new PEReader(File.OpenRead(file));
            if (!pe.HasMetadata) { pe.Dispose(); continue; }
            _open.Add(pe);

            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.TypeDefinitions)
                _types.TryAdd(Names.Of(reader, handle), (reader, handle));
        }
    }

    public bool HasType(string name) => _types.ContainsKey(name);

    /// <summary>On the type or any of its bases in the floor; System.Object's own members are taken as there.</summary>
    public bool HasMember(string type, string key)
    {
        for (var current = type; current is not null && _types.ContainsKey(current); current = BaseOf(current))
        {
            if (MembersOf(current).Contains(key)) return true;
        }

        return ObjectMembers.Contains(key);
    }

    private static readonly HashSet<string> ObjectMembers = new(StringComparer.Ordinal)
    {
        "ToString():System.String", "GetHashCode():System.Int32", "Equals(System.Object):System.Boolean",
        "GetType():System.Type", ".ctor():System.Void", "MemberwiseClone():System.Object",
    };

    private HashSet<string> MembersOf(string type)
    {
        if (_members.TryGetValue(type, out var known)) return known;

        var (reader, handle) = _types[type];
        var definition = reader.GetTypeDefinition(handle);
        var members = new HashSet<string>(StringComparer.Ordinal);

        foreach (var method in definition.GetMethods())
        {
            var m = reader.GetMethodDefinition(method);
            members.Add(Names.Method(reader.GetString(m.Name), m.DecodeSignature(_names, null)));
        }

        foreach (var field in definition.GetFields())
        {
            var f = reader.GetFieldDefinition(field);
            members.Add(Names.Field(reader.GetString(f.Name), f.DecodeSignature(_names, null)));
        }

        return _members[type] = members;
    }

    private string? BaseOf(string type)
    {
        var (reader, handle) = _types[type];
        var baseType = reader.GetTypeDefinition(handle).BaseType;

        return baseType.Kind switch
        {
            HandleKind.TypeDefinition => Names.Of(reader, (TypeDefinitionHandle)baseType),
            HandleKind.TypeReference => Names.Of(reader, (TypeReferenceHandle)baseType),
            HandleKind.TypeSpecification => GenericDefinitionOf(reader, (TypeSpecificationHandle)baseType),
            _ => null,
        };
    }

    private static string? GenericDefinitionOf(MetadataReader reader, TypeSpecificationHandle spec)
    {
        var blob = reader.GetBlobReader(reader.GetTypeSpecification(spec).Signature);
        if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return null;
        blob.ReadSignatureTypeCode();
        var generic = blob.ReadTypeHandle();
        return generic.Kind switch
        {
            HandleKind.TypeDefinition => Names.Of(reader, (TypeDefinitionHandle)generic),
            HandleKind.TypeReference => Names.Of(reader, (TypeReferenceHandle)generic),
            _ => null,
        };
    }
}

/// <summary>
/// Types written as full names, the same way in the Core and in the floor, whatever assembly each
/// was compiled against: "UnityEngine.UI.InputField+SubmitEvent", "System.Int32",
/// "UnityEngine.Events.UnityEvent`1&lt;System.String&gt;", "!0" for a type's generic parameter.
/// </summary>
sealed class Names : ISignatureTypeProvider<string, object?>
{
    public static string Of(MetadataReader reader, TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        var name = reader.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
            return Of(reader, (TypeReferenceHandle)type.ResolutionScope) + "+" + name;

        var ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public static string Of(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil) return Of(reader, declaring) + "+" + name;

        var ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    public static string Method(string name, MethodSignature<string> signature) =>
        name + (signature.GenericParameterCount > 0 ? $"``{signature.GenericParameterCount}" : "")
             + "(" + string.Join(",", signature.ParameterTypes) + "):" + signature.ReturnType;

    public static string Field(string name, string type) => name + ":" + type;

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Void => "System.Void",
        PrimitiveTypeCode.Boolean => "System.Boolean",
        PrimitiveTypeCode.Char => "System.Char",
        PrimitiveTypeCode.SByte => "System.SByte",
        PrimitiveTypeCode.Byte => "System.Byte",
        PrimitiveTypeCode.Int16 => "System.Int16",
        PrimitiveTypeCode.UInt16 => "System.UInt16",
        PrimitiveTypeCode.Int32 => "System.Int32",
        PrimitiveTypeCode.UInt32 => "System.UInt32",
        PrimitiveTypeCode.Int64 => "System.Int64",
        PrimitiveTypeCode.UInt64 => "System.UInt64",
        PrimitiveTypeCode.Single => "System.Single",
        PrimitiveTypeCode.Double => "System.Double",
        PrimitiveTypeCode.String => "System.String",
        PrimitiveTypeCode.Object => "System.Object",
        PrimitiveTypeCode.IntPtr => "System.IntPtr",
        PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
        PrimitiveTypeCode.TypedReference => "System.TypedReference",
        _ => typeCode.ToString(),
    };

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => Of(reader, handle);
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Of(reader, handle);
    public string GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, context);

    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPinnedType(string elementType) => elementType;
    public string GetGenericInstantiation(string genericType, System.Collections.Immutable.ImmutableArray<string> typeArguments) =>
        genericType + "<" + string.Join(",", typeArguments) + ">";
    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
}
