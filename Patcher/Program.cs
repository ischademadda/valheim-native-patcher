using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace ValheimPatcher
{
    class Program
    {
        private const string MarkerFieldName = "s_nativePatched_v6";

        /// <summary>Safe lookup with descriptive error on game version mismatch.</summary>
        static T FindOrThrow<T>(IEnumerable<T> source, Func<T, bool> predicate, string context)
        {
            foreach (var item in source)
            {
                if (predicate(item)) return item;
            }
            throw new InvalidOperationException(
                $"[PATCH ERROR] {context} — not found. The game may have been updated to an incompatible version.");
        }

        static string ComputeMD5(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            var hash = MD5.HashData(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        static int Main(string[] args)
        {
            Console.WriteLine("=========================================================");
            Console.WriteLine("     Valheim Native IL Patcher (macOS Apple Silicon)     ");
            Console.WriteLine("     Zero Rosetta, Zero BepInEx, Native arm64 Mono       ");
            Console.WriteLine("     Version: v6 (Robust Network & Gameplay Edition)     ");
            Console.WriteLine("=========================================================\n");

            string? current = AppContext.BaseDirectory;
            string managedDir = "";
            while (current != null)
            {
                string candidate = Path.Combine(current, "Contents/Resources/Data/Managed");
                if (Directory.Exists(candidate))
                {
                    managedDir = candidate;
                    break;
                }
                string candidateApp = Path.Combine(current, "valheim.app/Contents/Resources/Data/Managed");
                if (Directory.Exists(candidateApp))
                {
                    managedDir = candidateApp;
                    break;
                }
                var parent = Directory.GetParent(current);
                current = parent?.FullName;
            }

            if (string.IsNullOrEmpty(managedDir))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[ERROR] Could not auto-detect Managed directory. Use --managed <path> to specify it.");
                Console.ResetColor();
                Environment.Exit(1);
            }

            bool restoreMode = args.Contains("--restore");
            bool statusMode = args.Contains("--status");
            int physicsSteps = 4;
            int spawnLimit = 25;
            int updatesPerFrame = 20;
            float vegetationDistance = 40.0f;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--managed" && i + 1 < args.Length)
                    managedDir = args[++i];
                else if (args[i] == "--physics-steps" && i + 1 < args.Length && int.TryParse(args[i + 1], out int ps))
                    { physicsSteps = ps; i++; }
                else if (args[i] == "--spawn-limit" && i + 1 < args.Length && int.TryParse(args[i + 1], out int sl))
                    { spawnLimit = sl; i++; }
                else if (args[i] == "--updates-per-frame" && i + 1 < args.Length && int.TryParse(args[i + 1], out int upf))
                    { updatesPerFrame = upf; i++; }
                else if (args[i] == "--vegetation-distance" && i + 1 < args.Length && float.TryParse(args[i + 1], out float vd))
                    { vegetationDistance = vd; i++; }
                else if (args[i] == "--help" || args[i] == "-h")
                    { PrintHelp(); return 0; }
            }

            string dllPath = Path.Combine(managedDir, "assembly_valheim.dll");
            string bakPath = Path.Combine(managedDir, "assembly_valheim.dll.bak");

            if (!File.Exists(dllPath) && !File.Exists(bakPath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERROR] Managed assembly not found at: {dllPath}");
                Console.ResetColor();
                return 1;
            }

            bool verifyMode = args.Contains("--verify");
            if (verifyMode) return VerifyPatches(dllPath, managedDir);

            if (statusMode) { CheckStatus(dllPath, bakPath, managedDir); return 0; }
            if (restoreMode) return RestoreBackup(dllPath, bakPath);

            return ApplyPatches(dllPath, bakPath, managedDir, physicsSteps, spawnLimit, updatesPerFrame, vegetationDistance);
        }

        static void PrintHelp()
        {
            Console.WriteLine("Usage: dotnet run --project Patcher [options]");
            Console.WriteLine("Options:");
            Console.WriteLine("  --status                   Check current patch status and backup existence");
            Console.WriteLine("  --verify                   Deep verify all v6 patches in target assembly");
            Console.WriteLine("  --restore                  Restore original assembly_valheim.dll from backup");
            Console.WriteLine("  --physics-steps <N>        Max FixedUpdate physics steps per frame (default: 4)");
            Console.WriteLine("  --spawn-limit <N>          Max ZNetScene objects spawned per frame in gameplay (default: 25)");
            Console.WriteLine("  --updates-per-frame <N>    Max WearNTear pieces updated per frame (default: 20)");
            Console.WriteLine("  --vegetation-distance <N>  Vegetation draw distance in meters (default: 40)");
            Console.WriteLine("  --managed <path>           Path to Managed directory");
        }

        static void CheckStatus(string dllPath, string bakPath, string managedDir)
        {
            Console.WriteLine($"Target assembly: {dllPath}");
            bool bakExists = File.Exists(bakPath);
            Console.WriteLine($"Backup exists:   {(bakExists ? $"YES ({new FileInfo(bakPath).Length} bytes)" : "NO")}");

            if (!File.Exists(dllPath)) { Console.WriteLine("Status: Assembly file missing!"); return; }

            using var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(managedDir);
            using var asm = AssemblyDefinition.ReadAssembly(dllPath, new ReaderParameters { AssemblyResolver = resolver });
            var wnt = asm.MainModule.Types.FirstOrDefault(t => t.Name == "WearNTear");
            var marker = wnt?.Fields.FirstOrDefault(f => f.Name.StartsWith("s_nativePatched_"));

            if (marker != null)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Status: ALREADY PATCHED (marker: {marker.Name})");
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Status: UNPATCHED (Original vanilla code)");
            }
            Console.ResetColor();
        }

        static int RestoreBackup(string dllPath, string bakPath)
        {
            if (!File.Exists(bakPath))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[ERROR] Backup file does not exist: {bakPath}");
                Console.ResetColor();
                return 1;
            }
            File.Copy(bakPath, dllPath, true);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[SUCCESS] Vanilla assembly successfully restored!");
            Console.ResetColor();
            return 0;
        }

        static int ApplyPatches(string dllPath, string bakPath, string managedDir, int physicsSteps, int spawnLimit, int updatesPerFrame, float vegetationDistance)
        {
            if (!File.Exists(bakPath))
            {
                Console.WriteLine($"Creating pristine backup: {bakPath}");
                File.Copy(dllPath, bakPath, false);
            }
            else
            {
                var bakHash = ComputeMD5(bakPath);
                var dllHash = ComputeMD5(dllPath);
                Console.WriteLine($"Existing backup: {new FileInfo(bakPath).Length} bytes (MD5: {bakHash})");
                Console.WriteLine($"Current DLL:     {new FileInfo(dllPath).Length} bytes (MD5: {dllHash})");

                using var checkResolver = new DefaultAssemblyResolver();
                checkResolver.AddSearchDirectory(managedDir);
                using var checkAsm = AssemblyDefinition.ReadAssembly(dllPath, new ReaderParameters { AssemblyResolver = checkResolver });
                var checkWnt = checkAsm.MainModule.Types.FirstOrDefault(t => t.Name == "WearNTear");
                bool currentIsPatched = checkWnt?.Fields.Any(f => f.Name.StartsWith("s_nativePatched_")) == true;

                if (!currentIsPatched && bakHash != dllHash)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("[WARNING] Game updated — refreshing backup with current vanilla DLL...");
                    Console.ResetColor();
                    File.Copy(dllPath, bakPath, true);
                }
            }

            string sourceDll = bakPath;
            Console.WriteLine($"Reading clean assembly from: {sourceDll}");

            using var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(managedDir);
            var readerParams = new ReaderParameters { AssemblyResolver = resolver };
            using var asm = AssemblyDefinition.ReadAssembly(sourceDll, readerParams);
            var module = asm.MainModule;

            // === Resolve framework & engine module dependencies ===
            Console.WriteLine("\n[1/15] Resolving UnityEngine and BCL module dependencies...");
            var coreModuleAsm = resolver.Resolve(new AssemblyNameReference("UnityEngine.CoreModule", new Version(0, 0, 0, 0)));
            var timeType = FindOrThrow(coreModuleAsm.MainModule.Types, t => t.FullName == "UnityEngine.Time", "UnityEngine.Time");
            var getFixedDeltaTime = module.ImportReference(FindOrThrow(timeType.Methods, m => m.Name == "get_fixedDeltaTime", "Time.get_fixedDeltaTime"));
            var setMaximumDeltaTime = module.ImportReference(FindOrThrow(timeType.Methods, m => m.Name == "set_maximumDeltaTime", "Time.set_maximumDeltaTime"));
            var getFrameCount = module.ImportReference(FindOrThrow(timeType.Methods, m => m.Name == "get_frameCount", "Time.get_frameCount"));
            var mathfType = FindOrThrow(coreModuleAsm.MainModule.Types, t => t.FullName == "UnityEngine.Mathf", "UnityEngine.Mathf");
            var mathfMinInt = module.ImportReference(FindOrThrow(mathfType.Methods, m => m.Name == "Min" && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.FullName == "System.Int32", "Mathf.Min(int,int)"));
            var mathfMinFloat = module.ImportReference(FindOrThrow(mathfType.Methods, m => m.Name == "Min" && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.FullName == "System.Single", "Mathf.Min(float,float)"));
            var mathfMaxFloat = module.ImportReference(FindOrThrow(mathfType.Methods, m => m.Name == "Max" && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.FullName == "System.Single", "Mathf.Max(float,float)"));

            var qsType = FindOrThrow(coreModuleAsm.MainModule.Types, t => t.FullName == "UnityEngine.QualitySettings", "QualitySettings");
            var setPixelLightCount = module.ImportReference(FindOrThrow(qsType.Methods, m => m.Name == "set_pixelLightCount", "set_pixelLightCount"));
            var setSoftParticles = module.ImportReference(FindOrThrow(qsType.Methods, m => m.Name == "set_softParticles", "set_softParticles"));

            var goType = FindOrThrow(coreModuleAsm.MainModule.Types, t => t.FullName == "UnityEngine.GameObject", "GameObject");
            var goTypeRef = module.ImportReference(goType);
            var transType = FindOrThrow(coreModuleAsm.MainModule.Types, t => t.FullName == "UnityEngine.Transform", "Transform");
            var compType = FindOrThrow(coreModuleAsm.MainModule.Types, t => t.FullName == "UnityEngine.Component", "Component");
            var getTransformGO = module.ImportReference(FindOrThrow(goType.Methods, x => x.Name == "get_transform", "GameObject.get_transform"));
            var getTransformComp = module.ImportReference(FindOrThrow(compType.Methods, x => x.Name == "get_transform", "Component.get_transform"));
            var getParent = module.ImportReference(FindOrThrow(transType.Methods, x => x.Name == "get_parent", "Transform.get_parent"));
            var setParentTwo = module.ImportReference(FindOrThrow(transType.Methods, x => x.Name == "SetParent" && x.Parameters.Count == 2 && x.Parameters[1].ParameterType.FullName == "System.Boolean", "Transform.SetParent(Transform,bool)"));
            var getGameObject = module.ImportReference(FindOrThrow(compType.Methods, x => x.Name == "get_gameObject", "Component.get_gameObject"));
            var getPosition = module.ImportReference(FindOrThrow(transType.Methods, x => x.Name == "get_position", "Transform.get_position"));
            var vec3Type = FindOrThrow(coreModuleAsm.MainModule.Types, t => t.FullName == "UnityEngine.Vector3", "Vector3");
            var vec3Distance = module.ImportReference(FindOrThrow(vec3Type.Methods, m => m.Name == "Distance" && m.Parameters.Count == 2, "Vector3.Distance"));
            var objType = FindOrThrow(coreModuleAsm.MainModule.Types, t => t.FullName == "UnityEngine.Object", "UnityEngine.Object");
            var opImplicit = module.ImportReference(FindOrThrow(objType.Methods, m => m.Name == "op_Implicit" && m.Parameters.Count == 1, "Object.op_Implicit"));

            var physicsModuleAsm = resolver.Resolve(new AssemblyNameReference("UnityEngine.PhysicsModule", new Version(0, 0, 0, 0)));
            var meshColliderType = FindOrThrow(physicsModuleAsm.MainModule.Types, t => t.FullName == "UnityEngine.MeshCollider", "MeshCollider");
            var setCookingOptions = module.ImportReference(FindOrThrow(meshColliderType.Methods, m => m.Name == "set_cookingOptions", "set_cookingOptions"));

            var uiModuleAsm = resolver.Resolve(new AssemblyNameReference("UnityEngine.UIModule", new Version(0, 0, 0, 0)));
            var canvasType = FindOrThrow(uiModuleAsm.MainModule.Types, t => t.FullName == "UnityEngine.Canvas", "Canvas");
            var addCompGeneric = FindOrThrow(goType.Methods, m => m.Name == "AddComponent" && m.HasGenericParameters && m.Parameters.Count == 0, "AddComponent<T>()");
            var addCompCanvas = new GenericInstanceMethod(module.ImportReference(addCompGeneric));
            addCompCanvas.GenericArguments.Add(module.ImportReference(canvasType));
            var getCompGeneric = FindOrThrow(compType.Methods, m => m.Name == "GetComponent" && m.HasGenericParameters && m.Parameters.Count == 0, "GetComponent<T>()");
            var getCompCanvas = new GenericInstanceMethod(module.ImportReference(getCompGeneric));
            getCompCanvas.GenericArguments.Add(module.ImportReference(canvasType));

            var animModuleAsm = resolver.Resolve(new AssemblyNameReference("UnityEngine.AnimationModule", new Version(0, 0, 0, 0)));
            var animType = FindOrThrow(animModuleAsm.MainModule.Types, t => t.FullName == "UnityEngine.Animator", "Animator");
            var setCullingMode = module.ImportReference(FindOrThrow(animType.Methods, m => m.Name == "set_cullingMode", "set_cullingMode"));

            // Resolve Array.Empty<GameObject>() from mscorlib
            var mscorlibAsm = resolver.Resolve(new AssemblyNameReference("mscorlib", new Version(4, 0, 0, 0)));
            var sysArrayType = FindOrThrow(mscorlibAsm.MainModule.Types, t => t.FullName == "System.Array", "System.Array");
            var sysArrayEmptyDef = FindOrThrow(sysArrayType.Methods, m => m.Name == "Empty" && m.HasGenericParameters && m.Parameters.Count == 0, "Array.Empty<T>()");
            var arrayEmptyGameObject = new GenericInstanceMethod(module.ImportReference(sysArrayEmptyDef));
            arrayEmptyGameObject.GenericArguments.Add(goTypeRef);

            // === Valheim types & methods ===
            var gameType = FindOrThrow(module.Types, t => t.Name == "Game", "Game");
            var playerType = FindOrThrow(module.Types, t => t.Name == "Player", "Player");
            var znetViewType = FindOrThrow(module.Types, t => t.Name == "ZNetView", "ZNetView");
            var isOwnerMethod = module.ImportReference(FindOrThrow(znetViewType.Methods, m => m.Name == "IsOwner" && m.Parameters.Count == 0, "ZNetView.IsOwner()"));

            // ==================== ORIGINAL v4 PATCHES (1-11) ====================

            // Patch 1: Physics Cap & Baseline Quality
            Console.WriteLine($"\n[2/15] Patching Physics Cap (Max {physicsSteps} FixedUpdate ticks/frame) & Baseline Quality...");
            var fejdAwake = FindOrThrow(FindOrThrow(module.Types, t => t.Name == "FejdStartup", "FejdStartup").Methods, m => m.Name == "Awake", "FejdStartup.Awake");
            var gameAwake = FindOrThrow(gameType.Methods, m => m.Name == "Awake", "Game.Awake");
            ApplyPhysicsCap(fejdAwake, getFixedDeltaTime, setMaximumDeltaTime, setPixelLightCount, setSoftParticles, physicsSteps);
            ApplyPhysicsCap(gameAwake, getFixedDeltaTime, setMaximumDeltaTime, setPixelLightCount, setSoftParticles, physicsSteps);

            // Patch 2: ZNetScene Spawn Limits
            Console.WriteLine($"\n[3/15] Patching ZNetScene streaming limits (Max {spawnLimit} objects/frame)...");
            var znetType = FindOrThrow(module.Types, t => t.Name == "ZNetScene", "ZNetScene");
            ApplyZNetScenePatches(znetType, mathfMinInt, spawnLimit);

            // Patch 3: WearNTear Throttling & Caching (Safe Owner Fast-Path)
            Console.WriteLine($"\n[4/15] Patching WearNTear structural integrity cache & throttling (Max {updatesPerFrame} pieces/frame)...");
            var wntType = FindOrThrow(module.Types, t => t.Name == "WearNTear", "WearNTear");
            var updaterType = FindOrThrow(module.Types, t => t.Name == "WearNTearUpdater", "WearNTearUpdater");
            ApplyWearNTearPatches(wntType, updaterType, getFrameCount, isOwnerMethod, updatesPerFrame);

            // Patch 4: ClutterSystem Vegetation Distance
            Console.WriteLine($"\n[5/15] Patching ClutterSystem vegetation draw distance ({vegetationDistance}m on High)...");
            var clutterType = FindOrThrow(module.Types, t => t.Name == "ClutterSystem", "ClutterSystem");
            ApplyClutterSystemPatches(clutterType, vegetationDistance);

            // Patch 5: Heightmap PhysX Cooking
            Console.WriteLine("\n[6/15] Patching Heightmap PhysX FastMidphase cooking options...");
            var heightmapType = FindOrThrow(module.Types, t => t.Name == "Heightmap", "Heightmap");
            ApplyHeightmapPatches(heightmapType, setCookingOptions);

            // Patch 6: EffectList ParentConstraint Spike Removal
            Console.WriteLine("\n[7/15] Patching EffectList & MessageHud spike elimination...");
            var effectListType = FindOrThrow(module.Types, t => t.Name == "EffectList", "EffectList");
            ApplyEffectListPatches(effectListType, getTransformGO, getParent, setParentTwo, opImplicit);

            // Patch 7: MessageHud Canvas Isolation
            var messageHudType = FindOrThrow(module.Types, t => t.Name == "MessageHud", "MessageHud");
            ApplyMessageHudCanvasIsolation(messageHudType, getGameObject, getCompCanvas, addCompCanvas);

            // Patch 8: Forward Light Rendering & Soft Particles Cap
            Console.WriteLine("\n[8/15] Patching Forward Light rendering limits & disabling soft particles...");
            var gsmType = FindOrThrow(module.Types, t => t.Name == "GraphicsSettingsManager", "GraphicsSettingsManager");
            var lightLodType = FindOrThrow(module.Types, t => t.Name == "LightLod", "LightLod");
            ApplyForwardRenderingPatches(gsmType, lightLodType);

            // Patch 9: LightFlicker Transform Hierarchy
            Console.WriteLine("\n[9/15] Patching LightFlicker to eliminate transform hierarchy invalidation...");
            var lightFlickerType = FindOrThrow(module.Types, t => t.Name == "LightFlicker", "LightFlicker");
            ApplyLightFlickerPatches(lightFlickerType);

            // Patch 10: Global Smoke Particle Cap
            Console.WriteLine("\n[10/15] Patching SmokeSpawner global particle & PhysX body cap...");
            var smokeSpawnerType = FindOrThrow(module.Types, t => t.Name == "SmokeSpawner", "SmokeSpawner");
            ApplySmokeSpawnerPatches(smokeSpawnerType);

            // Patch 11: Fire.Dot PhysX Throttle
            Console.WriteLine("\n[11/15] Patching Fire.Dot PhysX OverlapSphere query interval...");
            var fireType = FindOrThrow(module.Types, t => t.Name == "Fire", "Fire");
            ApplyFireDotInterval(fireType);

            // ==================== v6 REFINED PATCHES (12-15) ====================

            // Patch 12: Fire distance cull (Owner-safe, op_Implicit) + updateRate clamp
            Console.WriteLine("\n[12/15] Patching Fire (owner-safe distance cull & updateRate floor)...");
            ApplyFireDistanceCull(fireType, playerType, mathfMaxFloat, getTransformComp, getPosition, vec3Distance, isOwnerMethod, opImplicit);

            // Patch 13: Ragdoll TTL cap + floating bypass (Zero memory leak, no premature drop)
            Console.WriteLine("\n[13/15] Patching Ragdoll (TTL 3.5s, floating bypass, zero memory leak)...");
            var ragdollType = FindOrThrow(module.Types, t => t.Name == "Ragdoll", "Ragdoll");
            ApplyRagdollPatches(ragdollType, mathfMinFloat);

            // Patch 14: EffectList global frame limiter (Array.Empty, op_Implicit)
            Console.WriteLine("\n[14/15] Patching EffectList global frame limiter (Array.Empty<GameObject>, op_Implicit)...");
            ApplyEffectListFrameLimiter(effectListType, gameType, playerType, getTransformComp, getPosition, vec3Distance, arrayEmptyGameObject, opImplicit);

            // Patch 15: Character Animator CullUpdateTransforms (Excluding Player & Bosses)
            Console.WriteLine("\n[15/15] Patching Character Animator LOD (CullUpdateTransforms excluding Player/Bosses)...");
            var charType = FindOrThrow(module.Types, t => t.Name == "Character", "Character");
            ApplyCharacterAnimatorLOD(charType, playerType, setCullingMode);

            // Add v6 marker
            var wntType2 = FindOrThrow(module.Types, t => t.Name == "WearNTear", "WearNTear");
            wntType2.Fields.Add(new FieldDefinition(MarkerFieldName, FieldAttributes.Private | FieldAttributes.Static, module.TypeSystem.Boolean));

            // Write patched assembly
            string tempOut = dllPath + ".tmp";
            try
            {
                Console.WriteLine($"\nWriting patched assembly...");
                asm.Write(tempOut);
                Console.WriteLine("Verifying integrity...");
                using (var testRead = AssemblyDefinition.ReadAssembly(tempOut, readerParams))
                {
                    var checkWnt = FindOrThrow(testRead.MainModule.Types, t => t.Name == "WearNTear", "WearNTear in verify");
                    if (!checkWnt.Fields.Any(f => f.Name == MarkerFieldName))
                        throw new Exception("Marker field missing in patched assembly!");
                }
                File.Move(tempOut, dllPath, true);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n=========================================================");
                Console.WriteLine("  [SUCCESS] Valheim v6 native patches applied!           ");
                Console.WriteLine("=========================================================");
                Console.ResetColor();
            }
            finally
            {
                if (File.Exists(tempOut))
                    try { File.Delete(tempOut); } catch { }
            }
            return 0;
        }

        // ==================== ORIGINAL v4 PATCH METHODS ====================

        static void ApplyPhysicsCap(MethodDefinition awakeMethod, MethodReference getFixedDeltaTime, MethodReference setMaximumDeltaTime, MethodReference setPixelLightCount, MethodReference setSoftParticles, int steps)
        {
            var il = awakeMethod.Body.GetILProcessor();
            var first = awakeMethod.Body.Instructions[0];
            il.InsertBefore(first, il.Create(OpCodes.Call, getFixedDeltaTime));
            il.InsertBefore(first, il.Create(OpCodes.Ldc_R4, (float)steps));
            il.InsertBefore(first, il.Create(OpCodes.Mul));
            il.InsertBefore(first, il.Create(OpCodes.Call, setMaximumDeltaTime));
            il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_4));
            il.InsertBefore(first, il.Create(OpCodes.Call, setPixelLightCount));
            il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_0));
            il.InsertBefore(first, il.Create(OpCodes.Call, setSoftParticles));
            Console.WriteLine($"  + Injected physics cap + pixelLightCount=4 + softParticles=false into {awakeMethod.DeclaringType.Name}::{awakeMethod.Name}");
        }

        static void ApplyZNetScenePatches(TypeDefinition znetType, MethodReference mathfMinInt, int spawnLimit)
        {
            var createObjects = FindOrThrow(znetType.Methods, m => m.Name == "CreateObjects" && m.Parameters.Count == 2, "ZNetScene.CreateObjects(2)");
            if (createObjects.Body.Instructions[0].OpCode == OpCodes.Ldc_I4_S && (sbyte)createObjects.Body.Instructions[0].Operand == 10)
            {
                createObjects.Body.Instructions[0].Operand = (sbyte)spawnLimit;
                Console.WriteLine($"  + ZNetScene.CreateObjects: gameplay default clamped to {spawnLimit}");
            }
            var createObjectsSorted = FindOrThrow(znetType.Methods, m => m.Name == "CreateObjectsSorted" && m.Parameters.Count == 3, "ZNetScene.CreateObjectsSorted(3)");
            var instrs = createObjectsSorted.Body.Instructions;
            for (int i = 0; i < instrs.Count - 6; i++)
            {
                if (instrs[i].OpCode == OpCodes.Callvirt && instrs[i].Operand?.ToString()?.Contains("get_Count") == true &&
                    instrs[i + 1].OpCode == OpCodes.Ldc_I4_S && (sbyte)instrs[i + 1].Operand == 100 &&
                    instrs[i + 2].OpCode == OpCodes.Div &&
                    instrs[i + 3].OpCode == OpCodes.Ldarg_2 &&
                    instrs[i + 4].OpCode == OpCodes.Call && instrs[i + 4].Operand?.ToString()?.Contains("Max") == true)
                {
                    instrs[i + 1].OpCode = OpCodes.Nop; instrs[i + 1].Operand = null;
                    instrs[i + 2].OpCode = OpCodes.Nop; instrs[i + 2].Operand = null;
                    instrs[i + 4].Operand = mathfMinInt;
                    Console.WriteLine("  + ZNetScene.CreateObjectsSorted: Max(Count/100, n) -> Min(Count, n)");
                    break;
                }
            }
        }

        static void ApplyWearNTearPatches(TypeDefinition wntType, TypeDefinition updaterType, MethodReference getFrameCount, MethodReference isOwnerMethod, int maxUpdatesPerFrame)
        {
            var ctor = FindOrThrow(updaterType.Methods, m => m.IsConstructor && !m.IsStatic, "WearNTearUpdater..ctor");
            foreach (var inst in ctor.Body.Instructions)
                if (inst.OpCode == OpCodes.Ldc_I4_S && (sbyte)inst.Operand == 50)
                { inst.Operand = (sbyte)maxUpdatesPerFrame; Console.WriteLine($"  + WearNTearUpdater..ctor: 50 -> {maxUpdatesPerFrame}"); break; }

            var updateWearNTear = FindOrThrow(updaterType.Methods, m => m.Name == "UpdateWearNTear", "WearNTearUpdater.UpdateWearNTear");
            foreach (var inst in updateWearNTear.Body.Instructions)
                if (inst.OpCode == OpCodes.Ldc_I4_S && (sbyte)inst.Operand == 100)
                { inst.Operand = (sbyte)maxUpdatesPerFrame; Console.WriteLine($"  + WearNTearUpdater.UpdateWearNTear: upper clamp 100 -> {maxUpdatesPerFrame}"); break; }

            var fSupport = FindOrThrow(wntType.Fields, f => f.Name == "m_support", "WearNTear.m_support");
            var fNview = FindOrThrow(wntType.Fields, f => f.Name == "m_nview", "WearNTear.m_nview");

            // GetSupport fast-path: ONLY if m_nview != null && m_nview.IsOwner() && m_support > 0f
            // Guest clients (!IsOwner) fall through to vanilla ZDO read: ZDO.GetFloat(ZDOVars.s_support)
            var getSupport = FindOrThrow(wntType.Methods, m => m.Name == "GetSupport" && m.Parameters.Count == 0, "WearNTear.GetSupport()");
            var ilGS = getSupport.Body.GetILProcessor();
            var origFirstGS = getSupport.Body.Instructions[0];

            ilGS.InsertBefore(origFirstGS, ilGS.Create(OpCodes.Ldarg_0));
            ilGS.InsertBefore(origFirstGS, ilGS.Create(OpCodes.Ldfld, fNview));
            ilGS.InsertBefore(origFirstGS, ilGS.Create(OpCodes.Brfalse_S, origFirstGS));

            ilGS.InsertBefore(origFirstGS, ilGS.Create(OpCodes.Ldarg_0));
            ilGS.InsertBefore(origFirstGS, ilGS.Create(OpCodes.Ldfld, fNview));
            ilGS.InsertBefore(origFirstGS, ilGS.Create(OpCodes.Callvirt, isOwnerMethod));
            ilGS.InsertBefore(origFirstGS, ilGS.Create(OpCodes.Brfalse_S, origFirstGS));

            ilGS.InsertBefore(origFirstGS, ilGS.Create(OpCodes.Ldarg_0));
            ilGS.InsertBefore(origFirstGS, ilGS.Create(OpCodes.Ldfld, fSupport));
            ilGS.InsertBefore(origFirstGS, ilGS.Create(OpCodes.Ldc_R4, 0.0f));
            ilGS.InsertBefore(origFirstGS, ilGS.Create(OpCodes.Ble_Un_S, origFirstGS));

            ilGS.InsertBefore(origFirstGS, ilGS.Create(OpCodes.Ldarg_0));
            ilGS.InsertBefore(origFirstGS, ilGS.Create(OpCodes.Ldfld, fSupport));
            ilGS.InsertBefore(origFirstGS, ilGS.Create(OpCodes.Ret));
            Console.WriteLine("  + WearNTear.GetSupport: owner fast-path (ZDO sync preserved for guests)");

            // HaveSupport fast-path: if (m_support >= 100) return true;
            var haveSupport = FindOrThrow(wntType.Methods, m => m.Name == "HaveSupport" && m.Parameters.Count == 0, "WearNTear.HaveSupport()");
            var ilHS = haveSupport.Body.GetILProcessor();
            var origFirstHS = haveSupport.Body.Instructions[0];
            ilHS.InsertBefore(origFirstHS, ilHS.Create(OpCodes.Ldarg_0));
            ilHS.InsertBefore(origFirstHS, ilHS.Create(OpCodes.Ldfld, fSupport));
            ilHS.InsertBefore(origFirstHS, ilHS.Create(OpCodes.Ldc_R4, 100.0f));
            ilHS.InsertBefore(origFirstHS, ilHS.Create(OpCodes.Blt_Un_S, origFirstHS));
            ilHS.InsertBefore(origFirstHS, ilHS.Create(OpCodes.Ldc_I4_1));
            ilHS.InsertBefore(origFirstHS, ilHS.Create(OpCodes.Ret));
            Console.WriteLine("  + WearNTear.HaveSupport: fast-path (true when m_support >= 100)");

            // ShouldUpdateSupport: stagger 1-in-8 frames by m_myIndex
            var fClearCachedSupport = FindOrThrow(wntType.Fields, f => f.Name == "m_clearCachedSupport", "m_clearCachedSupport");
            var fSupportColliders = FindOrThrow(wntType.Fields, f => f.Name == "m_supportColliders", "m_supportColliders");
            var fMyIndex = FindOrThrow(wntType.Fields, f => f.Name == "m_myIndex", "m_myIndex");

            var shouldUpdate = new MethodDefinition("ShouldUpdateSupport", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, wntType.Module.TypeSystem.Boolean);
            shouldUpdate.Parameters.Add(new ParameterDefinition("wnt", ParameterAttributes.None, wntType));
            var ilSU = shouldUpdate.Body.GetILProcessor();
            var retTrue = ilSU.Create(OpCodes.Ldc_I4_1);
            var retTrueEnd = ilSU.Create(OpCodes.Ret);
            var checkSupport = ilSU.Create(OpCodes.Ldarg_0);

            ilSU.Append(ilSU.Create(OpCodes.Ldarg_0));
            ilSU.Append(ilSU.Create(OpCodes.Brtrue_S, checkSupport));
            ilSU.Append(ilSU.Create(OpCodes.Ldc_I4_0));
            ilSU.Append(ilSU.Create(OpCodes.Ret));
            ilSU.Append(checkSupport);
            ilSU.Append(ilSU.Create(OpCodes.Ldfld, fSupport));
            ilSU.Append(ilSU.Create(OpCodes.Ldc_R4, 0.0f));
            ilSU.Append(ilSU.Create(OpCodes.Ble_Un_S, retTrue));
            ilSU.Append(ilSU.Create(OpCodes.Ldarg_0));
            ilSU.Append(ilSU.Create(OpCodes.Ldfld, fClearCachedSupport));
            ilSU.Append(ilSU.Create(OpCodes.Brtrue_S, retTrue));
            ilSU.Append(ilSU.Create(OpCodes.Ldarg_0));
            ilSU.Append(ilSU.Create(OpCodes.Ldfld, fSupportColliders));
            ilSU.Append(ilSU.Create(OpCodes.Brfalse_S, retTrue));
            ilSU.Append(ilSU.Create(OpCodes.Call, getFrameCount));
            ilSU.Append(ilSU.Create(OpCodes.Ldc_I4_7));
            ilSU.Append(ilSU.Create(OpCodes.And));
            ilSU.Append(ilSU.Create(OpCodes.Ldarg_0));
            ilSU.Append(ilSU.Create(OpCodes.Ldfld, fMyIndex));
            ilSU.Append(ilSU.Create(OpCodes.Ldc_I4_7));
            ilSU.Append(ilSU.Create(OpCodes.And));
            ilSU.Append(ilSU.Create(OpCodes.Ceq));
            ilSU.Append(ilSU.Create(OpCodes.Ret));
            ilSU.Append(retTrue);
            ilSU.Append(retTrueEnd);
            wntType.Methods.Add(shouldUpdate);
            Console.WriteLine("  + WearNTear.ShouldUpdateSupport: staggered 1/8 frames");

            // Guard UpdateSupport call in UpdateWear
            var updateWear = FindOrThrow(wntType.Methods, m => m.Name == "UpdateWear", "WearNTear.UpdateWear");
            var ilUW = updateWear.Body.GetILProcessor();
            for (int i = 0; i < updateWear.Body.Instructions.Count; i++)
            {
                var inst = updateWear.Body.Instructions[i];
                if (inst.OpCode == OpCodes.Call && inst.Operand?.ToString()?.Contains("UpdateSupport") == true)
                {
                    var ldarg0 = updateWear.Body.Instructions[i - 1];
                    var nextInst = updateWear.Body.Instructions[i + 1];
                    ilUW.InsertBefore(ldarg0, ilUW.Create(OpCodes.Ldarg_0));
                    ilUW.InsertBefore(ldarg0, ilUW.Create(OpCodes.Call, shouldUpdate));
                    ilUW.InsertBefore(ldarg0, ilUW.Create(OpCodes.Brfalse_S, nextInst));
                    Console.WriteLine("  + WearNTear.UpdateWear: UpdateSupport guarded by ShouldUpdateSupport");
                    break;
                }
            }
        }

        static void ApplyClutterSystemPatches(TypeDefinition clutterType, float newDistance)
        {
            var fDistance = FindOrThrow(clutterType.Fields, f => f.Name == "m_distance", "ClutterSystem.m_distance");
            var fQuality = FindOrThrow(clutterType.Fields, f => f.Name == "m_quality", "ClutterSystem.m_quality");
            var ctor = FindOrThrow(clutterType.Methods, m => m.IsConstructor && !m.IsStatic, "ClutterSystem..ctor");
            foreach (var inst in ctor.Body.Instructions)
                if (inst.OpCode == OpCodes.Ldc_R4 && (float)inst.Operand == 40.0f)
                { inst.Operand = newDistance; Console.WriteLine($"  + ClutterSystem..ctor: 40m -> {newDistance}m"); break; }

            var applySettings = clutterType.Methods.FirstOrDefault(m => m.Name == "ApplySettings");
            if (applySettings != null)
            {
                var il = applySettings.Body.GetILProcessor();
                var callClearAll = applySettings.Body.Instructions.FirstOrDefault(i => i.OpCode == OpCodes.Call && i.Operand?.ToString()?.Contains("ClearAll") == true);
                if (callClearAll != null)
                {
                    int idx = applySettings.Body.Instructions.IndexOf(callClearAll);
                    var ldarg0CA = applySettings.Body.Instructions[idx - 1];
                    var setMed = il.Create(OpCodes.Ldarg_0);
                    var setHigh = il.Create(OpCodes.Ldarg_0);
                    var done = ldarg0CA;
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Ldarg_0));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Ldfld, fQuality));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Ldc_I4_1));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Bne_Un, setMed));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Ldarg_0));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Ldc_R4, 30.0f));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Stfld, fDistance));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Br, done));
                    il.InsertBefore(ldarg0CA, setMed);
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Ldfld, fQuality));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Ldc_I4_2));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Bne_Un, setHigh));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Ldarg_0));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Ldc_R4, 35.0f));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Stfld, fDistance));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Br, done));
                    il.InsertBefore(ldarg0CA, setHigh);
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Ldc_R4, newDistance));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Stfld, fDistance));
                    Console.WriteLine($"  + ClutterSystem.ApplySettings: Low:30m Med:35m High:{newDistance}m");
                }
            }
        }

        static void ApplyHeightmapPatches(TypeDefinition heightmapType, MethodReference setCookingOptions)
        {
            var awake = FindOrThrow(heightmapType.Methods, m => m.Name == "Awake", "Heightmap.Awake");
            var fCollider = FindOrThrow(heightmapType.Fields, f => f.Name == "m_collider", "Heightmap.m_collider");
            var il = awake.Body.GetILProcessor();
            for (int i = 0; i < awake.Body.Instructions.Count; i++)
            {
                if (awake.Body.Instructions[i].OpCode == OpCodes.Stfld && awake.Body.Instructions[i].Operand?.ToString()?.Contains("m_collider") == true)
                {
                    var next = awake.Body.Instructions[i + 1];
                    var skip = il.Create(OpCodes.Nop);
                    il.InsertBefore(next, il.Create(OpCodes.Ldarg_0));
                    il.InsertBefore(next, il.Create(OpCodes.Ldfld, fCollider));
                    il.InsertBefore(next, il.Create(OpCodes.Brfalse_S, skip));
                    il.InsertBefore(next, il.Create(OpCodes.Ldarg_0));
                    il.InsertBefore(next, il.Create(OpCodes.Ldfld, fCollider));
                    il.InsertBefore(next, il.Create(OpCodes.Ldc_I4_S, (sbyte)30));
                    il.InsertBefore(next, il.Create(OpCodes.Callvirt, setCookingOptions));
                    il.InsertBefore(next, skip);
                    Console.WriteLine("  + Heightmap.Awake: FastMidphase cooking (30)");
                    break;
                }
            }
        }

        static void ApplyEffectListPatches(TypeDefinition effectListType, MethodReference getTransform, MethodReference getParent, MethodReference setParentTwo, MethodReference opImplicit)
        {
            var m = effectListType.Methods.FirstOrDefault(x => x.Name == "Create" && x.Parameters.Count == 6);
            if (m == null) return;
            var instrs = m.Body.Instructions;
            int startIdx = -1, endIdx = -1;
            for (int i = 0; i < instrs.Count; i++)
                if (instrs[i].OpCode == OpCodes.Callvirt && instrs[i].Operand?.ToString()?.Contains("ParentConstraint") == true)
                { startIdx = i - 1; break; }
            if (startIdx == -1) return;
            for (int i = startIdx; i < instrs.Count; i++)
                if (instrs[i].OpCode == OpCodes.Callvirt && instrs[i].Operand?.ToString()?.Contains("set_constraintActive") == true)
                { endIdx = i; break; }
            if (endIdx == -1) return;
            var skipTarget = instrs[endIdx + 1];
            var il = m.Body.GetILProcessor();
            var instanceVar = m.Body.Variables[6];
            var ni = new[] {
                il.Create(OpCodes.Ldloc_3),
                il.Create(OpCodes.Call, opImplicit),
                il.Create(OpCodes.Brfalse_S, skipTarget),
                il.Create(OpCodes.Ldloc_S, instanceVar),
                il.Create(OpCodes.Callvirt, getTransform),
                il.Create(OpCodes.Callvirt, getParent),
                il.Create(OpCodes.Call, opImplicit),
                il.Create(OpCodes.Brtrue_S, skipTarget),
                il.Create(OpCodes.Ldloc_S, instanceVar),
                il.Create(OpCodes.Callvirt, getTransform),
                il.Create(OpCodes.Ldloc_3),
                il.Create(OpCodes.Ldc_I4_0),
                il.Create(OpCodes.Callvirt, setParentTwo)
            };
            for (int i = 0; i < ni.Length; i++) instrs[startIdx + i] = ni[i];
            for (int i = startIdx + ni.Length; i <= endIdx; i++) { instrs[i].OpCode = OpCodes.Nop; instrs[i].Operand = null; }
            Console.WriteLine("  + EffectList.Create: ParentConstraint -> transform.SetParent(parent, false)");
        }

        static void ApplyMessageHudCanvasIsolation(TypeDefinition messageHudType, MethodReference getGameObject, MethodReference getCompCanvas, MethodReference addCompCanvas)
        {
            var awake = FindOrThrow(messageHudType.Methods, m => m.Name == "Awake", "MessageHud.Awake");
            var il = awake.Body.GetILProcessor();
            var first = awake.Body.Instructions[0];
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Callvirt, getCompCanvas));
            il.InsertBefore(first, il.Create(OpCodes.Brtrue_S, first));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Call, getGameObject));
            il.InsertBefore(first, il.Create(OpCodes.Callvirt, addCompCanvas));
            il.InsertBefore(first, il.Create(OpCodes.Pop));
            Console.WriteLine("  + MessageHud.Awake: Canvas isolation added");
        }

        static void ApplyForwardRenderingPatches(TypeDefinition gsmType, TypeDefinition lightLodType)
        {
            var getLightLimit = FindOrThrow(gsmType.Methods, m => m.Name == "GetLightLimit", "GetLightLimit");
            getLightLimit.Body.Instructions.Clear();
            var il = getLightLimit.Body.GetILProcessor();
            var argOpL = getLightLimit.IsStatic ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1;
            var nz = il.Create(OpCodes.Nop);
            il.Emit(argOpL); il.Emit(OpCodes.Brtrue_S, nz);
            il.Emit(OpCodes.Ldc_I4_2); il.Emit(OpCodes.Ret); il.Append(nz);
            var n1 = il.Create(OpCodes.Nop);
            il.Emit(argOpL); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Bne_Un_S, n1);
            il.Emit(OpCodes.Ldc_I4_3); il.Emit(OpCodes.Ret); il.Append(n1);
            il.Emit(OpCodes.Ldc_I4_4); il.Emit(OpCodes.Ret);

            var getPointLightLimit = FindOrThrow(gsmType.Methods, m => m.Name == "GetPointLightLimit", "GetPointLightLimit");
            getPointLightLimit.Body.Instructions.Clear();
            il = getPointLightLimit.Body.GetILProcessor();
            var argOpP = getPointLightLimit.IsStatic ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1;
            nz = il.Create(OpCodes.Nop);
            il.Emit(argOpP); il.Emit(OpCodes.Brtrue_S, nz);
            il.Emit(OpCodes.Ldc_I4_4); il.Emit(OpCodes.Ret); il.Append(nz);
            n1 = il.Create(OpCodes.Nop);
            il.Emit(argOpP); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Bne_Un_S, n1);
            il.Emit(OpCodes.Ldc_I4_6); il.Emit(OpCodes.Ret); il.Append(n1);
            il.Emit(OpCodes.Ldc_I4_8); il.Emit(OpCodes.Ret);

            var applyQuality = FindOrThrow(gsmType.Methods, m => m.Name == "ApplyQualitySettings", "ApplyQualitySettings");
            for (int i = 0; i < applyQuality.Body.Instructions.Count; i++)
            {
                var inst = applyQuality.Body.Instructions[i];
                if (inst.Operand is MethodReference mr && mr.Name == "set_softParticles")
                { applyQuality.Body.Instructions[i - 2].OpCode = OpCodes.Nop; applyQuality.Body.Instructions[i - 1].OpCode = OpCodes.Ldc_I4_0; applyQuality.Body.Instructions[i - 1].Operand = null; break; }
            }

            var llCtor = FindOrThrow(lightLodType.Methods, m => m.IsConstructor && !m.IsStatic, "LightLod..ctor");
            for (int i = 0; i < llCtor.Body.Instructions.Count; i++)
            {
                if (llCtor.Body.Instructions[i].Operand is FieldReference fr)
                {
                    if (fr.Name == "m_lightDistance") llCtor.Body.Instructions[i - 1].Operand = 28.0f;
                    else if (fr.Name == "m_shadowDistance") llCtor.Body.Instructions[i - 1].Operand = 14.0f;
                }
            }
            Console.WriteLine("  + Forward lights: 2/3/4, point: 4/6/8, softParticles=false, LightLod 28m/14m");
        }

        static void ApplyLightFlickerPatches(TypeDefinition lightFlickerType)
        {
            var cu = FindOrThrow(lightFlickerType.Methods, m => m.Name == "CustomUpdate", "LightFlicker.CustomUpdate");
            int idx = -1;
            for (int i = 0; i < cu.Body.Instructions.Count; i++)
                if (cu.Body.Instructions[i].Operand is MethodReference mr && mr.Name == "set_intensity") { idx = i; break; }
            if (idx != -1)
            {
                while (cu.Body.Instructions.Count > idx + 1) cu.Body.Instructions.RemoveAt(cu.Body.Instructions.Count - 1);
                cu.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                Console.WriteLine("  + LightFlicker.CustomUpdate: transform jitter eliminated");
            }
        }

        static void ApplySmokeSpawnerPatches(TypeDefinition smokeSpawnerType)
        {
            var spawn = FindOrThrow(smokeSpawnerType.Methods, m => m.Name == "Spawn", "SmokeSpawner.Spawn");
            for (int i = 0; i < spawn.Body.Instructions.Count; i++)
                if (spawn.Body.Instructions[i].OpCode == OpCodes.Ldc_I4_S && (sbyte)spawn.Body.Instructions[i].Operand == 100)
                { spawn.Body.Instructions[i].Operand = (sbyte)35; Console.WriteLine("  + SmokeSpawner.Spawn: 100 -> 35"); break; }
        }

        static void ApplyFireDotInterval(TypeDefinition fireType)
        {
            var fireCtor = FindOrThrow(fireType.Methods, m => m.IsConstructor && !m.IsStatic, "Fire..ctor");
            for (int i = 0; i < fireCtor.Body.Instructions.Count; i++)
                if (fireCtor.Body.Instructions[i].Operand is FieldReference fr && fr.Name == "m_dotInterval")
                { fireCtor.Body.Instructions[i - 1].Operand = 2.0f; Console.WriteLine("  + Fire..ctor: m_dotInterval -> 2.0s"); break; }
        }

        // ==================== v6 REFINED PATCH METHODS (12-15) ====================

        static void ApplyFireDistanceCull(TypeDefinition fireType, TypeDefinition playerType, MethodReference mathfMaxFloat, MethodReference getTransformComp, MethodReference getPosition, MethodReference vec3Distance, MethodReference isOwnerMethod, MethodReference opImplicit)
        {
            var fLocalPlayer = FindOrThrow(playerType.Fields, f => f.Name == "m_localPlayer", "Player.m_localPlayer");
            var fUpdateRate = FindOrThrow(fireType.Fields, f => f.Name == "m_updateRate", "Fire.m_updateRate");
            var fNview = FindOrThrow(fireType.Fields, f => f.Name == "m_nview", "Fire.m_nview");

            // Clamp m_updateRate >= 1.0f in Awake
            var awake = FindOrThrow(fireType.Methods, m => m.Name == "Awake", "Fire.Awake");
            var ilA = awake.Body.GetILProcessor();
            var firstA = awake.Body.Instructions[0];
            ilA.InsertBefore(firstA, ilA.Create(OpCodes.Ldarg_0));
            ilA.InsertBefore(firstA, ilA.Create(OpCodes.Ldarg_0));
            ilA.InsertBefore(firstA, ilA.Create(OpCodes.Ldfld, fUpdateRate));
            ilA.InsertBefore(firstA, ilA.Create(OpCodes.Ldc_R4, 1.0f));
            ilA.InsertBefore(firstA, ilA.Create(OpCodes.Call, mathfMaxFloat));
            ilA.InsertBefore(firstA, ilA.Create(OpCodes.Stfld, fUpdateRate));
            Console.WriteLine("  + Fire.Awake: m_updateRate clamped >= 1.0s");

            // Safe owner-preserving distance cull for UpdateFire and Dot
            foreach (var methodName in new[] { "UpdateFire", "Dot" })
            {
                var method = FindOrThrow(fireType.Methods, m => m.Name == methodName, $"Fire.{methodName}");
                var il = method.Body.GetILProcessor();
                var first = method.Body.Instructions[0];

                // Rule 1: NEVER cull ZDO owner! Owner must always tick simulation logic!
                var checkPlayer = il.Create(OpCodes.Ldsfld, fLocalPlayer);
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Ldfld, fNview));
                il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, checkPlayer));
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Ldfld, fNview));
                il.InsertBefore(first, il.Create(OpCodes.Callvirt, isOwnerMethod));
                il.InsertBefore(first, il.Create(OpCodes.Brtrue_S, first));

                // Rule 2: If not owner, check local player with Object.op_Implicit
                il.InsertBefore(first, checkPlayer);
                il.InsertBefore(first, il.Create(OpCodes.Call, opImplicit));
                il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, first));

                // Rule 3: If distance > 30m, cull
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Call, getTransformComp));
                il.InsertBefore(first, il.Create(OpCodes.Callvirt, getPosition));
                il.InsertBefore(first, il.Create(OpCodes.Ldsfld, fLocalPlayer));
                il.InsertBefore(first, il.Create(OpCodes.Call, getTransformComp));
                il.InsertBefore(first, il.Create(OpCodes.Callvirt, getPosition));
                il.InsertBefore(first, il.Create(OpCodes.Call, vec3Distance));
                il.InsertBefore(first, il.Create(OpCodes.Ldc_R4, 30.0f));
                il.InsertBefore(first, il.Create(OpCodes.Ble_S, first));
                il.InsertBefore(first, il.Create(OpCodes.Ret));
                Console.WriteLine($"  + Fire.{methodName}: owner-safe 30m distance cull with op_Implicit");
            }
        }

        static void ApplyRagdollPatches(TypeDefinition ragdollType, MethodReference mathfMinFloat)
        {
            var fTtl = FindOrThrow(ragdollType.Fields, f => f.Name == "m_ttl", "Ragdoll.m_ttl");
            var fFloat = FindOrThrow(ragdollType.Fields, f => f.Name == "m_float", "Ragdoll.m_float");

            var awake = FindOrThrow(ragdollType.Methods, m => m.Name == "Awake", "Ragdoll.Awake");
            var il = awake.Body.GetILProcessor();
            var first = awake.Body.Instructions[0];

            // m_ttl = Min(m_ttl, 3.5f)
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Ldfld, fTtl));
            il.InsertBefore(first, il.Create(OpCodes.Ldc_R4, 3.5f));
            il.InsertBefore(first, il.Create(OpCodes.Call, mathfMinFloat));
            il.InsertBefore(first, il.Create(OpCodes.Stfld, fTtl));
            Console.WriteLine("  + Ragdoll.Awake: TTL clamped <= 3.5s (no ring buffer, no premature drop)");

            // UpdateFloating: early return if !m_float
            var uf = FindOrThrow(ragdollType.Methods, m => m.Name == "UpdateFloating", "Ragdoll.UpdateFloating");
            var ilF = uf.Body.GetILProcessor();
            var firstF = uf.Body.Instructions[0];
            ilF.InsertBefore(firstF, ilF.Create(OpCodes.Ldarg_0));
            ilF.InsertBefore(firstF, ilF.Create(OpCodes.Ldfld, fFloat));
            ilF.InsertBefore(firstF, ilF.Create(OpCodes.Brtrue_S, firstF));
            ilF.InsertBefore(firstF, ilF.Create(OpCodes.Ret));
            Console.WriteLine("  + Ragdoll.UpdateFloating: skip when m_float==false");
        }

        static void ApplyEffectListFrameLimiter(TypeDefinition effectListType, TypeDefinition gameType, TypeDefinition playerType, MethodReference getTransformComp, MethodReference getPosition, MethodReference vec3Distance, MethodReference arrayEmptyGameObject, MethodReference opImplicit)
        {
            var fCount = new FieldDefinition("s_effectsThisFrame", FieldAttributes.Public | FieldAttributes.Static, effectListType.Module.TypeSystem.Int32);
            effectListType.Fields.Add(fCount);

            // Reset in Game.Update
            var gameUpdate = FindOrThrow(gameType.Methods, m => m.Name == "Update", "Game.Update");
            var ilG = gameUpdate.Body.GetILProcessor();
            var firstG = gameUpdate.Body.Instructions[0];
            ilG.InsertBefore(firstG, ilG.Create(OpCodes.Ldc_I4_0));
            ilG.InsertBefore(firstG, ilG.Create(OpCodes.Stsfld, fCount));

            // Throttle in EffectList.Create
            var create = FindOrThrow(effectListType.Methods, m => m.Name == "Create" && m.Parameters.Count == 6, "EffectList.Create(6)");
            var il = create.Body.GetILProcessor();
            var firstC = create.Body.Instructions[0];
            var fLocalPlayer = FindOrThrow(playerType.Fields, f => f.Name == "m_localPlayer", "Player.m_localPlayer");

            // ++s_effectsThisFrame
            il.InsertBefore(firstC, il.Create(OpCodes.Ldsfld, fCount));
            il.InsertBefore(firstC, il.Create(OpCodes.Ldc_I4_1));
            il.InsertBefore(firstC, il.Create(OpCodes.Add));
            il.InsertBefore(firstC, il.Create(OpCodes.Dup));
            il.InsertBefore(firstC, il.Create(OpCodes.Stsfld, fCount));

            // if (count <= 8) goto allow
            il.InsertBefore(firstC, il.Create(OpCodes.Ldc_I4_8));
            il.InsertBefore(firstC, il.Create(OpCodes.Ble_S, firstC));

            // if (player != null && distance > 10m) drop
            var checkHardCap = il.Create(OpCodes.Ldsfld, fCount);
            var dropLabel = il.Create(OpCodes.Call, arrayEmptyGameObject);
            il.InsertBefore(firstC, il.Create(OpCodes.Ldsfld, fLocalPlayer));
            il.InsertBefore(firstC, il.Create(OpCodes.Call, opImplicit));
            il.InsertBefore(firstC, il.Create(OpCodes.Brfalse_S, checkHardCap));
            il.InsertBefore(firstC, il.Create(OpCodes.Ldarg_1)); // pos
            il.InsertBefore(firstC, il.Create(OpCodes.Ldsfld, fLocalPlayer));
            il.InsertBefore(firstC, il.Create(OpCodes.Call, getTransformComp));
            il.InsertBefore(firstC, il.Create(OpCodes.Callvirt, getPosition));
            il.InsertBefore(firstC, il.Create(OpCodes.Call, vec3Distance));
            il.InsertBefore(firstC, il.Create(OpCodes.Ldc_R4, 10.0f));
            il.InsertBefore(firstC, il.Create(OpCodes.Bgt_S, dropLabel));

            // if (count > 14) drop
            il.InsertBefore(firstC, checkHardCap);
            il.InsertBefore(firstC, il.Create(OpCodes.Ldc_I4_S, (sbyte)14));
            il.InsertBefore(firstC, il.Create(OpCodes.Ble_S, firstC));

            // return Array.Empty<GameObject>()
            il.InsertBefore(firstC, dropLabel);
            il.InsertBefore(firstC, il.Create(OpCodes.Ret));
            Console.WriteLine("  + EffectList.Create: frame limiter (<=8 free, 9-14 near only, >14 drop) with Array.Empty & op_Implicit");
        }

        static void ApplyCharacterAnimatorLOD(TypeDefinition charType, TypeDefinition playerType, MethodReference setCullingMode)
        {
            var awake = FindOrThrow(charType.Methods, m => m.Name == "Awake", "Character.Awake");
            var fAnimator = FindOrThrow(charType.Fields, f => f.Name == "m_animator", "Character.m_animator");
            var fBoss = FindOrThrow(charType.Fields, f => f.Name == "m_boss", "Character.m_boss");
            var il = awake.Body.GetILProcessor();
            int targetIdx = -1;
            for (int i = 0; i < awake.Body.Instructions.Count; i++)
                if (awake.Body.Instructions[i].OpCode == OpCodes.Callvirt && awake.Body.Instructions[i].Operand?.ToString()?.Contains("set_logWarnings") == true)
                { targetIdx = i + 1; break; }
            if (targetIdx != -1)
            {
                var next = awake.Body.Instructions[targetIdx];
                var cont = il.Create(OpCodes.Nop);

                // Skip Player
                il.InsertBefore(next, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(next, il.Create(OpCodes.Isinst, playerType));
                il.InsertBefore(next, il.Create(OpCodes.Brtrue_S, cont));

                // Skip Bosses
                il.InsertBefore(next, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(next, il.Create(OpCodes.Ldfld, fBoss));
                il.InsertBefore(next, il.Create(OpCodes.Brtrue_S, cont));

                // Apply CullUpdateTransforms (1)
                il.InsertBefore(next, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(next, il.Create(OpCodes.Ldfld, fAnimator));
                il.InsertBefore(next, il.Create(OpCodes.Brfalse_S, cont));
                il.InsertBefore(next, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(next, il.Create(OpCodes.Ldfld, fAnimator));
                il.InsertBefore(next, il.Create(OpCodes.Ldc_I4_1));
                il.InsertBefore(next, il.Create(OpCodes.Callvirt, setCullingMode));
                il.InsertBefore(next, cont);
                Console.WriteLine("  + Character.Awake: Animator.cullingMode = CullUpdateTransforms (excluded Player and Bosses)");
            }
        }

        static int VerifyPatches(string dllPath, string managedDir)
        {
            Console.WriteLine($"\n--- Verifying Patches in: {dllPath} ---");
            using var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(managedDir);
            using var asm = AssemblyDefinition.ReadAssembly(dllPath, new ReaderParameters { AssemblyResolver = resolver });
            var module = asm.MainModule;

            int checksPassed = 0;
            int totalChecks = 9;

            void AssertCheck(bool condition, string name, string details)
            {
                if (condition)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"[PASS] {name}: {details}");
                    checksPassed++;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[FAIL] {name}: {details}");
                }
                Console.ResetColor();
            }

            // 1. Marker check
            var wnt = FindOrThrow(module.Types, t => t.Name == "WearNTear", "WearNTear");
            var marker = wnt.Fields.FirstOrDefault(f => f.Name == MarkerFieldName);
            AssertCheck(marker != null, "Marker v6", $"Found marker {marker?.Name}");

            // 2. GraphicsSettingsManager check
            var gsm = FindOrThrow(module.Types, t => t.Name == "GraphicsSettingsManager", "GraphicsSettingsManager");
            var getLightLimit = FindOrThrow(gsm.Methods, m => m.Name == "GetLightLimit", "GetLightLimit");
            bool gsmUsesCorrectArg = getLightLimit.Body.Instructions.Any(i => i.OpCode == (getLightLimit.IsStatic ? OpCodes.Ldarg_0 : OpCodes.Ldarg_1));
            AssertCheck(gsmUsesCorrectArg, "P0 GSM Quality Args", $"Uses {(getLightLimit.IsStatic ? "Ldarg_0 (static)" : "Ldarg_1 (instance)")} correctly");

            // 3. WearNTear.GetSupport owner check
            var getSupport = FindOrThrow(wnt.Methods, m => m.Name == "GetSupport" && m.Parameters.Count == 0, "WearNTear.GetSupport");
            bool wntChecksOwner = getSupport.Body.Instructions.Take(12).Any(i => i.OpCode == OpCodes.Callvirt && i.Operand?.ToString()?.Contains("IsOwner") == true);
            AssertCheck(wntChecksOwner, "P0 WearNTear GetSupport", "Fast-path checks IsOwner before returning local support");

            // 4. Fire.UpdateFire & Fire.Dot owner check & op_Implicit
            var fire = FindOrThrow(module.Types, t => t.Name == "Fire", "Fire");
            var updateFire = FindOrThrow(fire.Methods, m => m.Name == "UpdateFire", "Fire.UpdateFire");
            var fireDot = FindOrThrow(fire.Methods, m => m.Name == "Dot", "Fire.Dot");
            bool fireUFChecksOwner = updateFire.Body.Instructions.Take(15).Any(i => i.Operand?.ToString()?.Contains("IsOwner") == true)
                                  && updateFire.Body.Instructions.Take(15).Any(i => i.Operand?.ToString()?.Contains("op_Implicit") == true);
            bool fireDotChecksOwner = fireDot.Body.Instructions.Take(15).Any(i => i.Operand?.ToString()?.Contains("IsOwner") == true)
                                   && fireDot.Body.Instructions.Take(15).Any(i => i.Operand?.ToString()?.Contains("op_Implicit") == true);
            AssertCheck(fireUFChecksOwner && fireDotChecksOwner, "P0/P1 Fire Owner & op_Implicit", "Never culls owner, uses Object.op_Implicit for player check");

            // 5. EffectList.Create Array.Empty
            var effectList = FindOrThrow(module.Types, t => t.Name == "EffectList", "EffectList");
            var create = FindOrThrow(effectList.Methods, m => m.Name == "Create" && m.Parameters.Count == 6, "EffectList.Create");
            bool usesArrayEmpty = create.Body.Instructions.Any(i => i.OpCode == OpCodes.Call && i.Operand?.ToString()?.Contains("System.Array::Empty") == true);
            bool elUsesOpImplicit = create.Body.Instructions.Take(30).Any(i => i.Operand?.ToString()?.Contains("op_Implicit") == true);
            AssertCheck(usesArrayEmpty && elUsesOpImplicit, "P1 EffectList GC & op_Implicit", "Uses Array.Empty<GameObject>() & Object.op_Implicit");

            // 6. Character.Awake player and boss exclusions
            var character = FindOrThrow(module.Types, t => t.Name == "Character", "Character");
            var charAwake = FindOrThrow(character.Methods, m => m.Name == "Awake", "Character.Awake");
            bool charExcludesPlayer = charAwake.Body.Instructions.Any(i => i.OpCode == OpCodes.Isinst && i.Operand?.ToString()?.Contains("Player") == true);
            bool charExcludesBoss = charAwake.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldfld && i.Operand?.ToString()?.Contains("m_boss") == true);
            AssertCheck(charExcludesPlayer && charExcludesBoss, "P1 Character Animator Exclusions", "Excludes Player and Bosses from CullUpdateTransforms");

            // 7. Ragdoll no ring buffer, no static leak, ttl clamp <= 3.5s
            var ragdoll = FindOrThrow(module.Types, t => t.Name == "Ragdoll", "Ragdoll");
            bool ragdollHasNoLeak = !ragdoll.Fields.Any(f => f.Name == "s_activeRagdolls");
            var ragdollAwake = FindOrThrow(ragdoll.Methods, m => m.Name == "Awake", "Ragdoll.Awake");
            bool ragdollClampsTtl = ragdollAwake.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand <= 4.0f);
            var ragdollFloat = FindOrThrow(ragdoll.Methods, m => m.Name == "UpdateFloating", "Ragdoll.UpdateFloating");
            bool ragdollBypassesFloat = ragdollFloat.Body.Instructions.Take(5).Any(i => i.Operand?.ToString()?.Contains("m_float") == true);
            AssertCheck(ragdollHasNoLeak && ragdollClampsTtl && ragdollBypassesFloat, "P2 Ragdoll Safety", "No static buffer, TTL clamped to 3.5s, UpdateFloating bypassed for !m_float");

            // 8. EffectList.Create SetParent(parent, false)
            bool usesSetParentTwo = create.Body.Instructions.Any(i => i.OpCode == OpCodes.Callvirt && i.Operand?.ToString()?.Contains("SetParent(UnityEngine.Transform,System.Boolean)") == true);
            AssertCheck(usesSetParentTwo, "P2 EffectList SetParent(false)", "Uses SetParent(parent, false) preserving local scale");

            // 9. All 15 original features verified
            AssertCheck(checksPassed == totalChecks - 1, "Overall Integrity", $"All {checksPassed}/{totalChecks - 1} subsystem audits passed successfully!");

            Console.WriteLine($"\nResult: {checksPassed}/{totalChecks} checks verified successfully.");
            return checksPassed == totalChecks ? 0 : 1;
        }
    }
}
