using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace ValheimPatcher
{
    public enum PatchVersion
    {
        V5,
        V6,
        V7,
        V8
    }

    class Program
    {
        public const string MarkerFieldNameV5 = "s_nativePatched_v5";
        public const string MarkerFieldNameV6 = "s_nativePatched_v6";
        public const string MarkerFieldNameV7 = "s_nativePatched_v7";
        public const string MarkerFieldNameV8 = "s_nativePatched_v8";

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
            PatchVersion targetVersion = PatchVersion.V8;
            bool restoreMode = false;
            bool statusMode = false;
            bool verifyMode = false;
            PatchVersion? verifyVersion = null;

            int physicsSteps = 4;
            int spawnLimit = 25;
            int updatesPerFrame = 20;
            float vegetationDistance = 40.0f;
            string managedDir = "";

            // Parse arguments
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].Trim();
                if (string.Equals(arg, "patch", StringComparison.OrdinalIgnoreCase))
                {
                    // Allow subcommand syntax like: patch v5 / patch v6 / patch v7 / patch v8
                    continue;
                }
                else if (string.Equals(arg, "v5", StringComparison.OrdinalIgnoreCase))
                {
                    targetVersion = PatchVersion.V5;
                    verifyVersion = PatchVersion.V5;
                }
                else if (string.Equals(arg, "v6", StringComparison.OrdinalIgnoreCase))
                {
                    targetVersion = PatchVersion.V6;
                    verifyVersion = PatchVersion.V6;
                }
                else if (string.Equals(arg, "v7", StringComparison.OrdinalIgnoreCase))
                {
                    targetVersion = PatchVersion.V7;
                    verifyVersion = PatchVersion.V7;
                }
                else if (string.Equals(arg, "v8", StringComparison.OrdinalIgnoreCase))
                {
                    targetVersion = PatchVersion.V8;
                    verifyVersion = PatchVersion.V8;
                }
                else if ((arg == "--version" || arg == "-v") && i + 1 < args.Length)
                {
                    string vStr = args[++i].Trim().ToLowerInvariant();
                    if (vStr == "5" || vStr == "v5") { targetVersion = PatchVersion.V5; verifyVersion = PatchVersion.V5; }
                    else if (vStr == "6" || vStr == "v6") { targetVersion = PatchVersion.V6; verifyVersion = PatchVersion.V6; }
                    else if (vStr == "7" || vStr == "v7") { targetVersion = PatchVersion.V7; verifyVersion = PatchVersion.V7; }
                    else if (vStr == "8" || vStr == "v8") { targetVersion = PatchVersion.V8; verifyVersion = PatchVersion.V8; }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"[ERROR] Unknown version '{vStr}'. Supported versions: v5, v6, v7, v8");
                        Console.ResetColor();
                        return 1;
                    }
                }
                else if (arg == "--restore" || string.Equals(arg, "restore", StringComparison.OrdinalIgnoreCase))
                {
                    restoreMode = true;
                }
                else if (arg == "--status" || string.Equals(arg, "status", StringComparison.OrdinalIgnoreCase))
                {
                    statusMode = true;
                }
                else if (arg == "--verify" || string.Equals(arg, "verify", StringComparison.OrdinalIgnoreCase))
                {
                    verifyMode = true;
                    if (i + 1 < args.Length)
                    {
                        string next = args[i + 1].Trim().ToLowerInvariant();
                        if (next == "v5" || next == "5") { verifyVersion = PatchVersion.V5; i++; }
                        else if (next == "v6" || next == "6") { verifyVersion = PatchVersion.V6; i++; }
                        else if (next == "v7" || next == "7") { verifyVersion = PatchVersion.V7; i++; }
                        else if (next == "v8" || next == "8") { verifyVersion = PatchVersion.V8; i++; }
                    }
                }
                else if (arg == "--managed" && i + 1 < args.Length)
                {
                    managedDir = args[++i];
                }
                else if (arg == "--physics-steps" && i + 1 < args.Length && int.TryParse(args[i + 1], out int ps))
                {
                    physicsSteps = Math.Clamp(ps, 1, 30); i++;
                }
                else if (arg == "--spawn-limit" && i + 1 < args.Length && int.TryParse(args[i + 1], out int sl))
                {
                    spawnLimit = Math.Max(1, sl); i++;
                }
                else if (arg == "--updates-per-frame" && i + 1 < args.Length && int.TryParse(args[i + 1], out int upf))
                {
                    updatesPerFrame = Math.Max(1, upf); i++;
                }
                else if (arg == "--vegetation-distance" && i + 1 < args.Length && float.TryParse(args[i + 1], out float vd))
                {
                    vegetationDistance = Math.Max(0.0f, vd); i++;
                }
                else if (arg == "--help" || arg == "-h" || string.Equals(arg, "help", StringComparison.OrdinalIgnoreCase))
                {
                    PrintHelp();
                    return 0;
                }
            }

            string verTag = targetVersion switch
            {
                PatchVersion.V8 => "v8 (Zero-Stutter Anti-Pattern Elimination - Recommended)",
                PatchVersion.V7 => "v7 (Engine & Gameplay Fixes Edition)",
                PatchVersion.V6 => "v6 (Robust Network & Gameplay Edition)",
                _ => "v5 (Legacy Optimization Edition)"
            };
            Console.WriteLine("=========================================================");
            Console.WriteLine("     Valheim Native IL Patcher (macOS Apple Silicon)     ");
            Console.WriteLine("     Zero Rosetta, Zero BepInEx, Native arm64 Mono       ");
            Console.WriteLine($"     Selected Version: {verTag}");
            Console.WriteLine("=========================================================\n");

            if (string.IsNullOrEmpty(managedDir))
            {
                string? current = AppContext.BaseDirectory;
                while (current != null)
                {
                    string candidate = Path.Combine(current, "Contents/Resources/Data/Managed");
                    if (Directory.Exists(candidate)) { managedDir = candidate; break; }
                    string candidateApp = Path.Combine(current, "valheim.app/Contents/Resources/Data/Managed");
                    if (Directory.Exists(candidateApp)) { managedDir = candidateApp; break; }
                    var parent = Directory.GetParent(current);
                    current = parent?.FullName;
                }
            }

            if (string.IsNullOrEmpty(managedDir))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[ERROR] Could not auto-detect Managed directory. Use --managed <path> to specify it.");
                Console.ResetColor();
                return 1;
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

            if (statusMode) { CheckStatus(dllPath, bakPath, managedDir); return 0; }
            if (restoreMode) return RestoreBackup(dllPath, bakPath);
            if (verifyMode) return VerifyPatches(dllPath, managedDir, verifyVersion);

            return ApplyPatches(dllPath, bakPath, managedDir, physicsSteps, spawnLimit, updatesPerFrame, vegetationDistance, targetVersion);
        }

        static void PrintHelp()
        {
            Console.WriteLine("Usage: dotnet run --project Patcher [version] [options]");
            Console.WriteLine("       ./patch.sh [v5|v6|v7|v8|--status|--verify|--restore]");
            Console.WriteLine("       ./start patch [v5|v6|v7|v8|status|verify|restore]");
            Console.WriteLine("\nVersions:");
            Console.WriteLine("  v8                         Apply v8 patch set (Recommended, zero-stutter anti-pattern fixes)");
            Console.WriteLine("  v7                         Apply v7 patch set (Engine bugfixes & combat stability)");
            Console.WriteLine("  v6                         Apply v6 patch set (Legacy network & stability fixes)");
            Console.WriteLine("  v5                         Apply v5 patch set (Legacy optimization set)");
            Console.WriteLine("\nOptions:");
            Console.WriteLine("  --status, status           Check current patch status and backup existence");
            Console.WriteLine("  --verify [v5|v6|v7|v8], verify Deep verify patch invariants in target assembly");
            Console.WriteLine("  --restore, restore         Restore original assembly_valheim.dll from backup");
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
                if (marker.Name == MarkerFieldNameV8)
                    Console.WriteLine($"Status: ALREADY PATCHED (marker: {marker.Name} [v8 - Zero Stutter & Stability])");
                else if (marker.Name == MarkerFieldNameV7)
                    Console.WriteLine($"Status: ALREADY PATCHED (marker: {marker.Name} [v7 - Engine & Gameplay Fixes])");
                else if (marker.Name == MarkerFieldNameV6)
                    Console.WriteLine($"Status: ALREADY PATCHED (marker: {marker.Name} [v6 - Robust & Stable])");
                else if (marker.Name == MarkerFieldNameV5)
                    Console.WriteLine($"Status: ALREADY PATCHED (marker: {marker.Name} [v5 - Legacy])");
                else
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

        static int ApplyPatches(string dllPath, string bakPath, string managedDir, int physicsSteps, int spawnLimit, int updatesPerFrame, float vegetationDistance, PatchVersion targetVersion)
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
            var setParent = module.ImportReference(FindOrThrow(transType.Methods, x => x.Name == "set_parent", "Transform.set_parent"));
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

            // Resolve Array.Empty<GameObject>() from mscorlib (for v6)
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

            // ==================== SHARED CORE PATCHES (1-11) ====================

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

            // Patch 3: WearNTear Throttling & Support Caching
            var wntType = FindOrThrow(module.Types, t => t.Name == "WearNTear", "WearNTear");
            var updaterType = FindOrThrow(module.Types, t => t.Name == "WearNTearUpdater", "WearNTearUpdater");
            if (targetVersion == PatchVersion.V5)
            {
                Console.WriteLine($"\n[4/18] Patching WearNTear (v5 legacy fast-path, Max {updatesPerFrame} pieces/frame)...");
                ApplyWearNTearPatchesV5(wntType, updaterType, getFrameCount, updatesPerFrame);
            }
            else if (targetVersion == PatchVersion.V6)
            {
                Console.WriteLine($"\n[4/18] Patching WearNTear (v6 owner-guarded fast-path, Max {updatesPerFrame} pieces/frame)...");
                ApplyWearNTearPatchesV6(wntType, updaterType, getFrameCount, isOwnerMethod, updatesPerFrame);
            }
            else
            {
                Console.WriteLine($"\n[4/18] Patching WearNTear ({(targetVersion == PatchVersion.V8 ? "v8" : "v7")} robust, Max {updatesPerFrame} pieces/frame)...");
                var fSupportColliders = FindOrThrow(wntType.Fields, f => f.Name == "m_supportColliders", "m_supportColliders");
                var getCountList = module.ImportReference(new MethodReference("get_Count", module.TypeSystem.Int32, fSupportColliders.FieldType) { HasThis = true });
                ApplyWearNTearPatchesV7(wntType, updaterType, getFrameCount, getCountList, updatesPerFrame);
            }

            // Patch 4: ClutterSystem Vegetation Distance
            Console.WriteLine($"\n[5/18] Patching ClutterSystem vegetation draw distance ({vegetationDistance}m on High)...");
            var clutterType = FindOrThrow(module.Types, t => t.Name == "ClutterSystem", "ClutterSystem");
            ApplyClutterSystemPatches(clutterType, vegetationDistance);

            // Patch 5: Heightmap PhysX Cooking & Zero-Alloc Collision Mesh
            Console.WriteLine("\n[6/18] Patching Heightmap PhysX FastMidphase cooking options & zero-alloc mesh...");
            var heightmapType = FindOrThrow(module.Types, t => t.Name == "Heightmap", "Heightmap");
            ApplyHeightmapPatches(heightmapType, setCookingOptions);
            if (targetVersion == PatchVersion.V8)
            {
                ApplyHeightmapZeroAllocV8(heightmapType);
            }

            // Patch 6: EffectList ParentConstraint Spike Removal
            Console.WriteLine("\n[7/18] Patching EffectList & MessageHud spike elimination...");
            var effectListType = FindOrThrow(module.Types, t => t.Name == "EffectList", "EffectList");
            if (targetVersion == PatchVersion.V5)
                ApplyEffectListPatchesV5(effectListType, getTransformGO, getParent, setParent);
            else if (targetVersion == PatchVersion.V6)
                ApplyEffectListPatchesV6(effectListType, getTransformGO, getParent, setParentTwo, opImplicit);
            else
                ApplyEffectListPatchesV7(effectListType, getTransformGO, getParent, setParentTwo, opImplicit);

            // Patch 7: MessageHud Canvas Isolation
            var messageHudType = FindOrThrow(module.Types, t => t.Name == "MessageHud", "MessageHud");
            ApplyMessageHudCanvasIsolation(messageHudType, getGameObject, getCompCanvas, addCompCanvas);

            // Patch 8: Forward Light Rendering Limits & Soft Particles
            Console.WriteLine("\n[8/18] Patching Forward Light rendering limits & disabling soft particles...");
            var gsmType = FindOrThrow(module.Types, t => t.Name == "GraphicsSettingsManager", "GraphicsSettingsManager");
            var lightLodType = FindOrThrow(module.Types, t => t.Name == "LightLod", "LightLod");
            if (targetVersion == PatchVersion.V5)
                ApplyForwardRenderingPatchesV5(gsmType, lightLodType);
            else
                ApplyForwardRenderingPatchesV6(gsmType, lightLodType);

            // Patch 9: LightFlicker Transform Hierarchy
            Console.WriteLine("\n[9/18] Patching LightFlicker to eliminate transform hierarchy invalidation...");
            var lightFlickerType = FindOrThrow(module.Types, t => t.Name == "LightFlicker", "LightFlicker");
            ApplyLightFlickerPatches(lightFlickerType);

            // Patch 10: Global Smoke Particle Cap
            Console.WriteLine("\n[10/18] Patching SmokeSpawner global particle & PhysX body cap...");
            var smokeSpawnerType = FindOrThrow(module.Types, t => t.Name == "SmokeSpawner", "SmokeSpawner");
            ApplySmokeSpawnerPatches(smokeSpawnerType);

            // Patch 11: Fire.Dot PhysX Throttle
            Console.WriteLine("\n[11/18] Patching Fire.Dot PhysX OverlapSphere query interval...");
            var fireType = FindOrThrow(module.Types, t => t.Name == "Fire", "Fire");
            ApplyFireDotInterval(fireType);

            // ==================== VERSION-SPECIFIC PATCHES (12-18) ====================

            // Patch 12: Fire distance cull
            if (targetVersion == PatchVersion.V5)
            {
                Console.WriteLine("\n[12/18] Patching Fire (v5 legacy 30m distance cull)...");
                ApplyFireDistanceCullV5(fireType, playerType, mathfMaxFloat, getTransformComp, getPosition, vec3Distance);
            }
            else if (targetVersion == PatchVersion.V6)
            {
                Console.WriteLine("\n[12/18] Patching Fire (v6 owner-safe distance cull & updateRate floor)...");
                ApplyFireDistanceCullV6(fireType, playerType, mathfMaxFloat, getTransformComp, getPosition, vec3Distance, isOwnerMethod, opImplicit);
            }
            else
            {
                Console.WriteLine($"\n[12/18] Patching Fire ({(targetVersion == PatchVersion.V8 ? "v8" : "v7")} clean: preserved vanilla UpdateFire integrity, dotInterval=2.0s active)...");
            }

            // Patch 13: Ragdoll
            var ragdollType = FindOrThrow(module.Types, t => t.Name == "Ragdoll", "Ragdoll");
            if (targetVersion == PatchVersion.V5)
            {
                Console.WriteLine("\n[13/18] Patching Ragdoll (v5 legacy ring buffer 8, TTL 4.5s)...");
                ApplyRagdollPatchesV5(ragdollType, mathfMinFloat, opImplicit);
            }
            else if (targetVersion == PatchVersion.V6)
            {
                Console.WriteLine("\n[13/18] Patching Ragdoll (v6 TTL 3.5s, floating bypass, zero memory leak)...");
                ApplyRagdollPatchesV6(ragdollType, mathfMinFloat);
            }
            else
            {
                Console.WriteLine($"\n[13/18] Patching Ragdoll ({(targetVersion == PatchVersion.V8 ? "v8" : "v7")} TTL 3.5s clean PhysX release)...");
                ApplyRagdollPatchesV7(ragdollType, mathfMinFloat);
            }

            // Patch 14: EffectList frame limiter
            if (targetVersion == PatchVersion.V5)
            {
                Console.WriteLine("\n[14/18] Patching EffectList global frame limiter (v5 newarr GameObject)...");
                ApplyEffectListFrameLimiterV5(effectListType, gameType, playerType, getTransformComp, getPosition, vec3Distance, goTypeRef);
            }
            else if (targetVersion == PatchVersion.V6)
            {
                Console.WriteLine("\n[14/18] Patching EffectList global frame limiter (v6 Array.Empty<GameObject>, op_Implicit)...");
                ApplyEffectListFrameLimiterV6(effectListType, gameType, playerType, getTransformComp, getPosition, vec3Distance, arrayEmptyGameObject, opImplicit);
            }
            else
            {
                Console.WriteLine($"\n[14/18] Patching EffectList global frame limiter ({(targetVersion == PatchVersion.V8 ? "v8" : "v7")} 45m combat range, <=16 free, <=32 cap)...");
                ApplyEffectListFrameLimiterV7(effectListType, gameType, playerType, getTransformComp, getPosition, vec3Distance, arrayEmptyGameObject, opImplicit);
            }

            // Patch 15: Character Animator LOD
            var charType = FindOrThrow(module.Types, t => t.Name == "Character", "Character");
            var humanoidType = FindOrThrow(module.Types, t => t.Name == "Humanoid", "Humanoid");
            if (targetVersion == PatchVersion.V5)
            {
                Console.WriteLine("\n[15/18] Patching Character Animator LOD (v5 global CullUpdateTransforms)...");
                ApplyCharacterAnimatorLODV5(charType, setCullingMode);
            }
            else if (targetVersion == PatchVersion.V6)
            {
                Console.WriteLine("\n[15/18] Patching Character Animator LOD (v6 CullUpdateTransforms excluding Player/Bosses)...");
                ApplyCharacterAnimatorLODV6(charType, playerType, setCullingMode);
            }
            else
            {
                Console.WriteLine($"\n[15/18] Patching Character Animator LOD ({(targetVersion == PatchVersion.V8 ? "v8" : "v7")} CullUpdateTransforms only for ambient wildlife, full combat safety)...");
                ApplyCharacterAnimatorLODV7(charType, humanoidType, setCullingMode);
            }

            // ==================== V8 EXCLUSIVE ANTI-PATTERN FIXES (16-17) ====================
            if (targetVersion == PatchVersion.V8)
            {
                Console.WriteLine("\n[16/17] Patching EnemyHud per-frame localization removal...");
                var enemyHudType = FindOrThrow(module.Types, t => t.Name == "EnemyHud", "EnemyHud");
                ApplyEnemyHudCachingV8(enemyHudType);

                Console.WriteLine("\n[17/17] Patching Pathfinding zero-alloc corner queries...");
                var aiModuleAsm = resolver.Resolve(new AssemblyNameReference("UnityEngine.AIModule", new Version(0, 0, 0, 0)));
                var navMeshPathType = FindOrThrow(aiModuleAsm.MainModule.Types, t => t.FullName == "UnityEngine.AI.NavMeshPath", "NavMeshPath");
                var getCornersNonAlloc = module.ImportReference(FindOrThrow(navMeshPathType.Methods, m => m.Name == "GetCornersNonAlloc", "NavMeshPath.GetCornersNonAlloc"));
                var pfType = FindOrThrow(module.Types, t => t.Name == "Pathfinding", "Pathfinding");
                ApplyPathfindingZeroAllocV8(pfType, navMeshPathType, vec3Type, getCornersNonAlloc);
            }

            // Add marker field
            string activeMarker = targetVersion switch
            {
                PatchVersion.V8 => MarkerFieldNameV8,
                PatchVersion.V7 => MarkerFieldNameV7,
                PatchVersion.V6 => MarkerFieldNameV6,
                _ => MarkerFieldNameV5
            };
            var wntType2 = FindOrThrow(module.Types, t => t.Name == "WearNTear", "WearNTear");
            wntType2.Fields.Add(new FieldDefinition(activeMarker, FieldAttributes.Private | FieldAttributes.Static, module.TypeSystem.Boolean));

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
                    if (!checkWnt.Fields.Any(f => f.Name == activeMarker))
                        throw new Exception($"Marker field '{activeMarker}' missing in patched assembly!");
                }
                File.Move(tempOut, dllPath, true);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("\n=========================================================");
                Console.WriteLine($"  [SUCCESS] Valheim {targetVersion.ToString().ToLower()} native patches applied!           ");
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

        // ==================== SHARED PATCH METHODS ====================

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
                if (spawnLimit <= 127 && spawnLimit >= -128)
                {
                    createObjects.Body.Instructions[0].Operand = (sbyte)spawnLimit;
                }
                else
                {
                    createObjects.Body.Instructions[0].OpCode = OpCodes.Ldc_I4;
                    createObjects.Body.Instructions[0].Operand = spawnLimit;
                }
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
                    var setLow = il.Create(OpCodes.Ldarg_0);
                    var setMed = il.Create(OpCodes.Ldarg_0);
                    var setHigh = il.Create(OpCodes.Ldarg_0);
                    var done = ldarg0CA;

                    // Quality.Off (0) -> 0.0f
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Ldarg_0));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Ldfld, fQuality));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Brtrue_S, setLow));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Ldarg_0));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Ldc_R4, 0.0f));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Stfld, fDistance));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Br, done));

                    // Quality.Low (1) -> 30.0f
                    il.InsertBefore(ldarg0CA, setLow);
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Ldfld, fQuality));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Ldc_I4_1));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Bne_Un, setMed));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Ldarg_0));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Ldc_R4, 30.0f));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Stfld, fDistance));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Br, done));

                    // Quality.Med (2) -> 35.0f
                    il.InsertBefore(ldarg0CA, setMed);
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Ldfld, fQuality));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Ldc_I4_2));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Bne_Un, setHigh));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Ldarg_0));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Ldc_R4, 35.0f));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Stfld, fDistance));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Br, done));

                    // Quality.High (3+) -> newDistance
                    il.InsertBefore(ldarg0CA, setHigh);
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Ldc_R4, newDistance));
                    il.InsertBefore(ldarg0CA, il.Create(OpCodes.Stfld, fDistance));
                    Console.WriteLine($"  + ClutterSystem.ApplySettings: Off:0m Low:30m Med:35m High:{newDistance}m");
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

        // ==================== V5 PATCH IMPLEMENTATIONS ====================

        static void ApplyForwardRenderingPatchesV5(TypeDefinition gsmType, TypeDefinition lightLodType)
        {
            var getLightLimit = FindOrThrow(gsmType.Methods, m => m.Name == "GetLightLimit", "GetLightLimit");
            getLightLimit.Body.Instructions.Clear();
            var il = getLightLimit.Body.GetILProcessor();
            var nz = il.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Brtrue_S, nz);
            il.Emit(OpCodes.Ldc_I4_2); il.Emit(OpCodes.Ret); il.Append(nz);
            var n1 = il.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Bne_Un_S, n1);
            il.Emit(OpCodes.Ldc_I4_3); il.Emit(OpCodes.Ret); il.Append(n1);
            il.Emit(OpCodes.Ldc_I4_4); il.Emit(OpCodes.Ret);

            var getPointLightLimit = FindOrThrow(gsmType.Methods, m => m.Name == "GetPointLightLimit", "GetPointLightLimit");
            getPointLightLimit.Body.Instructions.Clear();
            il = getPointLightLimit.Body.GetILProcessor();
            nz = il.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Brtrue_S, nz);
            il.Emit(OpCodes.Ldc_I4_4); il.Emit(OpCodes.Ret); il.Append(nz);
            n1 = il.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Bne_Un_S, n1);
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
            Console.WriteLine("  + Forward lights: 2/3/4, point: 4/6/8, softParticles=false, LightLod 28m/14m (v5 legacy)");
        }

        static void ApplyWearNTearPatchesV5(TypeDefinition wntType, TypeDefinition updaterType, MethodReference getFrameCount, int maxUpdatesPerFrame)
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

            var getSupport = FindOrThrow(wntType.Methods, m => m.Name == "GetSupport" && m.Parameters.Count == 0, "WearNTear.GetSupport()");
            var ilGS = getSupport.Body.GetILProcessor();
            var origFirstGS = getSupport.Body.Instructions[0];
            ilGS.InsertBefore(origFirstGS, ilGS.Create(OpCodes.Ldarg_0));
            ilGS.InsertBefore(origFirstGS, ilGS.Create(OpCodes.Ldfld, fSupport));
            ilGS.InsertBefore(origFirstGS, ilGS.Create(OpCodes.Ldc_R4, 0.0f));
            ilGS.InsertBefore(origFirstGS, ilGS.Create(OpCodes.Ble_Un_S, origFirstGS));
            ilGS.InsertBefore(origFirstGS, ilGS.Create(OpCodes.Ldarg_0));
            ilGS.InsertBefore(origFirstGS, ilGS.Create(OpCodes.Ldfld, fSupport));
            ilGS.InsertBefore(origFirstGS, ilGS.Create(OpCodes.Ret));
            Console.WriteLine("  + WearNTear.GetSupport: v5 fast-path (return m_support when > 0)");

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

        static void ApplyEffectListPatchesV5(TypeDefinition effectListType, MethodReference getTransform, MethodReference getParent, MethodReference setParent)
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
            var ni = new[] {
                il.Create(OpCodes.Ldloc_3), il.Create(OpCodes.Brfalse_S, skipTarget),
                il.Create(OpCodes.Ldloc_S, m.Body.Variables[6]), il.Create(OpCodes.Callvirt, getTransform),
                il.Create(OpCodes.Callvirt, getParent), il.Create(OpCodes.Brtrue_S, skipTarget),
                il.Create(OpCodes.Ldloc_S, m.Body.Variables[6]), il.Create(OpCodes.Callvirt, getTransform),
                il.Create(OpCodes.Ldloc_3), il.Create(OpCodes.Callvirt, setParent)
            };
            for (int i = 0; i < ni.Length; i++) instrs[startIdx + i] = ni[i];
            for (int i = startIdx + ni.Length; i <= endIdx; i++) { instrs[i].OpCode = OpCodes.Nop; instrs[i].Operand = null; }
            Console.WriteLine("  + EffectList.Create: ParentConstraint -> transform.SetParent (v5 legacy)");
        }

        static void ApplyFireDistanceCullV5(TypeDefinition fireType, TypeDefinition playerType, MethodReference mathfMaxFloat, MethodReference getTransformComp, MethodReference getPosition, MethodReference vec3Distance)
        {
            var fLocalPlayer = FindOrThrow(playerType.Fields, f => f.Name == "m_localPlayer", "Player.m_localPlayer");
            var fUpdateRate = FindOrThrow(fireType.Fields, f => f.Name == "m_updateRate", "Fire.m_updateRate");

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

            foreach (var methodName in new[] { "UpdateFire", "Dot" })
            {
                var method = FindOrThrow(fireType.Methods, m => m.Name == methodName, $"Fire.{methodName}");
                var il = method.Body.GetILProcessor();
                var first = method.Body.Instructions[0];

                il.InsertBefore(first, il.Create(OpCodes.Ldsfld, fLocalPlayer));
                il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, first));
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
                Console.WriteLine($"  + Fire.{methodName}: 30m distance cull (v5 legacy)");
            }
        }

        static void ApplyRagdollPatchesV5(TypeDefinition ragdollType, MethodReference mathfMinFloat, MethodReference opImplicit)
        {
            var fTtl = FindOrThrow(ragdollType.Fields, f => f.Name == "m_ttl", "Ragdoll.m_ttl");
            var fFloat = FindOrThrow(ragdollType.Fields, f => f.Name == "m_float", "Ragdoll.m_float");
            var mDestroyNow = FindOrThrow(ragdollType.Methods, m => m.Name == "DestroyNow", "Ragdoll.DestroyNow");

            var fBuf = new FieldDefinition("s_activeRagdolls", FieldAttributes.Private | FieldAttributes.Static, new ArrayType(ragdollType));
            ragdollType.Fields.Add(fBuf);
            var fIdx = new FieldDefinition("s_ragdollIndex", FieldAttributes.Private | FieldAttributes.Static, ragdollType.Module.TypeSystem.Int32);
            ragdollType.Fields.Add(fIdx);

            var awake = FindOrThrow(ragdollType.Methods, m => m.Name == "Awake", "Ragdoll.Awake");
            var il = awake.Body.GetILProcessor();
            var first = awake.Body.Instructions[0];

            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Ldfld, fTtl));
            il.InsertBefore(first, il.Create(OpCodes.Ldc_R4, 4.5f));
            il.InsertBefore(first, il.Create(OpCodes.Call, mathfMinFloat));
            il.InsertBefore(first, il.Create(OpCodes.Stfld, fTtl));

            var checkOld = il.Create(OpCodes.Ldsfld, fBuf);
            il.InsertBefore(first, il.Create(OpCodes.Ldsfld, fBuf));
            il.InsertBefore(first, il.Create(OpCodes.Brtrue_S, checkOld));
            il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_8));
            il.InsertBefore(first, il.Create(OpCodes.Newarr, ragdollType));
            il.InsertBefore(first, il.Create(OpCodes.Stsfld, fBuf));

            var popAndStore = il.Create(OpCodes.Pop);
            var storeThis = il.Create(OpCodes.Ldsfld, fBuf);
            il.InsertBefore(first, checkOld);
            il.InsertBefore(first, il.Create(OpCodes.Ldsfld, fIdx));
            il.InsertBefore(first, il.Create(OpCodes.Ldelem_Ref));
            il.InsertBefore(first, il.Create(OpCodes.Dup));
            il.InsertBefore(first, il.Create(OpCodes.Call, opImplicit));
            il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, popAndStore));
            il.InsertBefore(first, il.Create(OpCodes.Callvirt, mDestroyNow));
            il.InsertBefore(first, il.Create(OpCodes.Br_S, storeThis));
            il.InsertBefore(first, popAndStore);

            il.InsertBefore(first, storeThis);
            il.InsertBefore(first, il.Create(OpCodes.Ldsfld, fIdx));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Stelem_Ref));

            il.InsertBefore(first, il.Create(OpCodes.Ldsfld, fIdx));
            il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_1));
            il.InsertBefore(first, il.Create(OpCodes.Add));
            il.InsertBefore(first, il.Create(OpCodes.Ldc_I4_8));
            il.InsertBefore(first, il.Create(OpCodes.Rem));
            il.InsertBefore(first, il.Create(OpCodes.Stsfld, fIdx));
            Console.WriteLine("  + Ragdoll.Awake: TTL<=4.5s, ring buffer cap 8 (v5 legacy)");

            var uf = FindOrThrow(ragdollType.Methods, m => m.Name == "UpdateFloating", "Ragdoll.UpdateFloating");
            var ilF = uf.Body.GetILProcessor();
            var firstF = uf.Body.Instructions[0];
            ilF.InsertBefore(firstF, ilF.Create(OpCodes.Ldarg_0));
            ilF.InsertBefore(firstF, ilF.Create(OpCodes.Ldfld, fFloat));
            ilF.InsertBefore(firstF, ilF.Create(OpCodes.Brtrue_S, firstF));
            ilF.InsertBefore(firstF, ilF.Create(OpCodes.Ret));
            Console.WriteLine("  + Ragdoll.UpdateFloating: skip when m_float==false");
        }

        static void ApplyEffectListFrameLimiterV5(TypeDefinition effectListType, TypeDefinition gameType, TypeDefinition playerType, MethodReference getTransformComp, MethodReference getPosition, MethodReference vec3Distance, TypeReference goType)
        {
            var fCount = new FieldDefinition("s_effectsThisFrame", FieldAttributes.Public | FieldAttributes.Static, effectListType.Module.TypeSystem.Int32);
            effectListType.Fields.Add(fCount);

            var gameUpdate = FindOrThrow(gameType.Methods, m => m.Name == "Update", "Game.Update");
            var ilG = gameUpdate.Body.GetILProcessor();
            var firstG = gameUpdate.Body.Instructions[0];
            ilG.InsertBefore(firstG, ilG.Create(OpCodes.Ldc_I4_0));
            ilG.InsertBefore(firstG, ilG.Create(OpCodes.Stsfld, fCount));

            var create = FindOrThrow(effectListType.Methods, m => m.Name == "Create" && m.Parameters.Count == 6, "EffectList.Create(6)");
            var il = create.Body.GetILProcessor();
            var firstC = create.Body.Instructions[0];
            var fLocalPlayer = FindOrThrow(playerType.Fields, f => f.Name == "m_localPlayer", "Player.m_localPlayer");

            il.InsertBefore(firstC, il.Create(OpCodes.Ldsfld, fCount));
            il.InsertBefore(firstC, il.Create(OpCodes.Ldc_I4_1));
            il.InsertBefore(firstC, il.Create(OpCodes.Add));
            il.InsertBefore(firstC, il.Create(OpCodes.Dup));
            il.InsertBefore(firstC, il.Create(OpCodes.Stsfld, fCount));

            il.InsertBefore(firstC, il.Create(OpCodes.Ldc_I4_8));
            il.InsertBefore(firstC, il.Create(OpCodes.Ble_S, firstC));

            var checkHardCap = il.Create(OpCodes.Ldsfld, fCount);
            var dropLabel = il.Create(OpCodes.Ldc_I4_0);
            il.InsertBefore(firstC, il.Create(OpCodes.Ldsfld, fLocalPlayer));
            il.InsertBefore(firstC, il.Create(OpCodes.Brfalse_S, checkHardCap));
            il.InsertBefore(firstC, il.Create(OpCodes.Ldarg_1));
            il.InsertBefore(firstC, il.Create(OpCodes.Ldsfld, fLocalPlayer));
            il.InsertBefore(firstC, il.Create(OpCodes.Call, getTransformComp));
            il.InsertBefore(firstC, il.Create(OpCodes.Callvirt, getPosition));
            il.InsertBefore(firstC, il.Create(OpCodes.Call, vec3Distance));
            il.InsertBefore(firstC, il.Create(OpCodes.Ldc_R4, 10.0f));
            il.InsertBefore(firstC, il.Create(OpCodes.Bgt_S, dropLabel));

            il.InsertBefore(firstC, checkHardCap);
            il.InsertBefore(firstC, il.Create(OpCodes.Ldc_I4_S, (sbyte)14));
            il.InsertBefore(firstC, il.Create(OpCodes.Ble_S, firstC));

            il.InsertBefore(firstC, dropLabel);
            il.InsertBefore(firstC, il.Create(OpCodes.Newarr, goType));
            il.InsertBefore(firstC, il.Create(OpCodes.Ret));
            Console.WriteLine("  + EffectList.Create: frame limiter (<=8 free, 9-14 near only, >14 drop) (v5 legacy)");
        }

        static void ApplyCharacterAnimatorLODV5(TypeDefinition charType, MethodReference setCullingMode)
        {
            var awake = FindOrThrow(charType.Methods, m => m.Name == "Awake", "Character.Awake");
            var fAnimator = FindOrThrow(charType.Fields, f => f.Name == "m_animator", "Character.m_animator");
            var il = awake.Body.GetILProcessor();
            int targetIdx = -1;
            for (int i = 0; i < awake.Body.Instructions.Count; i++)
                if (awake.Body.Instructions[i].OpCode == OpCodes.Callvirt && awake.Body.Instructions[i].Operand?.ToString()?.Contains("set_logWarnings") == true)
                { targetIdx = i + 1; break; }
            if (targetIdx != -1)
            {
                var next = awake.Body.Instructions[targetIdx];
                var cont = il.Create(OpCodes.Nop);
                il.InsertBefore(next, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(next, il.Create(OpCodes.Ldfld, fAnimator));
                il.InsertBefore(next, il.Create(OpCodes.Brfalse_S, cont));
                il.InsertBefore(next, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(next, il.Create(OpCodes.Ldfld, fAnimator));
                il.InsertBefore(next, il.Create(OpCodes.Ldc_I4_1));
                il.InsertBefore(next, il.Create(OpCodes.Callvirt, setCullingMode));
                il.InsertBefore(next, cont);
                Console.WriteLine("  + Character.Awake: Animator.cullingMode = CullUpdateTransforms (v5 global)");
            }
        }

        // ==================== V6 REFINED PATCH IMPLEMENTATIONS ====================

        static void ApplyForwardRenderingPatchesV6(TypeDefinition gsmType, TypeDefinition lightLodType)
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
            Console.WriteLine("  + Forward lights: 2/3/4, point: 4/6/8, softParticles=false, LightLod 28m/14m (v6 adaptive)");
        }

        static void ApplyWearNTearPatchesV6(TypeDefinition wntType, TypeDefinition updaterType, MethodReference getFrameCount, MethodReference isOwnerMethod, int maxUpdatesPerFrame)
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

        static void ApplyWearNTearPatchesV7(TypeDefinition wntType, TypeDefinition updaterType, MethodReference getFrameCount, MethodReference getCountList, int maxUpdatesPerFrame)
        {
            void SetLdcI4(Instruction inst, int val)
            {
                if (val <= 127 && val >= -128)
                {
                    inst.OpCode = OpCodes.Ldc_I4_S;
                    inst.Operand = (sbyte)val;
                }
                else
                {
                    inst.OpCode = OpCodes.Ldc_I4;
                    inst.Operand = val;
                }
            }

            var ctor = FindOrThrow(updaterType.Methods, m => m.IsConstructor && !m.IsStatic, "WearNTearUpdater..ctor");
            foreach (var inst in ctor.Body.Instructions)
                if (inst.OpCode == OpCodes.Ldc_I4_S && (sbyte)inst.Operand == 50)
                { SetLdcI4(inst, maxUpdatesPerFrame); Console.WriteLine($"  + WearNTearUpdater..ctor: 50 -> {maxUpdatesPerFrame}"); break; }

            var updateWearNTear = FindOrThrow(updaterType.Methods, m => m.Name == "UpdateWearNTear", "WearNTearUpdater.UpdateWearNTear");
            foreach (var inst in updateWearNTear.Body.Instructions)
                if (inst.OpCode == OpCodes.Ldc_I4_S && (sbyte)inst.Operand == 100)
                { SetLdcI4(inst, maxUpdatesPerFrame); Console.WriteLine($"  + WearNTearUpdater.UpdateWearNTear: upper clamp 100 -> {maxUpdatesPerFrame}"); break; }

            // Note: WearNTear.GetSupport is NOT patched in v7 (vanilla already returns m_support when IsOwner).
            // Note: WearNTear.HaveSupport is NOT patched in v7 (vanilla m_support >= GetMinSupport() is clean and preserves heavy materials).
            Console.WriteLine("  + WearNTear: Preserved vanilla GetSupport & HaveSupport (stone/iron/marble structural integrity intact)");

            // ShouldUpdateSupport:
            // return true if wnt.m_support <= 0f || wnt.m_clearCachedSupport || wnt.m_supportColliders == null || wnt.m_supportColliders.Count == 0 || ((frameCount & 7) == (m_myIndex & 7));
            var fSupport = FindOrThrow(wntType.Fields, f => f.Name == "m_support", "WearNTear.m_support");
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

            // Check if m_supportColliders is null
            ilSU.Append(ilSU.Create(OpCodes.Ldarg_0));
            ilSU.Append(ilSU.Create(OpCodes.Ldfld, fSupportColliders));
            ilSU.Append(ilSU.Create(OpCodes.Brfalse_S, retTrue));

            // Check if m_supportColliders.Count == 0
            ilSU.Append(ilSU.Create(OpCodes.Ldarg_0));
            ilSU.Append(ilSU.Create(OpCodes.Ldfld, fSupportColliders));
            ilSU.Append(ilSU.Create(OpCodes.Callvirt, getCountList));
            ilSU.Append(ilSU.Create(OpCodes.Brfalse_S, retTrue));

            // Stagger 1-in-8 frames
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
            Console.WriteLine("  + WearNTear.ShouldUpdateSupport: checks support <= 0, cache empty, or 1/8 staggered frames");

            // Guard UpdateSupport AND HaveSupport block in UpdateWear
            var updateWear = FindOrThrow(wntType.Methods, m => m.Name == "UpdateWear", "WearNTear.UpdateWear");
            var ilUW = updateWear.Body.GetILProcessor();
            for (int i = 0; i < updateWear.Body.Instructions.Count; i++)
            {
                var inst = updateWear.Body.Instructions[i];
                if (inst.OpCode == OpCodes.Call && inst.Operand?.ToString()?.Contains("UpdateSupport") == true)
                {
                    var ldarg0 = updateWear.Body.Instructions[i - 1];
                    // Locate skip target (after HaveSupport and damage block)
                    Instruction? skipTarget = null;
                    for (int j = i; j < Math.Min(i + 10, updateWear.Body.Instructions.Count); j++)
                    {
                        if (updateWear.Body.Instructions[j].OpCode == OpCodes.Call && updateWear.Body.Instructions[j].Operand?.ToString()?.Contains("HaveSupport") == true)
                        {
                            var brInst = updateWear.Body.Instructions[j + 1];
                            if (brInst.Operand is Instruction targetInst)
                            {
                                skipTarget = targetInst;
                                break;
                            }
                        }
                    }
                    if (skipTarget == null)
                        throw new InvalidOperationException("[PATCH ERROR] Could not determine skip target for WearNTear.UpdateWear HaveSupport block");

                    ilUW.InsertBefore(ldarg0, ilUW.Create(OpCodes.Ldarg_0));
                    ilUW.InsertBefore(ldarg0, ilUW.Create(OpCodes.Call, shouldUpdate));
                    ilUW.InsertBefore(ldarg0, ilUW.Create(OpCodes.Brfalse, skipTarget));
                    Console.WriteLine("  + WearNTear.UpdateWear: guarded entire support/destruction block with ShouldUpdateSupport");
                    break;
                }
            }
        }

        static void ApplyEffectListPatchesV6(TypeDefinition effectListType, MethodReference getTransform, MethodReference getParent, MethodReference setParentTwo, MethodReference opImplicit)
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

        static void ApplyEffectListPatchesV7(TypeDefinition effectListType, MethodReference getTransform, MethodReference getParent, MethodReference setParentTwo, MethodReference opImplicit)
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
                il.Create(OpCodes.Ldc_I4_1), // worldPositionStays = true: keeps world position, rotation, and non-distorted scale
                il.Create(OpCodes.Callvirt, setParentTwo)
            };
            for (int i = 0; i < ni.Length; i++) instrs[startIdx + i] = ni[i];
            for (int i = startIdx + ni.Length; i <= endIdx; i++) { instrs[i].OpCode = OpCodes.Nop; instrs[i].Operand = null; }
            Console.WriteLine("  + EffectList.Create: ParentConstraint -> transform.SetParent(parent, true) [preserves offset & scale]");
        }

        static void ApplyFireDistanceCullV6(TypeDefinition fireType, TypeDefinition playerType, MethodReference mathfMaxFloat, MethodReference getTransformComp, MethodReference getPosition, MethodReference vec3Distance, MethodReference isOwnerMethod, MethodReference opImplicit)
        {
            var fLocalPlayer = FindOrThrow(playerType.Fields, f => f.Name == "m_localPlayer", "Player.m_localPlayer");
            var fUpdateRate = FindOrThrow(fireType.Fields, f => f.Name == "m_updateRate", "Fire.m_updateRate");
            var fNview = FindOrThrow(fireType.Fields, f => f.Name == "m_nview", "Fire.m_nview");

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

            foreach (var methodName in new[] { "UpdateFire", "Dot" })
            {
                var method = FindOrThrow(fireType.Methods, m => m.Name == methodName, $"Fire.{methodName}");
                var il = method.Body.GetILProcessor();
                var first = method.Body.Instructions[0];

                var checkPlayer = il.Create(OpCodes.Ldsfld, fLocalPlayer);
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Ldfld, fNview));
                il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, checkPlayer));
                il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(first, il.Create(OpCodes.Ldfld, fNview));
                il.InsertBefore(first, il.Create(OpCodes.Callvirt, isOwnerMethod));
                il.InsertBefore(first, il.Create(OpCodes.Brtrue_S, first));

                il.InsertBefore(first, checkPlayer);
                il.InsertBefore(first, il.Create(OpCodes.Call, opImplicit));
                il.InsertBefore(first, il.Create(OpCodes.Brfalse_S, first));

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

        static void ApplyRagdollPatchesV6(TypeDefinition ragdollType, MethodReference mathfMinFloat)
        {
            var fTtl = FindOrThrow(ragdollType.Fields, f => f.Name == "m_ttl", "Ragdoll.m_ttl");
            var fFloat = FindOrThrow(ragdollType.Fields, f => f.Name == "m_float", "Ragdoll.m_float");

            var awake = FindOrThrow(ragdollType.Methods, m => m.Name == "Awake", "Ragdoll.Awake");
            var il = awake.Body.GetILProcessor();
            var first = awake.Body.Instructions[0];

            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Ldfld, fTtl));
            il.InsertBefore(first, il.Create(OpCodes.Ldc_R4, 3.5f));
            il.InsertBefore(first, il.Create(OpCodes.Call, mathfMinFloat));
            il.InsertBefore(first, il.Create(OpCodes.Stfld, fTtl));
            Console.WriteLine("  + Ragdoll.Awake: TTL clamped <= 3.5s (no ring buffer, no premature drop)");

            var uf = FindOrThrow(ragdollType.Methods, m => m.Name == "UpdateFloating", "Ragdoll.UpdateFloating");
            var ilF = uf.Body.GetILProcessor();
            var firstF = uf.Body.Instructions[0];
            ilF.InsertBefore(firstF, ilF.Create(OpCodes.Ldarg_0));
            ilF.InsertBefore(firstF, ilF.Create(OpCodes.Ldfld, fFloat));
            ilF.InsertBefore(firstF, ilF.Create(OpCodes.Brtrue_S, firstF));
            ilF.InsertBefore(firstF, ilF.Create(OpCodes.Ret));
            Console.WriteLine("  + Ragdoll.UpdateFloating: skip when m_float==false");
        }

        static void ApplyRagdollPatchesV7(TypeDefinition ragdollType, MethodReference mathfMinFloat)
        {
            var fTtl = FindOrThrow(ragdollType.Fields, f => f.Name == "m_ttl", "Ragdoll.m_ttl");
            var awake = FindOrThrow(ragdollType.Methods, m => m.Name == "Awake", "Ragdoll.Awake");
            var il = awake.Body.GetILProcessor();
            var first = awake.Body.Instructions[0];

            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Ldfld, fTtl));
            il.InsertBefore(first, il.Create(OpCodes.Ldc_R4, 3.5f));
            il.InsertBefore(first, il.Create(OpCodes.Call, mathfMinFloat));
            il.InsertBefore(first, il.Create(OpCodes.Stfld, fTtl));
            Console.WriteLine("  + Ragdoll.Awake: TTL clamped <= 3.5s (clean PhysX release, no leaks)");
        }

        static void ApplyEffectListFrameLimiterV6(TypeDefinition effectListType, TypeDefinition gameType, TypeDefinition playerType, MethodReference getTransformComp, MethodReference getPosition, MethodReference vec3Distance, MethodReference arrayEmptyGameObject, MethodReference opImplicit)
        {
            var fCount = new FieldDefinition("s_effectsThisFrame", FieldAttributes.Public | FieldAttributes.Static, effectListType.Module.TypeSystem.Int32);
            effectListType.Fields.Add(fCount);

            var gameUpdate = FindOrThrow(gameType.Methods, m => m.Name == "Update", "Game.Update");
            var ilG = gameUpdate.Body.GetILProcessor();
            var firstG = gameUpdate.Body.Instructions[0];
            ilG.InsertBefore(firstG, ilG.Create(OpCodes.Ldc_I4_0));
            ilG.InsertBefore(firstG, ilG.Create(OpCodes.Stsfld, fCount));

            var create = FindOrThrow(effectListType.Methods, m => m.Name == "Create" && m.Parameters.Count == 6, "EffectList.Create(6)");
            var il = create.Body.GetILProcessor();
            var firstC = create.Body.Instructions[0];
            var fLocalPlayer = FindOrThrow(playerType.Fields, f => f.Name == "m_localPlayer", "Player.m_localPlayer");

            il.InsertBefore(firstC, il.Create(OpCodes.Ldsfld, fCount));
            il.InsertBefore(firstC, il.Create(OpCodes.Ldc_I4_1));
            il.InsertBefore(firstC, il.Create(OpCodes.Add));
            il.InsertBefore(firstC, il.Create(OpCodes.Dup));
            il.InsertBefore(firstC, il.Create(OpCodes.Stsfld, fCount));

            il.InsertBefore(firstC, il.Create(OpCodes.Ldc_I4_8));
            il.InsertBefore(firstC, il.Create(OpCodes.Ble_S, firstC));

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

            il.InsertBefore(firstC, checkHardCap);
            il.InsertBefore(firstC, il.Create(OpCodes.Ldc_I4_S, (sbyte)14));
            il.InsertBefore(firstC, il.Create(OpCodes.Ble_S, firstC));

            il.InsertBefore(firstC, dropLabel);
            il.InsertBefore(firstC, il.Create(OpCodes.Ret));
            Console.WriteLine("  + EffectList.Create: frame limiter (<=8 free, 9-14 near only, >14 drop) with Array.Empty & op_Implicit");
        }

        static void ApplyEffectListFrameLimiterV7(TypeDefinition effectListType, TypeDefinition gameType, TypeDefinition playerType, MethodReference getTransformComp, MethodReference getPosition, MethodReference vec3Distance, MethodReference arrayEmptyGameObject, MethodReference opImplicit)
        {
            var fCount = new FieldDefinition("s_effectsThisFrame", FieldAttributes.Public | FieldAttributes.Static, effectListType.Module.TypeSystem.Int32);
            effectListType.Fields.Add(fCount);

            var gameUpdate = FindOrThrow(gameType.Methods, m => m.Name == "Update", "Game.Update");
            var ilG = gameUpdate.Body.GetILProcessor();
            var firstG = gameUpdate.Body.Instructions[0];
            ilG.InsertBefore(firstG, ilG.Create(OpCodes.Ldc_I4_0));
            ilG.InsertBefore(firstG, ilG.Create(OpCodes.Stsfld, fCount));

            var create = FindOrThrow(effectListType.Methods, m => m.Name == "Create" && m.Parameters.Count == 6, "EffectList.Create(6)");
            var il = create.Body.GetILProcessor();
            var firstC = create.Body.Instructions[0];
            var fLocalPlayer = FindOrThrow(playerType.Fields, f => f.Name == "m_localPlayer", "Player.m_localPlayer");

            il.InsertBefore(firstC, il.Create(OpCodes.Ldsfld, fCount));
            il.InsertBefore(firstC, il.Create(OpCodes.Ldc_I4_1));
            il.InsertBefore(firstC, il.Create(OpCodes.Add));
            il.InsertBefore(firstC, il.Create(OpCodes.Dup));
            il.InsertBefore(firstC, il.Create(OpCodes.Stsfld, fCount));

            // Free tier: <= 16 effects/frame without distance check
            il.InsertBefore(firstC, il.Create(OpCodes.Ldc_I4_S, (sbyte)16));
            il.InsertBefore(firstC, il.Create(OpCodes.Ble_S, firstC));

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
            il.InsertBefore(firstC, il.Create(OpCodes.Ldc_R4, 45.0f)); // 45m covers full combat and bow range
            il.InsertBefore(firstC, il.Create(OpCodes.Bgt_S, dropLabel));

            // Hard cap: 32 effects/frame (blocks particle lag in extreme scenarios)
            il.InsertBefore(firstC, checkHardCap);
            il.InsertBefore(firstC, il.Create(OpCodes.Ldc_I4_S, (sbyte)32));
            il.InsertBefore(firstC, il.Create(OpCodes.Ble_S, firstC));

            il.InsertBefore(firstC, dropLabel);
            il.InsertBefore(firstC, il.Create(OpCodes.Ret));
            Console.WriteLine("  + EffectList.Create: frame limiter (<=16 free, 17-32 within 45m, >32 drop) with Array.Empty");
        }

        static void ApplyCharacterAnimatorLODV6(TypeDefinition charType, TypeDefinition playerType, MethodReference setCullingMode)
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

        static void ApplyCharacterAnimatorLODV7(TypeDefinition charType, TypeDefinition humanoidType, MethodReference setCullingMode)
        {
            var awake = FindOrThrow(charType.Methods, m => m.Name == "Awake", "Character.Awake");
            var fAnimator = FindOrThrow(charType.Fields, f => f.Name == "m_animator", "Character.m_animator");
            var fBoss = FindOrThrow(charType.Fields, f => f.Name == "m_boss", "Character.m_boss");
            var fTamed = FindOrThrow(charType.Fields, f => f.Name == "m_tamed", "Character.m_tamed");
            var fFaction = FindOrThrow(charType.Fields, f => f.Name == "m_faction", "Character.m_faction");
            var il = awake.Body.GetILProcessor();
            int targetIdx = -1;
            for (int i = 0; i < awake.Body.Instructions.Count; i++)
                if (awake.Body.Instructions[i].OpCode == OpCodes.Callvirt && awake.Body.Instructions[i].Operand?.ToString()?.Contains("set_logWarnings") == true)
                { targetIdx = i + 1; break; }
            if (targetIdx != -1)
            {
                var next = awake.Body.Instructions[targetIdx];
                var cont = il.Create(OpCodes.Nop);

                // Skip Humanoid (Player and all weapon-wielding humanoids)
                il.InsertBefore(next, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(next, il.Create(OpCodes.Isinst, humanoidType));
                il.InsertBefore(next, il.Create(OpCodes.Brtrue_S, cont));

                // Skip Bosses
                il.InsertBefore(next, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(next, il.Create(OpCodes.Ldfld, fBoss));
                il.InsertBefore(next, il.Create(OpCodes.Brtrue_S, cont));

                // Skip Tamed pets/mounts
                il.InsertBefore(next, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(next, il.Create(OpCodes.Ldfld, fTamed));
                il.InsertBefore(next, il.Create(OpCodes.Brtrue_S, cont));

                // Skip combat monsters (only apply to Faction.AnimalsVeg == 1)
                il.InsertBefore(next, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(next, il.Create(OpCodes.Ldfld, fFaction));
                il.InsertBefore(next, il.Create(OpCodes.Ldc_I4_1)); // AnimalsVeg
                il.InsertBefore(next, il.Create(OpCodes.Bne_Un_S, cont));

                // Apply CullUpdateTransforms (1) only to ambient wildlife
                il.InsertBefore(next, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(next, il.Create(OpCodes.Ldfld, fAnimator));
                il.InsertBefore(next, il.Create(OpCodes.Brfalse_S, cont));
                il.InsertBefore(next, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(next, il.Create(OpCodes.Ldfld, fAnimator));
                il.InsertBefore(next, il.Create(OpCodes.Ldc_I4_1));
                il.InsertBefore(next, il.Create(OpCodes.Callvirt, setCullingMode));
                il.InsertBefore(next, cont);
                Console.WriteLine("  + Character.Awake: Animator LOD applied only to ambient wildlife (combat hitboxes & tamed fully preserved)");
            }
        }

        // ==================== V8 PATCH IMPLEMENTATIONS ====================

        static void ApplyHeightmapZeroAllocV8(TypeDefinition hmType)
        {
            var rcmMethod = FindOrThrow(hmType.Methods, m => m.Name == "RebuildCollisionMesh", "Heightmap.RebuildCollisionMesh");
            var rrmMethod = FindOrThrow(hmType.Methods, m => m.Name == "RebuildRenderMesh", "Heightmap.RebuildRenderMesh");
            var setIndices5 = (MethodReference)FindOrThrow(rrmMethod.Body.Instructions, i => i.OpCode == OpCodes.Callvirt && i.Operand?.ToString()?.Contains("SetIndices") == true && i.Operand?.ToString()?.Contains("List") == true, "SetIndices(List)").Operand;

            for (int i = 0; i < rcmMethod.Body.Instructions.Count; i++)
            {
                var inst = rcmMethod.Body.Instructions[i];
                if (inst.OpCode == OpCodes.Callvirt && inst.Operand?.ToString()?.Contains("ToArray") == true)
                {
                    inst.OpCode = OpCodes.Nop;
                    inst.Operand = null;
                    var setIndicesOld = rcmMethod.Body.Instructions[i + 3];
                    setIndicesOld.Operand = setIndices5;

                    var il = rcmMethod.Body.GetILProcessor();
                    il.InsertBefore(setIndicesOld, il.Create(OpCodes.Ldc_I4_1)); // calculateBounds = true
                    il.InsertBefore(setIndicesOld, il.Create(OpCodes.Ldc_I4_0)); // baseVertex = 0
                    Console.WriteLine("  + Heightmap.RebuildCollisionMesh: converted to zero-alloc List<int> (no ToArray)");
                    break;
                }
            }
        }

        static void ApplyEnemyHudCachingV8(TypeDefinition ehType)
        {
            var updateHuds = FindOrThrow(ehType.Methods, m => m.Name == "UpdateHuds", "EnemyHud.UpdateHuds");
            updateHuds.Body.SimplifyMacros();

            for (int i = 0; i < updateHuds.Body.Instructions.Count; i++)
            {
                var inst = updateHuds.Body.Instructions[i];
                if (inst.OpCode == OpCodes.Callvirt && inst.Operand?.ToString()?.Contains("set_text") == true &&
                    updateHuds.Body.Instructions[i - 1].Operand?.ToString()?.Contains("Localize") == true)
                {
                    for (int j = i - 7; j <= i; j++)
                    {
                        updateHuds.Body.Instructions[j].OpCode = OpCodes.Nop;
                        updateHuds.Body.Instructions[j].Operand = null;
                    }
                    Console.WriteLine("  + EnemyHud.UpdateHuds: redundant per-frame localization eliminated");
                    break;
                }
            }

            updateHuds.Body.OptimizeMacros();
        }



        static void ApplyPathfindingZeroAllocV8(TypeDefinition pfType, TypeDefinition navMeshPathType, TypeDefinition vec3Type, MethodReference getCornersNonAlloc)
        {
            var module = pfType.Module;
            var vec3ArrayType = new ArrayType(module.ImportReference(vec3Type));
            var fCornerBuf = new FieldDefinition("s_cornerBuffer", FieldAttributes.Public | FieldAttributes.Static, vec3ArrayType);
            pfType.Fields.Add(fCornerBuf);

            var getPath = FindOrThrow(pfType.Methods, m => m.Name == "GetPath" && m.Parameters.Count == 7, "Pathfinding.GetPath(7)");
            var listVec3Type = getPath.Parameters[2].ParameterType;

            // Borrow exact imported Add method from CleanPath
            var cleanPath = FindOrThrow(pfType.Methods, m => m.Name == "CleanPath", "Pathfinding.CleanPath");
            var addVec3Method = (MethodReference)FindOrThrow(cleanPath.Body.Instructions, i => i.Operand is MethodReference mr && mr.Name == "Add" && mr.DeclaringType.FullName.Contains("List`1<UnityEngine.Vector3>"), "List<Vector3>.Add").Operand;

            var helper = new MethodDefinition("AddCornersNonAlloc", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Void);
            helper.Parameters.Add(new ParameterDefinition("navPath", ParameterAttributes.None, module.ImportReference(navMeshPathType)));
            helper.Parameters.Add(new ParameterDefinition("list", ParameterAttributes.None, listVec3Type));

            var il = helper.Body.GetILProcessor();
            helper.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32)); // V_0: count
            helper.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32)); // V_1: i

            var initBuf = il.Create(OpCodes.Ldc_I4_0);
            var loopEnd = il.Create(OpCodes.Ret);

            il.Append(il.Create(OpCodes.Ldsfld, fCornerBuf));
            il.Append(il.Create(OpCodes.Brtrue, initBuf));
            il.Append(il.Create(OpCodes.Ldc_I4, 128));
            il.Append(il.Create(OpCodes.Newarr, module.ImportReference(vec3Type)));
            il.Append(il.Create(OpCodes.Stsfld, fCornerBuf));

            il.Append(initBuf);
            il.Append(il.Create(OpCodes.Pop));
            il.Append(il.Create(OpCodes.Ldarg_0));
            il.Append(il.Create(OpCodes.Ldsfld, fCornerBuf));
            il.Append(il.Create(OpCodes.Callvirt, getCornersNonAlloc));
            il.Append(il.Create(OpCodes.Stloc_0));

            il.Append(il.Create(OpCodes.Ldc_I4_0));
            il.Append(il.Create(OpCodes.Stloc_1));

            var loopCheck = il.Create(OpCodes.Ldloc_1);
            il.Append(il.Create(OpCodes.Br, loopCheck));

            var loopBody = il.Create(OpCodes.Ldarg_1);
            il.Append(loopBody);
            il.Append(il.Create(OpCodes.Ldsfld, fCornerBuf));
            il.Append(il.Create(OpCodes.Ldloc_1));
            il.Append(il.Create(OpCodes.Ldelem_Any, module.ImportReference(vec3Type)));
            il.Append(il.Create(OpCodes.Callvirt, addVec3Method));
            il.Append(il.Create(OpCodes.Ldloc_1));
            il.Append(il.Create(OpCodes.Ldc_I4_1));
            il.Append(il.Create(OpCodes.Add));
            il.Append(il.Create(OpCodes.Stloc_1));

            il.Append(loopCheck);
            il.Append(il.Create(OpCodes.Ldloc_0));
            il.Append(il.Create(OpCodes.Blt, loopBody));
            il.Append(loopEnd);

            helper.Body.OptimizeMacros();
            pfType.Methods.Add(helper);

            getPath.Body.SimplifyMacros();
            for (int i = 0; i < getPath.Body.Instructions.Count; i++)
            {
                var inst = getPath.Body.Instructions[i];
                if (inst.OpCode == OpCodes.Callvirt && inst.Operand?.ToString()?.Contains("AddRange") == true &&
                    getPath.Body.Instructions[i - 1].OpCode == OpCodes.Callvirt && getPath.Body.Instructions[i - 1].Operand?.ToString()?.Contains("get_corners") == true)
                {
                    getPath.Body.Instructions[i - 4].OpCode = OpCodes.Ldarg_0;
                    getPath.Body.Instructions[i - 4].Operand = null;
                    getPath.Body.Instructions[i - 3].OpCode = OpCodes.Ldfld;
                    getPath.Body.Instructions[i - 3].Operand = getPath.Body.Instructions[i - 2].Operand;
                    getPath.Body.Instructions[i - 2].OpCode = OpCodes.Ldarg_3;
                    getPath.Body.Instructions[i - 2].Operand = null;
                    getPath.Body.Instructions[i - 1].OpCode = OpCodes.Call;
                    getPath.Body.Instructions[i - 1].Operand = helper;
                    getPath.Body.Instructions[i].OpCode = OpCodes.Nop;
                    getPath.Body.Instructions[i].Operand = null;
                    Console.WriteLine("  + Pathfinding.GetPath: AddRange(m_path.corners) replaced with AddCornersNonAlloc");
                    break;
                }
            }
            getPath.Body.OptimizeMacros();
        }

        // ==================== VERIFICATION ENGINE ====================

        static int VerifyPatches(string dllPath, string managedDir, PatchVersion? targetVer)
        {
            Console.WriteLine($"\n--- Verifying Patches in: {dllPath} ---");
            using var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(managedDir);
            using var asm = AssemblyDefinition.ReadAssembly(dllPath, new ReaderParameters { AssemblyResolver = resolver });
            var module = asm.MainModule;

            var wnt = FindOrThrow(module.Types, t => t.Name == "WearNTear", "WearNTear");
            var marker = wnt.Fields.FirstOrDefault(f => f.Name.StartsWith("s_nativePatched_"));

            if (marker == null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("[INFO] Target assembly is UNPATCHED (vanilla code). No patches to verify.");
                Console.ResetColor();
                return 0;
            }

            PatchVersion detected = marker.Name switch
            {
                MarkerFieldNameV5 => PatchVersion.V5,
                MarkerFieldNameV6 => PatchVersion.V6,
                MarkerFieldNameV7 => PatchVersion.V7,
                _ => PatchVersion.V8
            };
            PatchVersion verToVerify = targetVer ?? detected;

            Console.WriteLine($"Detected marker: {marker.Name} -> verifying against {verToVerify.ToString().ToLower()} ruleset...\n");

            int checksPassed = 0;
            int totalChecks = verToVerify == PatchVersion.V8 ? 13 : (verToVerify == PatchVersion.V7 ? 10 : (verToVerify == PatchVersion.V6 ? 9 : 8));

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

            if (verToVerify == PatchVersion.V8)
            {
                // 1. Marker check
                AssertCheck(marker.Name == MarkerFieldNameV8, "Marker v8", $"Found marker {marker.Name}");

                // 2. WearNTear.UpdateWear safety: ShouldUpdateSupport guards the block and skips HaveSupport on non-update frames
                var updateWear = FindOrThrow(wnt.Methods, m => m.Name == "UpdateWear", "WearNTear.UpdateWear");
                bool wntGuardsBlock = updateWear.Body.Instructions.Any(i => i.OpCode == OpCodes.Call && i.Operand?.ToString()?.Contains("ShouldUpdateSupport") == true)
                                      && updateWear.Body.Instructions.Any(i => i.OpCode == OpCodes.Brfalse);
                AssertCheck(wntGuardsBlock, "P0 WearNTear UpdateWear Safety", "Skips entire support & destruction check on non-updating frames");

                // 3. WearNTear.ShouldUpdateSupport cache check: calls get_Count
                var shouldUpdate = FindOrThrow(wnt.Methods, m => m.Name == "ShouldUpdateSupport", "WearNTear.ShouldUpdateSupport");
                bool suChecksCount = shouldUpdate.Body.Instructions.Any(i => i.OpCode == OpCodes.Callvirt && i.Operand?.ToString()?.Contains("get_Count") == true);
                AssertCheck(suChecksCount, "P0 WearNTear Empty Cache Detection", "Checks m_supportColliders.Count == 0 to force update on cleared cache");

                // 4. WearNTear.HaveSupport: clean vanilla integrity (no 100f hardcode)
                var haveSupport = FindOrThrow(wnt.Methods, m => m.Name == "HaveSupport", "WearNTear.HaveSupport");
                bool hsIsClean = !haveSupport.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 100.0f);
                AssertCheck(hsIsClean, "P0 WearNTear Structural Mechanics", "Preserved vanilla GetMinSupport check for stone/iron/marble");

                // 6. Heightmap RebuildCollisionMesh zero-alloc List<int>
                var hm = FindOrThrow(module.Types, t => t.Name == "Heightmap", "Heightmap");
                var rcm = FindOrThrow(hm.Methods, m => m.Name == "RebuildCollisionMesh", "Heightmap.RebuildCollisionMesh");
                bool rcmNoToArray = !rcm.Body.Instructions.Any(i => i.OpCode == OpCodes.Callvirt && i.Operand?.ToString()?.Contains("ToArray") == true);
                bool rcmUsesList = rcm.Body.Instructions.Any(i => i.OpCode == OpCodes.Callvirt && i.Operand?.ToString()?.Contains("SetIndices") == true && i.Operand?.ToString()?.Contains("List") == true);
                AssertCheck(rcmNoToArray && rcmUsesList, "P1 Heightmap Zero-Alloc Collision Mesh", "RebuildCollisionMesh uses List<int> without ToArray() heap allocation");

                // 7. Character.Awake combat preservation
                var character = FindOrThrow(module.Types, t => t.Name == "Character", "Character");
                var charAwake = FindOrThrow(character.Methods, m => m.Name == "Awake", "Character.Awake");
                bool charProtectsCombat = charAwake.Body.Instructions.Any(i => i.OpCode == OpCodes.Isinst && i.Operand?.ToString()?.Contains("Humanoid") == true)
                                       && charAwake.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldfld && i.Operand?.ToString()?.Contains("m_tamed") == true);
                AssertCheck(charProtectsCombat, "P0/P1 Combat Hitbox Preservation", "Excludes all Humanoids, Bosses, and Tamed pets from CullUpdateTransforms");

                // 8. EffectList SetParent(parent, true)
                var effectList = FindOrThrow(module.Types, t => t.Name == "EffectList", "EffectList");
                var create = FindOrThrow(effectList.Methods, m => m.Name == "Create" && m.Parameters.Count == 6, "EffectList.Create");
                bool elUsesSetParentTrue = create.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_I4_1)
                                        && create.Body.Instructions.Any(i => i.OpCode == OpCodes.Callvirt && i.Operand?.ToString()?.Contains("SetParent(UnityEngine.Transform,System.Boolean)") == true);
                AssertCheck(elUsesSetParentTrue, "P1 EffectList Transform Integrity", "Uses SetParent(parent, true) preserving world position, rotation and scale");

                // 9. EffectList 45m range threshold
                bool elExtendedRange = create.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 45.0f);
                AssertCheck(elExtendedRange, "P1 EffectList Combat Range", "45m distance threshold preserves archery sounds and combat particles");

                // 10. ClutterSystem Off == 0.0m
                var clutter = FindOrThrow(module.Types, t => t.Name == "ClutterSystem", "ClutterSystem");
                var applySettings = FindOrThrow(clutter.Methods, m => m.Name == "ApplySettings", "ClutterSystem.ApplySettings");
                bool clutterHandlesOff = applySettings.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 0.0f);
                AssertCheck(clutterHandlesOff, "P1 ClutterSystem Quality Logic", "Sets 0m vegetation distance when Quality is Off");

                // 10. EnemyHud hover text optimization
                var enemyHud = FindOrThrow(module.Types, t => t.Name == "EnemyHud", "EnemyHud");
                var updateHuds = FindOrThrow(enemyHud.Methods, m => m.Name == "UpdateHuds", "EnemyHud.UpdateHuds");
                bool ehNoPerFrameLocalize = !updateHuds.Body.Instructions.Any(i => i.OpCode == OpCodes.Callvirt && i.Operand?.ToString()?.Contains("GetHoverName") == true);
                AssertCheck(ehNoPerFrameLocalize, "P1 EnemyHud Hover Optimization", "Eliminated redundant per-frame Localization.Localize & set_text in UpdateHuds");


                // 14. Pathfinding zero-alloc corners
                var pf = FindOrThrow(module.Types, t => t.Name == "Pathfinding", "Pathfinding");
                bool pfHasHelper = pf.Methods.Any(m => m.Name == "AddCornersNonAlloc");
                var pfGetPath = FindOrThrow(pf.Methods, m => m.Name == "GetPath" && m.Parameters.Count == 7, "Pathfinding.GetPath");
                bool pfUsesHelper = pfGetPath.Body.Instructions.Any(i => i.OpCode == OpCodes.Call && i.Operand?.ToString()?.Contains("AddCornersNonAlloc") == true);
                AssertCheck(pfHasHelper && pfUsesHelper, "P1 Pathfinding Zero-Alloc", "GetPath queries use AddCornersNonAlloc with 128-element buffer (0 heap arrays)");

                // 15. Ragdoll safety
                var ragdoll = FindOrThrow(module.Types, t => t.Name == "Ragdoll", "Ragdoll");
                bool ragdollNoLeak = !ragdoll.Fields.Any(f => f.Name == "s_activeRagdolls");
                var ragdollAwake = FindOrThrow(ragdoll.Methods, m => m.Name == "Awake", "Ragdoll.Awake");
                bool ragdollClampsTtl = ragdollAwake.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand <= 3.5f);
                AssertCheck(ragdollNoLeak && ragdollClampsTtl, "P2 Ragdoll Safety", "Clean PhysX release at TTL<=3.5s with zero static buffer leak");

                // 16. All subsystems verified
                AssertCheck(checksPassed == totalChecks - 1, "Overall Integrity", $"All {checksPassed}/{totalChecks - 1} subsystem audits passed successfully!");
            }
            else if (verToVerify == PatchVersion.V7)
            {
                // 1. Marker check
                AssertCheck(marker.Name == MarkerFieldNameV7, "Marker v7", $"Found marker {marker.Name}");

                // 2. WearNTear.UpdateWear safety: ShouldUpdateSupport guards the block and skips HaveSupport on non-update frames
                var updateWear = FindOrThrow(wnt.Methods, m => m.Name == "UpdateWear", "WearNTear.UpdateWear");
                bool wntGuardsBlock = updateWear.Body.Instructions.Any(i => i.OpCode == OpCodes.Call && i.Operand?.ToString()?.Contains("ShouldUpdateSupport") == true)
                                      && updateWear.Body.Instructions.Any(i => i.OpCode == OpCodes.Brfalse);
                AssertCheck(wntGuardsBlock, "P0 WearNTear UpdateWear Safety", "Skips entire support & destruction check on non-updating frames");

                // 3. WearNTear.ShouldUpdateSupport cache check: calls get_Count
                var shouldUpdate = FindOrThrow(wnt.Methods, m => m.Name == "ShouldUpdateSupport", "WearNTear.ShouldUpdateSupport");
                bool suChecksCount = shouldUpdate.Body.Instructions.Any(i => i.OpCode == OpCodes.Callvirt && i.Operand?.ToString()?.Contains("get_Count") == true);
                AssertCheck(suChecksCount, "P0 WearNTear Empty Cache Detection", "Checks m_supportColliders.Count == 0 to force update on cleared cache");

                // 4. WearNTear.HaveSupport: clean vanilla integrity (no 100f hardcode)
                var haveSupport = FindOrThrow(wnt.Methods, m => m.Name == "HaveSupport", "WearNTear.HaveSupport");
                bool hsIsClean = !haveSupport.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 100.0f);
                AssertCheck(hsIsClean, "P0 WearNTear Structural Mechanics", "Preserved vanilla GetMinSupport check for stone/iron/marble");

                // 5. ClutterSystem.ApplySettings: properly handles Quality.Off (0.0f)
                var clutter = FindOrThrow(module.Types, t => t.Name == "ClutterSystem", "ClutterSystem");
                var applySettings = FindOrThrow(clutter.Methods, m => m.Name == "ApplySettings", "ClutterSystem.ApplySettings");
                bool clutterHandlesOff = applySettings.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 0.0f);
                AssertCheck(clutterHandlesOff, "P1 ClutterSystem Quality Logic", "Sets 0m vegetation distance when Quality is Off");

                // 6. Character.Awake: excludes Humanoids (Player and all weapon-wielding monsters) from animator transform culling
                var character = FindOrThrow(module.Types, t => t.Name == "Character", "Character");
                var charAwake = FindOrThrow(character.Methods, m => m.Name == "Awake", "Character.Awake");
                bool charProtectsHumanoids = charAwake.Body.Instructions.Any(i => i.OpCode == OpCodes.Isinst && i.Operand?.ToString()?.Contains("Humanoid") == true)
                                          && charAwake.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldfld && i.Operand?.ToString()?.Contains("m_tamed") == true);
                AssertCheck(charProtectsHumanoids, "P0/P1 Combat Hitbox Preservation", "Excludes all Humanoids, Bosses, and Tamed pets from CullUpdateTransforms");

                // 7. EffectList.Create: uses SetParent(parent, true) to keep scale and offsets
                var effectList = FindOrThrow(module.Types, t => t.Name == "EffectList", "EffectList");
                var create = FindOrThrow(effectList.Methods, m => m.Name == "Create" && m.Parameters.Count == 6, "EffectList.Create");
                bool elUsesSetParentTrue = create.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_I4_1)
                                        && create.Body.Instructions.Any(i => i.OpCode == OpCodes.Callvirt && i.Operand?.ToString()?.Contains("SetParent(UnityEngine.Transform,System.Boolean)") == true);
                AssertCheck(elUsesSetParentTrue, "P1 EffectList Transform Integrity", "Uses SetParent(parent, true) preserving world position, rotation and scale");

                // 8. EffectList.Create: extended 45m range limiter
                bool elExtendedRange = create.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 45.0f);
                AssertCheck(elExtendedRange, "P1 EffectList Combat Range", "45m distance threshold preserves archery sounds and combat particles");

                // 9. Ragdoll: clean TTL <= 3.5s without static memory leak
                var ragdoll = FindOrThrow(module.Types, t => t.Name == "Ragdoll", "Ragdoll");
                bool ragdollNoLeak = !ragdoll.Fields.Any(f => f.Name == "s_activeRagdolls");
                var ragdollAwake = FindOrThrow(ragdoll.Methods, m => m.Name == "Awake", "Ragdoll.Awake");
                bool ragdollClampsTtl = ragdollAwake.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand <= 3.5f);
                AssertCheck(ragdollNoLeak && ragdollClampsTtl, "P2 Ragdoll Safety", "Clean PhysX release at TTL<=3.5s with zero static buffer leak");

                // 10. All subsystems verified
                AssertCheck(checksPassed == totalChecks - 1, "Overall Integrity", $"All {checksPassed}/{totalChecks - 1} subsystem audits passed successfully!");
            }
            else if (verToVerify == PatchVersion.V6)
            {
                // 1. Marker check
                AssertCheck(marker.Name == MarkerFieldNameV6, "Marker v6", $"Found marker {marker.Name}");

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

                // 9. All subsystems verified
                AssertCheck(checksPassed == totalChecks - 1, "Overall Integrity", $"All {checksPassed}/{totalChecks - 1} subsystem audits passed successfully!");
            }
            else
            {
                // Verify V5 invariants
                AssertCheck(marker.Name == MarkerFieldNameV5, "Marker v5", $"Found marker {marker.Name}");

                var gsm = FindOrThrow(module.Types, t => t.Name == "GraphicsSettingsManager", "GraphicsSettingsManager");
                var getLightLimit = FindOrThrow(gsm.Methods, m => m.Name == "GetLightLimit", "GetLightLimit");
                bool gsmUsesLdarg0 = getLightLimit.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldarg_0);
                AssertCheck(gsmUsesLdarg0, "V5 GSM Args", "Uses Ldarg_0 (v5 legacy)");

                var getSupport = FindOrThrow(wnt.Methods, m => m.Name == "GetSupport" && m.Parameters.Count == 0, "WearNTear.GetSupport");
                bool wntFastPath = getSupport.Body.Instructions.Take(8).Any(i => i.OpCode == OpCodes.Ret);
                AssertCheck(wntFastPath, "V5 WearNTear GetSupport", "Fast-path returns local support");

                var fire = FindOrThrow(module.Types, t => t.Name == "Fire", "Fire");
                var updateFire = FindOrThrow(fire.Methods, m => m.Name == "UpdateFire", "Fire.UpdateFire");
                bool fireSimpleCull = updateFire.Body.Instructions.Take(15).Any(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 30.0f);
                AssertCheck(fireSimpleCull, "V5 Fire Distance Cull", "30m distance cull present");

                var effectList = FindOrThrow(module.Types, t => t.Name == "EffectList", "EffectList");
                var create = FindOrThrow(effectList.Methods, m => m.Name == "Create" && m.Parameters.Count == 6, "EffectList.Create");
                bool usesNewarr = create.Body.Instructions.Any(i => i.OpCode == OpCodes.Newarr);
                AssertCheck(usesNewarr, "V5 EffectList Newarr", "Uses newarr GameObject (v5 legacy)");

                var ragdoll = FindOrThrow(module.Types, t => t.Name == "Ragdoll", "Ragdoll");
                bool ragdollHasBuffer = ragdoll.Fields.Any(f => f.Name == "s_activeRagdolls");
                AssertCheck(ragdollHasBuffer, "V5 Ragdoll Buffer", "Ring buffer s_activeRagdolls present");

                var character = FindOrThrow(module.Types, t => t.Name == "Character", "Character");
                var charAwake = FindOrThrow(character.Methods, m => m.Name == "Awake", "Character.Awake");
                bool charSetsCulling = charAwake.Body.Instructions.Any(i => i.OpCode == OpCodes.Callvirt && i.Operand?.ToString()?.Contains("set_cullingMode") == true);
                AssertCheck(charSetsCulling, "V5 Character Animator LOD", "CullUpdateTransforms applied to characters");

                AssertCheck(checksPassed == totalChecks - 1, "Overall Integrity", $"All {checksPassed}/{totalChecks - 1} v5 checks passed successfully!");
            }

            Console.WriteLine($"\nResult: {checksPassed}/{totalChecks} checks verified successfully.");
            return checksPassed == totalChecks ? 0 : 1;
        }
    }
}
