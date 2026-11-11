using System.Security.Cryptography;
using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length < 2)
    throw new ArgumentException("Usage: patch <original.dll> <output.dll> <AlienTextFlow.dll> | verify <patched.dll>");
if (args[0] == "verify")
{
    using var module = ModuleDefinition.ReadModule(args[1]);
    Verify(module);
    Console.WriteLine("Verified: bootstrap, initialization and per-label hooks; original text layout preserved.");
    return;
}
if (args.Length != 4 || args[0] != "patch") throw new ArgumentException("Expected patch and three paths.");
using var input = ModuleDefinition.ReadModule(args[1]);
if (input.AssemblyReferences.Any(r => r.Name == "AlienTextFlow"))
    throw new InvalidOperationException("Already patched. Supply the verified original backup, not a patched assembly.");
using var runtime = ModuleDefinition.ReadModule(args[3]);
var runtimeType = runtime.GetType("AlienTextFlow.Runtime") ?? throw new Exception("Runtime class missing.");
MethodReference Import(string name) => input.ImportReference(runtimeType.Methods.Single(m => m.Name == name));
var draw = input.GetType("Draw") ?? throw new Exception("Unsupported game: Draw missing.");
var character = input.GetType("AlienCharacter") ?? throw new Exception("Unsupported game: AlienCharacter missing.");
var awake = draw.Methods.Single(m => m.Name == "Awake" && m.Parameters.Count == 0);
var updatePos = character.Methods.Single(m => m.Name == "UpdatePos" && m.Parameters.Count == 0);
if (!updatePos.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "set_fontSizeMax"))
    throw new Exception("Unsupported game: UpdatePos sizing pattern changed.");
var first = awake.Body.Instructions[0];
var il = awake.Body.GetILProcessor();
il.InsertBefore(first, Instruction.Create(OpCodes.Ldarg_0));
il.InsertBefore(first, Instruction.Create(OpCodes.Call, Import("Bootstrap")));
il.InsertBefore(first, Instruction.Create(OpCodes.Brfalse, first));
il.InsertBefore(first, Instruction.Create(OpCodes.Ret));
Postfix(awake, Import("Ready"), skipFirstReturn: true);
Postfix(updatePos, Import("Apply"));
foreach (var instruction in updatePos.Body.Instructions)
{
    if (instruction.Operand is not MethodReference method) continue;
    if (method.Name == "set_textWrappingMode")
    {
        instruction.OpCode = OpCodes.Call;
        instruction.Operand = Import("SetWrapping");
    }
    if (method.Name == "set_fontSizeMax")
    {
        instruction.OpCode = OpCodes.Call;
        instruction.Operand = Import("SetMaximum");
    }
}
// Added IL can push existing short branches past sbyte range. Expand every
// short branch before emitting, including conditional branches to an early ret.
foreach (var method in new[] { awake, updatePos })
    foreach (var instruction in method.Body.Instructions)
        if (LongBranch(instruction.OpCode) is { } op) instruction.OpCode = op;
input.Write(args[2]);
using (var check = ModuleDefinition.ReadModule(args[2]))
using (var originalCheck = ModuleDefinition.ReadModule(args[1]))
{
    Verify(check);
    var changed = new List<string>();
    foreach (var type in originalCheck.Types)
    foreach (var method in type.Methods.Where(m => m.HasBody))
    {
        var emitted = check.GetType(type.FullName).Methods.Single(m => m.FullName == method.FullName);
        if (method.Body.Instructions.Select(i => i.ToString()).SequenceEqual(emitted.Body.Instructions.Select(i => i.ToString()))) continue;
        if (!((type.Name == "Draw" && method.Name == "Awake") || (type.Name == "AlienCharacter" && method.Name == "UpdatePos")))
            throw new Exception("Unexpected change outside the two rendering hooks: " + method.FullName);
        changed.Add(type.Name + "." + method.Name);
    }
    Console.WriteLine("Isolated changes: " + string.Join(", ", changed) + "; save and layout methods unchanged.");
}
Console.WriteLine(JsonSerializer.Serialize(new { original = Hash(args[1]), patched = Hash(args[2]), runtime = Hash(args[3]), hooks = 5 }));

static void Postfix(MethodDefinition method, MethodReference callback, bool skipFirstReturn = false)
{
    var returns = method.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray();
    var il = method.Body.GetILProcessor();
    foreach (var ret in returns.Skip(skipFirstReturn ? 1 : 0))
    {
        // Preserve branch targets by turning the existing ret into ldarg.0.
        ret.OpCode = OpCodes.Ldarg_0;
        var call = Instruction.Create(OpCodes.Call, callback);
        il.InsertAfter(ret, call);
        il.InsertAfter(call, Instruction.Create(OpCodes.Ret));
    }
}

static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

static void Verify(ModuleDefinition module)
{
    foreach (var name in new[] { "Bootstrap", "Ready", "Apply", "SetWrapping", "SetMaximum" })
    {
        if (!module.Types.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
            .Any(i => i.Operand is MethodReference called && called.DeclaringType.FullName == "AlienTextFlow.Runtime" && called.Name == name))
            throw new Exception("Missing hook: " + name);
    }
    foreach (var method in module.Types.SelectMany(t => t.Methods).Where(m => m.HasBody))
        foreach (var i in method.Body.Instructions)
            if (i.Operand is Instruction target && !method.Body.Instructions.Contains(target))
                throw new Exception("Invalid branch in " + method.FullName);
    var forbidden = module.GetType("Draw").Methods.Where(m => m.Name == "Write");
    if (forbidden.Any(m => m.Body.Instructions.Any(i => i.Operand is MethodReference called && called.DeclaringType.FullName == "AlienTextFlow.Runtime")))
        throw new Exception("The renderer/grid must not be patched.");
}

static OpCode? LongBranch(OpCode op) => op.Code switch
{
    Code.Br_S => OpCodes.Br, Code.Brfalse_S => OpCodes.Brfalse, Code.Brtrue_S => OpCodes.Brtrue,
    Code.Beq_S => OpCodes.Beq, Code.Bne_Un_S => OpCodes.Bne_Un, Code.Bge_S => OpCodes.Bge,
    Code.Bge_Un_S => OpCodes.Bge_Un, Code.Bgt_S => OpCodes.Bgt, Code.Bgt_Un_S => OpCodes.Bgt_Un,
    Code.Ble_S => OpCodes.Ble, Code.Ble_Un_S => OpCodes.Ble_Un, Code.Blt_S => OpCodes.Blt,
    Code.Blt_Un_S => OpCodes.Blt_Un, Code.Leave_S => OpCodes.Leave, _ => null
};
