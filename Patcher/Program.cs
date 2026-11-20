using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
        V8,
        V9
    }

    class Program
    {
        public const string MarkerFieldNameV5 = "s_nativePatched_v5";
        public const string MarkerFieldNameV6 = "s_nativePatched_v6";
        public const string MarkerFieldNameV7 = "s_nativePatched_v7";
        public const string MarkerFieldNameV8 = "s_nativePatched_v8";
        // v9.16: vanilla light limits and LightLod ranges again (lights reach as far as in the unpatched game).
        // v9.15: ZNetScene survives instances destroyed by other code (vanilla RemoveObjects then threw forever: nothing unloaded).
        // v9.14: expired-zone unloading check no longer rescans every scene object per zone (grew to 50 ms ticks over a session).
        // v9.13: reflection probes without sun shadows, re-rendered every 12 s while the player stands still.
        // v9.12: SunShafts skipped while the sun is off screen; reflection probes rendered one face per frame.
        // v9.11: sun shadows of small layers (items, effects, decorations, mobs) culled closer than the shadow distance.
        // v9.10: navmesh link removal time-bounded (was up to 15 ms/frame in Mistlands).
        // v9.9: navmesh tile rebuild interval x3 (Pathfinding), lower Mistlands particle caps (tunable via mist_cap file).
        // v9.8: Mistlands fog rewritten (NativePatchMist.dll): cached mister geometry, emission spread over frames, no buried particles, particle cap.
        // v9.6: bulk minimap serialization on save, DepthCamera skips re-rendering an unchanged view.
        // v9.5: fully vanilla object streaming (throttling it made nearby mobs/pickables pop in late).
        // v9.3: vanilla terrain cooking options. v9.2: heightmap rebuild budget.
        // Bumping the marker makes "ensure" upgrade older v9 installs.
        public const string MarkerFieldNameV9 = "s_nativePatched_v9_16";

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

        /// <summary>Exit code of "ensure" when the patch was missing and has just been applied (caller must re-sign on macOS).</summary>
        const int ExitPatchedNow = 3;

        static int Main(string[] args)
        {
            // Steam launch-option wrapper: "ValheimPatcher.exe ensure v9 -- %command%".
            // Everything after "--" is the game command; it is started even if patching fails.
            int sep = Array.IndexOf(args, "--");
            if (sep < 0) return Run(args);

            string[] gameCommand = args[(sep + 1)..];
            int result;
            try { result = Run(args[..sep]); }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] {ex.Message}");
                result = 1;
            }
            AppendLaunchLog(string.Join(" ", args[..sep]), result);
            return gameCommand.Length == 0 ? result : LaunchGame(gameCommand);
        }

        static void AppendLaunchLog(string patcherArgs, int result)
        {
            string outcome = result switch { 0 => "already patched", ExitPatchedNow => "patched now", _ => $"FAILED (exit {result}), game started unpatched" };
            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "autopatch.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {patcherArgs}: {outcome}{Environment.NewLine}"); }
            catch { /* logging must never block the game launch */ }
        }

        [DllImport("kernel32.dll")]
        static extern bool FreeConsole();

        static int LaunchGame(string[] command)
        {
            string exe = Path.GetFullPath(command[0]);
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" };
            foreach (var a in command.Skip(1)) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi)!;
            // Close our console window but stay alive, so Steam keeps tracking the session (playtime, overlay).
            if (OperatingSystem.IsWindows()) FreeConsole();
            proc.WaitForExit();
            return proc.ExitCode;
        }

        static string MarkerFor(PatchVersion v) => v switch
        {
            PatchVersion.V9 => MarkerFieldNameV9,
            PatchVersion.V8 => MarkerFieldNameV8,
            PatchVersion.V7 => MarkerFieldNameV7,
            PatchVersion.V6 => MarkerFieldNameV6,
            _ => MarkerFieldNameV5
        };

        static string? ReadMarker(string dllPath, string managedDir)
        {
            using var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(managedDir);
            using var asm = AssemblyDefinition.ReadAssembly(dllPath, new ReaderParameters { AssemblyResolver = resolver });
            var wnt = asm.MainModule.Types.FirstOrDefault(t => t.Name == "WearNTear");
            return wnt?.Fields.FirstOrDefault(f => f.Name.StartsWith("s_nativePatched_"))?.Name;
        }

        static int Run(string[] args)
        {
            PatchVersion targetVersion = PatchVersion.V9;
            bool restoreMode = false;
            bool statusMode = false;
            bool verifyMode = false;
            bool ensureMode = false;
            bool profile = false;
            PatchVersion? verifyVersion = null;

            int physicsSteps = 4;
            int? spawnLimitArg = null;
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
                else if (string.Equals(arg, "v9", StringComparison.OrdinalIgnoreCase))
                {
                    targetVersion = PatchVersion.V9;
                    verifyVersion = PatchVersion.V9;
                }
                else if ((arg == "--version" || arg == "-v") && i + 1 < args.Length)
                {
                    string vStr = args[++i].Trim().ToLowerInvariant();
                    if (vStr == "5" || vStr == "v5") { targetVersion = PatchVersion.V5; verifyVersion = PatchVersion.V5; }
                    else if (vStr == "6" || vStr == "v6") { targetVersion = PatchVersion.V6; verifyVersion = PatchVersion.V6; }
                    else if (vStr == "7" || vStr == "v7") { targetVersion = PatchVersion.V7; verifyVersion = PatchVersion.V7; }
                    else if (vStr == "8" || vStr == "v8") { targetVersion = PatchVersion.V8; verifyVersion = PatchVersion.V8; }
                    else if (vStr == "9" || vStr == "v9") { targetVersion = PatchVersion.V9; verifyVersion = PatchVersion.V9; }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"[ERROR] Unknown version '{vStr}'. Supported versions: v5, v6, v7, v8, v9");
                        Console.ResetColor();
                        return 1;
                    }
                }
                else if (arg == "--profile" || string.Equals(arg, "profile", StringComparison.OrdinalIgnoreCase))
                {
                    profile = true;
                }
                else if (arg == "--ensure" || string.Equals(arg, "ensure", StringComparison.OrdinalIgnoreCase))
                {
                    ensureMode = true;
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
                        else if (next == "v9" || next == "9") { verifyVersion = PatchVersion.V9; i++; }
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
                    spawnLimitArg = Math.Max(1, sl); i++;
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
                PatchVersion.V9 => "v9.16 (v7 + Vanilla-Correct Support, Fire & Object Streaming, Terrain Rebuild Budget, Fast Autosave, Lean Mistlands Fog, Pathfinding Rebuild Throttle, Sun Shadow Layer Cull, Idle Sun Shafts Skip, Lean Reflection Probe, Zone Unload Check, Scene Unload Fix - Recommended)",
                PatchVersion.V8 => "v8 (Zero-Stutter Anti-Pattern Elimination)",
                PatchVersion.V7 => "v7 (Engine & Gameplay Fixes Edition)",
                PatchVersion.V6 => "v6 (Robust Network & Gameplay Edition)",
                _ => "v5 (Legacy Optimization Edition)"
            };
            Console.WriteLine("=========================================================");
            if (OperatingSystem.IsWindows())
            {
                Console.WriteLine("     Valheim Native IL Patcher (Windows x64)             ");
                Console.WriteLine("     Zero BepInEx, direct IL patching                    ");
            }
            else
            {
                Console.WriteLine("     Valheim Native IL Patcher (macOS Apple Silicon)     ");
                Console.WriteLine("     Zero Rosetta, Zero BepInEx, Native arm64 Mono       ");
            }
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
                    // Windows layout: <Valheim>/valheim_Data/Managed
                    string candidateWin = Path.Combine(current, "valheim_Data", "Managed");
                    if (Directory.Exists(candidateWin)) { managedDir = candidateWin; break; }
                    var parent = Directory.GetParent(current);
                    current = parent?.FullName;
                }
            }

            if (string.IsNullOrEmpty(managedDir) && OperatingSystem.IsWindows())
            {
                // Patcher not placed inside the game folder: try the default Steam library
                string defaultSteam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps", "common", "Valheim", "valheim_Data", "Managed");
                if (Directory.Exists(defaultSteam)) managedDir = defaultSteam;
            }

            if (string.IsNullOrEmpty(managedDir))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[ERROR] Could not auto-detect Managed directory. Put the patcher inside the Valheim folder or use --managed <path>.");
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

            int spawnLimit = spawnLimitArg ?? 25;

            if (statusMode) { CheckStatus(dllPath, bakPath, managedDir); return 0; }
            if (restoreMode) return RestoreBackup(dllPath, bakPath);
            if (verifyMode) return VerifyPatches(dllPath, managedDir, verifyVersion);

            if (ensureMode)
            {
                string? found = ReadMarker(dllPath, managedDir);
                if (found == MarkerFor(targetVersion))
                {
                    Console.WriteLine($"[ENSURE] Already patched ({found}), nothing to do.");
                    return 0;
                }
                Console.WriteLine($"[ENSURE] Patch missing (found: {found ?? "vanilla"}), applying {targetVersion.ToString().ToLowerInvariant()}...");
                int applied = ApplyPatches(dllPath, bakPath, managedDir, physicsSteps, spawnLimit, updatesPerFrame, vegetationDistance, targetVersion, profile);
                return applied == 0 ? ExitPatchedNow : applied;
            }

            return ApplyPatches(dllPath, bakPath, managedDir, physicsSteps, spawnLimit, updatesPerFrame, vegetationDistance, targetVersion, profile);
        }

        static void PrintHelp()
        {
            Console.WriteLine("Usage: dotnet run --project Patcher [version] [options]");
            Console.WriteLine("       ./patch.sh [v5|v6|v7|v8|v9|--status|--verify|--restore]");
            Console.WriteLine("       ./start patch [v5|v6|v7|v8|v9|status|verify|restore]");
            Console.WriteLine("\nVersions:");
            Console.WriteLine("  v9                         Apply v9 patch set (Recommended, v7 + vanilla-correct support/fire, terrain rebuild budget)");
            Console.WriteLine("  v8                         Apply v8 patch set (Zero-stutter anti-pattern fixes)");
            Console.WriteLine("  v7                         Apply v7 patch set (Engine bugfixes & combat stability)");
            Console.WriteLine("  v6                         Apply v6 patch set (Legacy network & stability fixes)");
            Console.WriteLine("  v5                         Apply v5 patch set (Legacy optimization set)");
            Console.WriteLine("\nOptions:");
            Console.WriteLine("  --status, status           Check current patch status and backup existence");
            Console.WriteLine("  --profile, profile          Also inject the section profiler (writes native_profile.log next to Player.log)");
            Console.WriteLine("  --ensure, ensure [vN]      Patch only if the requested version is not already applied (exit 0 = ok, 3 = patched now)");
            Console.WriteLine("  ... -- <game command>      Steam launch option wrapper, e.g.: ValheimPatcher.exe ensure v9 -- %command%");
            Console.WriteLine("  --verify [v5|v6|v7|v8|v9], verify Deep verify patch invariants in target assembly");
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
                if (marker.Name == MarkerFieldNameV9)
                    Console.WriteLine($"Status: ALREADY PATCHED (marker: {marker.Name} [v9.16 - Vanilla-Correct Engine Fixes])");
                else if (marker.Name == MarkerFieldNameV8)
                    Console.WriteLine($"Status: ALREADY PATCHED (marker: {marker.Name} [v8 - Zero Stutter & Stability])");
                else if (marker.Name == MarkerFieldNameV7)
                    Console.WriteLine($"Status: ALREADY PATCHED (marker: {marker.Name} [v7 - Engine & Gameplay Fixes])");
                else if (marker.Name == MarkerFieldNameV6)
                    Console.WriteLine($"Status: ALREADY PATCHED (marker: {marker.Name} [v6 - Robust & Stable])");
                else if (marker.Name == MarkerFieldNameV5)
                    Console.WriteLine($"Status: ALREADY PATCHED (marker: {marker.Name} [v5 - Legacy])");
                else
                    Console.WriteLine($"Status: ALREADY PATCHED (marker: {marker.Name})");
                if (wnt!.Fields.Any(f => f.Name == ProfilerMarkerField))
                    Console.WriteLine($"Profiler: INJECTED (writes native_profile.log; re-apply without 'profile' to remove)");
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
            string profilerDll = Path.Combine(Path.GetDirectoryName(dllPath)!, ProfilerAssemblyFile);
            if (File.Exists(profilerDll)) File.Delete(profilerDll);
            string mistDll = Path.Combine(Path.GetDirectoryName(dllPath)!, MistAssemblyFile);
            if (File.Exists(mistDll)) File.Delete(mistDll);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[SUCCESS] Vanilla assembly successfully restored!");
            Console.ResetColor();
            return 0;
        }

        static int ApplyPatches(string dllPath, string bakPath, string managedDir, int physicsSteps, int spawnLimit, int updatesPerFrame, float vegetationDistance, PatchVersion targetVersion, bool profile)
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
            var znetType = FindOrThrow(module.Types, t => t.Name == "ZNetScene", "ZNetScene");
            if (targetVersion == PatchVersion.V9)
            {
                // Vanilla: 10/frame, scaled up to Count/100 in dense areas. Throttling it (fixed caps, time budget)
                // left nearby mobs and pickables un-instantiated for seconds in bases and forests.
                Console.WriteLine("\n[3/15] ZNetScene: preserved vanilla object streaming (10/frame, scales with nearby object count)");
            }
            else
            {
                Console.WriteLine($"\n[3/15] Patching ZNetScene streaming limits (Max {spawnLimit} objects/frame)...");
                ApplyZNetScenePatches(znetType, mathfMinInt, spawnLimit);
            }

            // Patch 3: WearNTear Throttling & Support Caching
            var wntType = FindOrThrow(module.Types, t => t.Name == "WearNTear", "WearNTear");
            var updaterType = FindOrThrow(module.Types, t => t.Name == "WearNTearUpdater", "WearNTearUpdater");
            string verLabel = targetVersion.ToString().ToLowerInvariant();
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
            else if (targetVersion == PatchVersion.V9)
            {
                // v9: vanilla support logic (it already caches support colliders) + per-frame updater cap only.
                // The v7 1/8 frame stagger aliases with the updater's round-robin and can stall collapse indefinitely.
                Console.WriteLine($"\n[4/18] Patching WearNTear (v9 vanilla support logic, Max {updatesPerFrame} pieces/frame)...");
                ApplyWearNTearUpdaterCap(updaterType, updatesPerFrame);
                Console.WriteLine("  + WearNTear: Preserved vanilla UpdateSupport cache & HaveSupport (no frame stagger)");
            }
            else
            {
                Console.WriteLine($"\n[4/18] Patching WearNTear ({verLabel} robust, Max {updatesPerFrame} pieces/frame)...");
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
            // 30 = Unity default (FasterSimulation|MeshCleaning|WeldVertices|FastMidphase), i.e. a no-op.
            // v9 keeps it: dropping CookForFasterSimulation (16) made cooking cheaper but every PhysX step against
            // terrain slower, and sessions on it were physics/engine-bound. Rebuild spikes are handled by the regen budget.
            ApplyHeightmapPatches(heightmapType, setCookingOptions, 30);
            if (targetVersion == PatchVersion.V9)
            {
                var getRealtimeDouble = module.ImportReference(FindOrThrow(timeType.Methods, m => m.Name == "get_realtimeSinceStartupAsDouble", "Time.get_realtimeSinceStartupAsDouble"));
                ApplyHeightmapRegenBudget(heightmapType, getFrameCount, getRealtimeDouble, 0.004);

                Console.WriteLine("\n[6b/18] Patching Minimap save (bulk explored-map serialization) & DepthCamera (skip unchanged re-render)...");
                var minimapType = FindOrThrow(module.Types, t => t.Name == "Minimap", "Minimap");
                var zpackageType = FindOrThrow(module.Types, t => t.Name == "ZPackage", "ZPackage");
                ApplyMinimapBulkSave(minimapType, zpackageType, mscorlibAsm);

                var depthCameraType = FindOrThrow(module.Types, t => t.Name == "DepthCamera", "DepthCamera");
                var getTime = module.ImportReference(FindOrThrow(timeType.Methods, m => m.Name == "get_time", "Time.get_time"));
                var vec3Equality = module.ImportReference(FindOrThrow(vec3Type.Methods, m => m.Name == "op_Equality", "Vector3.op_Equality"));
                ApplyDepthCameraSkipUnchanged(depthCameraType, FindOrThrow(module.Types, t => t.Name == "ZNetScene", "ZNetScene"), getTime, vec3Equality, opImplicit, 5f);

                Console.WriteLine("\n[6c/18] Pathfinding navmesh rebuild interval (x3) & Mistlands fog...");
                var pathfindingType = FindOrThrow(module.Types, t => t.Name == "Pathfinding", "Pathfinding");
                ApplyPathfindingRebuildInterval(pathfindingType, 3f);
                ApplyPathfindingLinkRemovalBudget(pathfindingType);
                Console.WriteLine("  Replacing Mistlands fog emitter (ParticleMist.Update) with the lean NativePatchMist implementation...");
                ApplyMistRewrite(module, managedDir);
                ApplyRenderTweaks(module, managedDir);
                ApplyZoneTtlCheck(module, managedDir);
                ApplySceneObjectsFix(module, managedDir);
            }
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
            else if (targetVersion == PatchVersion.V9)
            {
                // v9.16: vanilla light counts and LightLod ranges (2/4/8 pixel, 4/15/40/unlimited point lights), soft particles still off
                ApplySoftParticlesOff(gsmType);
                Console.WriteLine("  + Lights: vanilla limits and LightLod distances, softParticles=false");
            }
            else
                ApplyForwardRenderingPatchesV6(gsmType, lightLodType, 4, 6, 8);

            // Patch 8b: Sun shadow cull distances for small layers
            if (targetVersion == PatchVersion.V9)
            {
                Console.WriteLine("\n[8b/18] Patching sun shadow cull distances for small-object layers...");
                var lightType = FindOrThrow(coreModuleAsm.MainModule.Types, t => t.FullName == "UnityEngine.Light", "UnityEngine.Light");
                var setLayerShadowCull = module.ImportReference(FindOrThrow(lightType.Methods, m => m.Name == "set_layerShadowCullDistances", "Light.set_layerShadowCullDistances"));
                var envManType = FindOrThrow(module.Types, t => t.Name == "EnvMan", "EnvMan");
                string? layerError = null;
                string[]? layerNames = null;
                try { layerNames = ReadLayerNames(Path.Combine(Path.GetDirectoryName(managedDir.TrimEnd(Path.DirectorySeparatorChar))!, "globalgamemanagers")); }
                catch (Exception ex) { layerError = ex.Message; }
                if (layerNames != null)
                    ApplySunShadowLayerCull(envManType, setLayerShadowCull, opImplicit, layerNames);
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"  ! Layer names unreadable ({layerError}), sun shadow cull skipped (vanilla shadows)");
                    Console.ResetColor();
                }
            }

            // Patch 9: LightFlicker Transform Hierarchy
            Console.WriteLine("\n[9/18] Patching LightFlicker to eliminate transform hierarchy invalidation...");
            var lightFlickerType = FindOrThrow(module.Types, t => t.Name == "LightFlicker", "LightFlicker");
            ApplyLightFlickerPatches(lightFlickerType);

            // Patch 10: Global Smoke Particle Cap
            Console.WriteLine("\n[10/18] Patching SmokeSpawner global particle & PhysX body cap...");
            var smokeSpawnerType = FindOrThrow(module.Types, t => t.Name == "SmokeSpawner", "SmokeSpawner");
            ApplySmokeSpawnerPatches(smokeSpawnerType);

            // Patch 11: Fire.Dot PhysX Throttle
            var fireType = FindOrThrow(module.Types, t => t.Name == "Fire", "Fire");
            if (targetVersion == PatchVersion.V9)
            {
                // Fire.Dot deals damage per tick, so a longer interval is a balance change, not an optimization.
                Console.WriteLine("\n[11/18] Fire.Dot: preserved vanilla 1.0s interval (damage & fuel burn per tick unchanged)");
            }
            else
            {
                Console.WriteLine("\n[11/18] Patching Fire.Dot PhysX OverlapSphere query interval...");
                ApplyFireDotInterval(fireType);
            }

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
                Console.WriteLine($"\n[12/18] Patching Fire ({verLabel} clean: preserved vanilla UpdateFire integrity, dotInterval={(targetVersion == PatchVersion.V9 ? "1.0s vanilla" : "2.0s active")})...");
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
                Console.WriteLine($"\n[13/18] Patching Ragdoll ({verLabel} TTL 3.5s clean PhysX release)...");
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
                Console.WriteLine($"\n[14/18] Patching EffectList global frame limiter ({verLabel} 45m combat range, <=16 free, <=32 cap)...");
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
                Console.WriteLine($"\n[15/18] Patching Character Animator LOD ({verLabel} CullUpdateTransforms only for ambient wildlife, full combat safety)...");
                ApplyCharacterAnimatorLODV7(charType, humanoidType, setCullingMode);
            }

            // ==================== V8 EXCLUSIVE ANTI-PATTERN FIXES (16) ====================
            if (targetVersion == PatchVersion.V8)
            {
                Console.WriteLine("\n[16/16] Patching EnemyHud per-frame localization removal...");
                var enemyHudType = FindOrThrow(module.Types, t => t.Name == "EnemyHud", "EnemyHud");
                ApplyEnemyHudCachingV8(enemyHudType);
            }

            // Optional profiling build (must run last so it measures the patched code)
            string profilerDllPath = Path.Combine(managedDir, ProfilerAssemblyFile);
            if (profile)
            {
                Console.WriteLine("\n[PROFILE] Injecting native section profiler (log: native_profile.log next to Player.log)...");
                ApplyProfiler(module, profilerDllPath);
            }
            else if (File.Exists(profilerDllPath))
            {
                File.Delete(profilerDllPath);
                Console.WriteLine($"Removed stale profiler helper: {profilerDllPath}");
            }
            if (targetVersion != PatchVersion.V9)
            {
                string staleMist = Path.Combine(managedDir, MistAssemblyFile);
                if (File.Exists(staleMist)) File.Delete(staleMist);
            }

            // Add marker field
            string activeMarker = MarkerFor(targetVersion);
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

        static void ApplyHeightmapPatches(TypeDefinition heightmapType, MethodReference setCookingOptions, int cookingOptions)
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
                    il.InsertBefore(next, il.Create(OpCodes.Ldc_I4_S, (sbyte)cookingOptions));
                    il.InsertBefore(next, il.Create(OpCodes.Callvirt, setCookingOptions));
                    il.InsertBefore(next, skip);
                    Console.WriteLine($"  + Heightmap.Awake: FastMidphase cooking ({cookingOptions})");
                    break;
                }
            }
        }

        /// <summary>
        /// Vanilla rebuilds every queued (Poke(2)) terrain chunk in the same LateUpdate, which the profiler showed
        /// as 10-47 ms spikes. Keep the first rebuild of each frame, then only continue while under the budget;
        /// the rest stay queued (m_doLateUpdate == 2) and rebuild on the following frames.
        /// </summary>
        static void ApplyHeightmapRegenBudget(TypeDefinition heightmapType, MethodReference getFrameCount, MethodReference getRealtimeDouble, double budgetSeconds)
        {
            var module = heightmapType.Module;
            var clu = FindOrThrow(heightmapType.Methods, m => m.Name == "CustomLateUpdate", "Heightmap.CustomLateUpdate");
            var instrs = clu.Body.Instructions;

            // Expected vanilla shape: ldarg.0; ldfld m_doLateUpdate; ldc.i4.2; bne.un(.s) RET; ldarg.0; ldc.i4.0; stfld m_doLateUpdate; ...; ret
            int bneIdx = -1;
            for (int i = 2; i < instrs.Count; i++)
                if ((instrs[i].OpCode == OpCodes.Bne_Un_S || instrs[i].OpCode == OpCodes.Bne_Un) && instrs[i - 1].OpCode == OpCodes.Ldc_I4_2 &&
                    instrs[i - 2].Operand is FieldReference fr && fr.Name == "m_doLateUpdate")
                { bneIdx = i; break; }
            var ret = instrs[instrs.Count - 1];
            if (bneIdx < 0 || ret.OpCode != OpCodes.Ret || instrs[bneIdx].Operand != ret)
                throw new InvalidOperationException("[PATCH ERROR] Heightmap.CustomLateUpdate: unexpected IL (expected m_doLateUpdate == 2 guard)");

            var fFrame = new FieldDefinition("s_nativeRegenFrame", FieldAttributes.Private | FieldAttributes.Static, module.TypeSystem.Int32);
            var fStart = new FieldDefinition("s_nativeRegenStart", FieldAttributes.Private | FieldAttributes.Static, module.TypeSystem.Double);
            heightmapType.Fields.Add(fFrame);
            heightmapType.Fields.Add(fStart);

            clu.Body.SimplifyMacros();
            var il = clu.Body.GetILProcessor();
            var cont = instrs[bneIdx + 1];
            var sameFrame = il.Create(OpCodes.Call, getRealtimeDouble);
            var block = new[]
            {
                // if (Time.frameCount != s_nativeRegenFrame) { s_nativeRegenFrame = Time.frameCount; s_nativeRegenStart = now; }
                il.Create(OpCodes.Call, getFrameCount),
                il.Create(OpCodes.Ldsfld, fFrame),
                il.Create(OpCodes.Beq, sameFrame),
                il.Create(OpCodes.Call, getFrameCount),
                il.Create(OpCodes.Stsfld, fFrame),
                il.Create(OpCodes.Call, getRealtimeDouble),
                il.Create(OpCodes.Stsfld, fStart),
                il.Create(OpCodes.Br, cont),
                // else if (now - s_nativeRegenStart > budget) return;  // stays queued for the next frame
                sameFrame,
                il.Create(OpCodes.Ldsfld, fStart),
                il.Create(OpCodes.Sub),
                il.Create(OpCodes.Ldc_R8, budgetSeconds),
                il.Create(OpCodes.Bgt, ret),
            };
            foreach (var b in block) il.InsertBefore(cont, b);
            clu.Body.OptimizeMacros();
            Console.WriteLine($"  + Heightmap.CustomLateUpdate: queued chunk rebuilds limited to {budgetSeconds * 1000:0} ms/frame (first always runs)");
        }

        /// <summary>
        /// Minimap.GetMapData (every character autosave) writes the two 2048x2048 explored BitArrays one bool at a time:
        /// ~8.4M BinaryWriter calls, 150-250 ms on the main thread. Writes the same bytes (0/1 per bit) in one call each.
        /// </summary>
        static void ApplyMinimapBulkSave(TypeDefinition minimapType, TypeDefinition zpackageType, AssemblyDefinition mscorlibAsm)
        {
            var module = minimapType.Module;
            var getMapData = FindOrThrow(minimapType.Methods, m => m.Name == "GetMapData" && m.Parameters.Count == 0, "Minimap.GetMapData");
            var fExplored = FindOrThrow(minimapType.Fields, f => f.Name == "m_explored", "Minimap.m_explored");
            var fExploredOthers = FindOrThrow(minimapType.Fields, f => f.Name == "m_exploredOthers", "Minimap.m_exploredOthers");
            var fPins = FindOrThrow(minimapType.Fields, f => f.Name == "m_pins", "Minimap.m_pins");
            var fTextureSize = FindOrThrow(minimapType.Fields, f => f.Name == "m_textureSize", "Minimap.m_textureSize");
            var fWriter = FindOrThrow(zpackageType.Fields, f => f.Name == "m_writer", "ZPackage.m_writer");

            var bitArrayType = FindOrThrow(mscorlibAsm.MainModule.Types, t => t.FullName == "System.Collections.BitArray", "System.Collections.BitArray");
            var bufferType = FindOrThrow(mscorlibAsm.MainModule.Types, t => t.FullName == "System.Buffer", "System.Buffer");
            var writerType = FindOrThrow(mscorlibAsm.MainModule.Types, t => t.FullName == "System.IO.BinaryWriter", "System.IO.BinaryWriter");
            var bitArrayRef = module.ImportReference(bitArrayType);
            var getLength = module.ImportReference(FindOrThrow(bitArrayType.Methods, m => m.Name == "get_Length", "BitArray.get_Length"));
            var copyTo = module.ImportReference(FindOrThrow(bitArrayType.Methods, m => m.Name == "CopyTo" && m.Parameters.Count == 2, "BitArray.CopyTo(Array,int)"));
            var blockCopy = module.ImportReference(FindOrThrow(bufferType.Methods, m => m.Name == "BlockCopy" && m.Parameters.Count == 5, "Buffer.BlockCopy"));
            var writeBytes = module.ImportReference(FindOrThrow(writerType.Methods, m => m.Name == "Write" && m.Parameters.Count == 1 &&
                                                                 m.Parameters[0].ParameterType.FullName == "System.Byte[]", "BinaryWriter.Write(byte[])"));

            // ZPackage.WriteBitsAsBytes(BitArray bits, int count): same output as count x Write(bits[i])
            var writeBits = new MethodDefinition("WriteBitsAsBytes", MethodAttributes.Public | MethodAttributes.HideBySig, module.TypeSystem.Void);
            writeBits.Parameters.Add(new ParameterDefinition("bits", ParameterAttributes.None, bitArrayRef));
            writeBits.Parameters.Add(new ParameterDefinition("count", ParameterAttributes.None, module.TypeSystem.Int32));
            var vBools = new VariableDefinition(new ArrayType(module.TypeSystem.Boolean));
            var vBytes = new VariableDefinition(new ArrayType(module.TypeSystem.Byte));
            writeBits.Body.Variables.Add(vBools);
            writeBits.Body.Variables.Add(vBytes);
            writeBits.Body.InitLocals = true;
            var w = writeBits.Body.GetILProcessor();
            // bool[] bools = new bool[bits.Length]; bits.CopyTo(bools, 0);
            w.Emit(OpCodes.Ldarg_1); w.Emit(OpCodes.Callvirt, getLength); w.Emit(OpCodes.Newarr, module.TypeSystem.Boolean); w.Emit(OpCodes.Stloc, vBools);
            w.Emit(OpCodes.Ldarg_1); w.Emit(OpCodes.Ldloc, vBools); w.Emit(OpCodes.Ldc_I4_0); w.Emit(OpCodes.Callvirt, copyTo);
            // byte[] bytes = new byte[count]; Buffer.BlockCopy(bools, 0, bytes, 0, count);  (bool is one byte, 0 or 1)
            w.Emit(OpCodes.Ldarg_2); w.Emit(OpCodes.Newarr, module.TypeSystem.Byte); w.Emit(OpCodes.Stloc, vBytes);
            w.Emit(OpCodes.Ldloc, vBools); w.Emit(OpCodes.Ldc_I4_0); w.Emit(OpCodes.Ldloc, vBytes); w.Emit(OpCodes.Ldc_I4_0); w.Emit(OpCodes.Ldarg_2);
            w.Emit(OpCodes.Call, blockCopy);
            // m_writer.Write(bytes);  raw, no length prefix
            w.Emit(OpCodes.Ldarg_0); w.Emit(OpCodes.Ldfld, fWriter); w.Emit(OpCodes.Ldloc, vBytes); w.Emit(OpCodes.Callvirt, writeBytes);
            w.Emit(OpCodes.Ret);
            zpackageType.Methods.Add(writeBits);

            // Replace both per-bit loops in GetMapData: from after "pkg.Write(m_textureSize)" up to the pin count ("m_pins.Count(...)")
            var body = getMapData.Body;
            body.SimplifyMacros();
            var instrs = body.Instructions;
            int sizeIdx = -1, pinsIdx = -1;
            for (int i = 1; i < instrs.Count - 1; i++)
            {
                if (sizeIdx < 0 && instrs[i].Operand is FieldReference fs && fs.Name == fTextureSize.Name &&
                    instrs[i + 1].Operand is MethodReference wi && wi.Name == "Write" && wi.Parameters[0].ParameterType.MetadataType == MetadataType.Int32)
                    sizeIdx = i + 1;
                if (sizeIdx >= 0 && instrs[i].OpCode == OpCodes.Ldarg && instrs[i + 1].Operand is FieldReference fp && fp.Name == fPins.Name)
                { pinsIdx = i; break; }
            }
            if (sizeIdx < 0 || pinsIdx < 0 || instrs[sizeIdx - 3].OpCode != OpCodes.Ldloc)
                throw new InvalidOperationException("[PATCH ERROR] Minimap.GetMapData: unexpected IL (expected Write(m_textureSize) ... m_pins)");
            var loops = instrs.Skip(sizeIdx + 1).Take(pinsIdx - sizeIdx - 1).ToList();
            int boolWrites = loops.Count(i => i.Operand is MethodReference mr && mr.Name == "Write" && mr.Parameters[0].ParameterType.MetadataType == MetadataType.Boolean);
            bool readsBoth = loops.Any(i => i.Operand is FieldReference f && f.Name == fExplored.Name) && loops.Any(i => i.Operand is FieldReference f && f.Name == fExploredOthers.Name);
            if (boolWrites != 2 || !readsBoth || loops.Count > 40)
                throw new InvalidOperationException($"[PATCH ERROR] Minimap.GetMapData: expected two per-bit write loops, found {boolWrites} Write(bool) in {loops.Count} instructions");
            if (instrs.Any(i => i.Operand is Instruction t && loops.Contains(t) && !loops.Contains(i)) || body.ExceptionHandlers.Any(h => loops.Contains(h.TryStart) || loops.Contains(h.HandlerStart)))
                throw new InvalidOperationException("[PATCH ERROR] Minimap.GetMapData: loop range is a branch target from outside");

            var pkgVar = (VariableDefinition)instrs[sizeIdx - 3].Operand;
            var il = body.GetILProcessor();
            foreach (var x in loops) il.Remove(x);
            var pinsStart = instrs[sizeIdx + 1];
            foreach (var bits in new[] { fExplored, fExploredOthers })
            {
                // pkg.WriteBitsAsBytes(bits, m_explored.Length)  (vanilla bounds both loops by m_explored.Length)
                il.InsertBefore(pinsStart, il.Create(OpCodes.Ldloc, pkgVar));
                il.InsertBefore(pinsStart, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(pinsStart, il.Create(OpCodes.Ldfld, bits));
                il.InsertBefore(pinsStart, il.Create(OpCodes.Ldarg_0));
                il.InsertBefore(pinsStart, il.Create(OpCodes.Ldfld, fExplored));
                il.InsertBefore(pinsStart, il.Create(OpCodes.Callvirt, getLength));
                il.InsertBefore(pinsStart, il.Create(OpCodes.Callvirt, writeBits));
            }
            body.OptimizeMacros();
            Console.WriteLine("  + Minimap.GetMapData: explored maps serialized in bulk (identical bytes, no per-bit writer calls)");
        }

        /// <summary>
        /// DepthCamera (rain/wetness "under a roof" mask) re-renders every piece and rock within 50 m from above once a second,
        /// at maximum LOD. Skips the render when the view is identical to the last one: same rounded position and same number
        /// of networked objects (any piece built/destroyed changes it). Re-renders at least every maxStaleSeconds regardless.
        /// </summary>
        static void ApplyDepthCameraSkipUnchanged(TypeDefinition depthCamType, TypeDefinition znetSceneType, MethodReference getTime,
                                                  MethodReference vec3Equality, MethodReference opImplicit, float maxStaleSeconds)
        {
            var module = depthCamType.Module;
            var render = FindOrThrow(depthCamType.Methods, m => m.Name == "RenderDepth", "DepthCamera.RenderDepth");
            var getInstance = FindOrThrow(znetSceneType.Methods, m => m.Name == "get_instance", "ZNetScene.get_instance");
            var nrOfInstances = FindOrThrow(znetSceneType.Methods, m => m.Name == "NrOfInstances" && m.Parameters.Count == 0, "ZNetScene.NrOfInstances");

            var body = render.Body;
            body.SimplifyMacros();
            var instrs = body.Instructions;
            // Expected: ...; ldarg.0; call Component::get_transform; ldloc pos; callvirt Transform::set_position; ... ; ret
            int setIdx = -1;
            for (int i = 3; i < instrs.Count; i++)
                if (instrs[i].Operand is MethodReference sp && sp.Name == "set_position" && instrs[i - 1].OpCode == OpCodes.Ldloc &&
                    instrs[i - 2].Operand is MethodReference gt && gt.Name == "get_transform" && instrs[i - 3].OpCode == OpCodes.Ldarg)
                { setIdx = i; break; }
            var ret = instrs[instrs.Count - 1];
            if (setIdx < 0 || ret.OpCode != OpCodes.Ret || instrs.Count(i => i.Operand is MethodReference r && r.Name == "RenderWithShader") != 1)
                throw new InvalidOperationException("[PATCH ERROR] DepthCamera.RenderDepth: unexpected IL (expected transform.position = pos before RenderWithShader)");
            var posVar = (VariableDefinition)instrs[setIdx - 1].Operand;
            var renderStart = instrs[setIdx - 3];

            var fRendered = new FieldDefinition("m_nativeRendered", FieldAttributes.Private, module.TypeSystem.Boolean);
            var fPos = new FieldDefinition("m_nativePos", FieldAttributes.Private, posVar.VariableType);
            var fCount = new FieldDefinition("m_nativeCount", FieldAttributes.Private, module.TypeSystem.Int32);
            var fTime = new FieldDefinition("m_nativeTime", FieldAttributes.Private, module.TypeSystem.Single);
            foreach (var f in new[] { fRendered, fPos, fCount, fTime }) depthCamType.Fields.Add(f);
            var vCount = new VariableDefinition(module.TypeSystem.Int32);
            body.Variables.Add(vCount);

            var il = body.GetILProcessor();
            var store = il.Create(OpCodes.Ldarg_0);
            var block = new[]
            {
                // No world loaded: always render (vanilla)
                il.Create(OpCodes.Call, getInstance),
                il.Create(OpCodes.Call, opImplicit),
                il.Create(OpCodes.Brfalse, renderStart),
                il.Create(OpCodes.Call, getInstance),
                il.Create(OpCodes.Callvirt, nrOfInstances),
                il.Create(OpCodes.Stloc, vCount),
                // if (m_nativeRendered && count == m_nativeCount && pos == m_nativePos && Time.time - m_nativeTime < maxStale) return;
                il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, fRendered), il.Create(OpCodes.Brfalse, store),
                il.Create(OpCodes.Ldloc, vCount), il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, fCount), il.Create(OpCodes.Bne_Un, store),
                il.Create(OpCodes.Ldloc, posVar), il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, fPos), il.Create(OpCodes.Call, vec3Equality), il.Create(OpCodes.Brfalse, store),
                il.Create(OpCodes.Call, getTime), il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, fTime), il.Create(OpCodes.Sub),
                il.Create(OpCodes.Ldc_R4, maxStaleSeconds), il.Create(OpCodes.Blt, ret),
                // Remember what this render shows
                store, il.Create(OpCodes.Ldc_I4_1), il.Create(OpCodes.Stfld, fRendered),
                il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldloc, vCount), il.Create(OpCodes.Stfld, fCount),
                il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldloc, posVar), il.Create(OpCodes.Stfld, fPos),
                il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Call, getTime), il.Create(OpCodes.Stfld, fTime),
            };
            foreach (var b in block) il.InsertBefore(renderStart, b);
            body.OptimizeMacros();
            Console.WriteLine($"  + DepthCamera.RenderDepth: skips identical views (same position & object count), re-renders at least every {maxStaleSeconds:0}s");
        }

        /// <summary>
        /// Pathfinding.Buildtiles rebuilds every tile that creatures poked more than m_updateInterval (5 s, serialized in the prefab)
        /// after its last build, so busy areas rebuild a tile on almost every 0.1 s tick; each finished build re-stitches the tile
        /// edges and, when the stitch points moved, queues all old links for NavMesh.RemoveLink. The interval used by Buildtiles is
        /// multiplied to cut that churn; a brand-new tile (never built) is still built immediately.
        /// </summary>
        static void ApplyPathfindingRebuildInterval(TypeDefinition pathfindingType, float factor)
        {
            var build = FindOrThrow(pathfindingType.Methods, m => m.Name == "Buildtiles", "Pathfinding.Buildtiles");
            var body = build.Body;
            var loads = body.Instructions.Where(i => i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference fr && fr.Name == "m_updateInterval").ToList();
            if (loads.Count != 1)
                throw new InvalidOperationException($"[PATCH ERROR] Pathfinding.Buildtiles: expected one read of m_updateInterval, found {loads.Count}");
            var il = body.GetILProcessor();
            il.InsertAfter(loads[0], il.Create(OpCodes.Mul));
            il.InsertAfter(loads[0], il.Create(OpCodes.Ldc_R4, factor));
            body.MaxStackSize = Math.Max(body.MaxStackSize, 8);
            Console.WriteLine($"  + Pathfinding.Buildtiles: tile rebuild interval x{factor:0.#} (5 s -> {5 * factor:0} s)");
        }

        /// <summary>
        /// Pathfinding.DestroyQueuedNavmeshData runs every frame and removes the first Count/40 queued links with no time check
        /// (only the removals beyond that are limited to 25 / 1 ms), so a long queue after link churn cost 4-15 ms per frame in
        /// Mistlands. The forced share becomes 0: each frame removes links until 25 are gone or 1 ms has passed (always at least
        /// one, so DestroyAllLinks still terminates). Stale links only linger a little longer before removal.
        /// </summary>
        static void ApplyPathfindingLinkRemovalBudget(TypeDefinition pathfindingType)
        {
            var destroy = FindOrThrow(pathfindingType.Methods, m => m.Name == "DestroyQueuedNavmeshData", "Pathfinding.DestroyQueuedNavmeshData");
            var body = destroy.Body;
            body.SimplifyMacros();
            var instrs = body.Instructions;
            int divIdx = -1;
            for (int i = 2; i < instrs.Count - 1; i++)
                if (instrs[i].OpCode == OpCodes.Div && instrs[i - 1].OpCode == OpCodes.Ldc_I4 && (int)instrs[i - 1].Operand == 40 &&
                    instrs[i - 2].Operand is MethodReference mr && mr.Name == "get_Count" && instrs[i + 1].OpCode == OpCodes.Stloc)
                { divIdx = i; break; }
            if (divIdx < 0 || !instrs.Any(i => i.Operand is MethodReference r && r.Name == "RemoveLink"))
                throw new InvalidOperationException("[PATCH ERROR] Pathfinding.DestroyQueuedNavmeshData: unexpected IL (expected forced = m_linkRemoveQueue.Count / 40)");
            var il = body.GetILProcessor();
            il.InsertAfter(instrs[divIdx], il.Create(OpCodes.Ldc_I4, 0));
            il.InsertAfter(instrs[divIdx], il.Create(OpCodes.Pop));
            body.OptimizeMacros();
            Console.WriteLine("  + Pathfinding.DestroyQueuedNavmeshData: link removal bounded to 25 / 1 ms per frame (no unbounded Count/40 share)");
        }

        /// <summary>
        /// Mistlands fog: ParticleMist.Update becomes "m_haveActiveMist = NativePatchMist.MistTick.Tick(this, m_haveActiveMist)".
        /// The helper (embedded, dropped next to assembly_valheim.dll) keeps vanilla's emission rules but works on a per-tick
        /// snapshot of mister/demister geometry, spreads emission over the following frames, skips particles buried in terrain
        /// and caps distant mist below maxParticles. The private fields it replaces (accumulator, movement, last position) are
        /// owned by the helper; m_haveActiveMist stays in the game because IsInMist / IsMistBlocked read it.
        /// </summary>
        static void ApplyMistRewrite(ModuleDefinition module, string managedDir)
        {
            string helperPath = Path.Combine(managedDir, MistAssemblyFile);
            using var res = typeof(Program).Assembly.GetManifestResourceStream(MistAssemblyFile)
                ?? throw new InvalidOperationException($"[PATCH ERROR] Embedded {MistAssemblyFile} missing from patcher build");
            using var ms = new MemoryStream();
            res.CopyTo(ms);
            File.WriteAllBytes(helperPath, ms.ToArray());
            ms.Position = 0;
            using var helper = AssemblyDefinition.ReadAssembly(ms);
            var tickType = FindOrThrow(helper.MainModule.Types, t => t.FullName == "NativePatchMist.MistTick", "NativePatchMist.MistTick");
            var tick = module.ImportReference(FindOrThrow(tickType.Methods, m => m.Name == "Tick", "MistTick.Tick"));

            var pm = FindOrThrow(module.Types, t => t.Name == "ParticleMist", "ParticleMist");
            var update = FindOrThrow(pm.Methods, m => m.Name == "Update" && m.Parameters.Count == 0, "ParticleMist.Update");
            var haveActive = FindOrThrow(pm.Fields, f => f.Name == "m_haveActiveMist", "ParticleMist.m_haveActiveMist");
            foreach (var name in new[] { "m_localRange", "m_localEmission", "m_localEmissionPerUnit", "m_distantMaxRange", "m_distantThickness", "m_maxDistance", "m_emissionMax" })
                FindOrThrow(pm.Fields, f => f.Name == name && f.IsPublic, "ParticleMist." + name);
            var body = update.Body;
            if (!body.Instructions.Any(i => i.Operand is FieldReference fr && fr.Name == "m_haveActiveMist") ||
                !body.Instructions.Any(i => i.Operand is MethodReference mr && mr.Name == "Emit"))
                throw new InvalidOperationException("[PATCH ERROR] ParticleMist.Update: unexpected IL (expected vanilla emitter)");

            body.Instructions.Clear();
            body.ExceptionHandlers.Clear();
            body.Variables.Clear();
            var il = body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);                 // target of the stfld
            il.Emit(OpCodes.Ldarg_0);                 // MistTick.Tick(this, m_haveActiveMist)
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, haveActive);
            il.Emit(OpCodes.Call, tick);
            il.Emit(OpCodes.Stfld, haveActive);
            il.Emit(OpCodes.Ret);
            Console.WriteLine($"  + ParticleMist.Update -> NativePatchMist.MistTick.Tick (helper written to {helperPath})");
        }

        /// <summary>Sun shadow cull distance (m) per layer; 0/unlisted = QualitySettings.shadowDistance (80/120/150 m).</summary>
        static readonly (string Layer, float Meters)[] SunShadowLayerCull =
        {
            ("effect", 20f),          // particles' mesh parts, fire/smoke props
            ("item", 25f),            // dropped items
            ("Default_small", 30f),   // small rocks, bushes, pickables
            ("piece_nonsolid", 35f),  // torches, banners, item stands, rugs
            ("character", 60f),       // mobs (skinned meshes are the costliest casters)
        };

        /// <summary>
        /// Reads the 32 layer names from TagManager in globalgamemanagers (serialized as a 32-entry string vector
        /// starting with "Default", "TransparentFX"), so cull distances follow layer names, not hard-coded indices.
        /// </summary>
        static string[] ReadLayerNames(string globalGameManagersPath)
        {
            byte[] b = File.ReadAllBytes(globalGameManagersPath);
            byte[] probe = { 0x07, 0, 0, 0, (byte)'D', (byte)'e', (byte)'f', (byte)'a', (byte)'u', (byte)'l', (byte)'t', 0,
                             0x0d, 0, 0, 0, (byte)'T', (byte)'r', (byte)'a', (byte)'n', (byte)'s', (byte)'p', (byte)'a', (byte)'r', (byte)'e', (byte)'n', (byte)'t', (byte)'F', (byte)'X' };
            int p = b.AsSpan().IndexOf(probe);
            if (p < 4 || BitConverter.ToInt32(b, p - 4) != 32)
                throw new InvalidOperationException("TagManager layer table not found");
            var names = new string[32];
            for (int i = 0; i < 32; i++)
            {
                int len = BitConverter.ToInt32(b, p);
                if (len < 0 || len > 64 || p + 4 + len > b.Length) throw new InvalidOperationException($"bad layer name length at #{i}");
                names[i] = System.Text.Encoding.UTF8.GetString(b, p + 4, len);
                p = (p + 4 + len + 3) & ~3;
            }
            return names;
        }

        /// <summary>
        /// The sun renders every shadow caster within the shadow distance into its cascades: in a base that is thousands
        /// of small renderers (items, decorations, mobs) whose shadows are a few pixels at 30+ m. EnvMan.Awake now gives
        /// the directional light per-layer cull distances; terrain, building pieces, trees and rocks keep full range.
        /// </summary>
        static void ApplySunShadowLayerCull(TypeDefinition envManType, MethodReference setLayerShadowCull, MethodReference opImplicit, string[] layerNames)
        {
            var module = envManType.Module;
            var fDirLight = FindOrThrow(envManType.Fields, f => f.Name == "m_dirLight", "EnvMan.m_dirLight");
            var awake = FindOrThrow(envManType.Methods, m => m.Name == "Awake", "EnvMan.Awake");
            var body = awake.Body;
            body.SimplifyMacros();
            var first = body.Instructions[0];
            if (body.Instructions.Any(i => i.Operand == first) || body.ExceptionHandlers.Any(h => h.TryStart == first || h.HandlerStart == first))
                throw new InvalidOperationException("[PATCH ERROR] EnvMan.Awake: first instruction is a branch target, cannot prepend safely");

            var il = body.GetILProcessor();
            var block = new List<Instruction>
            {
                // if (m_dirLight) m_dirLight.layerShadowCullDistances = new float[32] { ... };
                il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, fDirLight), il.Create(OpCodes.Call, opImplicit), il.Create(OpCodes.Brfalse, first),
                il.Create(OpCodes.Ldarg_0), il.Create(OpCodes.Ldfld, fDirLight),
                il.Create(OpCodes.Ldc_I4, 32), il.Create(OpCodes.Newarr, module.TypeSystem.Single),
            };
            var applied = new List<string>();
            foreach (var (layer, meters) in SunShadowLayerCull)
            {
                int idx = Array.IndexOf(layerNames, layer);
                if (idx < 0)
                {
                    Console.WriteLine($"  ! Layer '{layer}' not found, left at the shadow distance");
                    continue;
                }
                block.Add(il.Create(OpCodes.Dup));
                block.Add(il.Create(OpCodes.Ldc_I4, idx));
                block.Add(il.Create(OpCodes.Ldc_R4, meters));
                block.Add(il.Create(OpCodes.Stelem_R4));
                applied.Add($"{layer}(#{idx}) {meters:0}m");
            }
            block.Add(il.Create(OpCodes.Callvirt, setLayerShadowCull));
            foreach (var ins in block) il.InsertBefore(first, ins);
            body.OptimizeMacros();
            Console.WriteLine($"  + EnvMan.Awake: sun layerShadowCullDistances = {string.Join(", ", applied)}; other layers keep the full shadow distance");
        }

        /// <summary>
        /// Image-identical render shortcuts in NativePatchMist.RenderTweaks (ApplyMistRewrite has already written the helper):
        /// EnvMan.SetEnv hands the SunShafts effect and camera over right after setting its colour, and the helper disables
        /// the effect in Camera.onPreCull while no screen point is within its radius of the sun (vanilla then renders five
        /// full passes that add zero). ReflectionUpdate.Start lets the helper switch both probes to one cube face per frame.
        /// </summary>
        static void ApplyRenderTweaks(ModuleDefinition module, string managedDir)
        {
            using var helper = AssemblyDefinition.ReadAssembly(Path.Combine(managedDir, MistAssemblyFile));
            var tweaks = FindOrThrow(helper.MainModule.Types, t => t.FullName == "NativePatchMist.RenderTweaks", "NativePatchMist.RenderTweaks");
            var track = module.ImportReference(FindOrThrow(tweaks.Methods, m => m.Name == "TrackSunShafts", "RenderTweaks.TrackSunShafts"));
            var slice = module.ImportReference(FindOrThrow(tweaks.Methods, m => m.Name == "SliceReflectionProbes", "RenderTweaks.SliceReflectionProbes"));

            var envMan = FindOrThrow(module.Types, t => t.Name == "EnvMan", "EnvMan");
            var setEnv = FindOrThrow(envMan.Methods, m => m.Name == "SetEnv", "EnvMan.SetEnv");
            var body = setEnv.Body;
            body.SimplifyMacros();
            var instrs = body.Instructions;
            int st = -1;
            for (int i = 4; i < instrs.Count; i++)
                if (instrs[i].OpCode == OpCodes.Stfld && instrs[i].Operand is FieldReference fr && fr.Name == "sunColor" && fr.DeclaringType.Name == "SunShafts" &&
                    instrs[i - 4].OpCode == OpCodes.Ldloc)
                { st = i; break; }
            int camStore = instrs.ToList().FindIndex(i => i.Operand is MethodReference mr && mr.Name == "GetMainCamera");
            if (st < 0 || camStore < 0 || instrs[camStore + 1].OpCode != OpCodes.Stloc)
                throw new InvalidOperationException("[PATCH ERROR] EnvMan.SetEnv: unexpected IL (expected cam = Utils.GetMainCamera(); ... shafts.sunColor = m_dirLight.color)");
            var shaftsVar = (VariableDefinition)instrs[st - 4].Operand;
            var camVar = (VariableDefinition)instrs[camStore + 1].Operand;
            var il = body.GetILProcessor();
            var after = instrs[st];
            foreach (var ins in new[] { il.Create(OpCodes.Ldloc, shaftsVar), il.Create(OpCodes.Ldloc, camVar), il.Create(OpCodes.Call, track) }.Reverse())
                il.InsertAfter(after, ins);
            body.OptimizeMacros();
            Console.WriteLine("  + EnvMan.SetEnv: SunShafts handed to RenderTweaks (skipped while the sun is off screen, image-identical)");

            var reflection = FindOrThrow(module.Types, t => t.Name == "ReflectionUpdate", "ReflectionUpdate");
            var start = FindOrThrow(reflection.Methods, m => m.Name == "Start", "ReflectionUpdate.Start");
            var sb = start.Body;
            var first = sb.Instructions[0];
            if (sb.Instructions.Any(i => i.Operand == first))
                throw new InvalidOperationException("[PATCH ERROR] ReflectionUpdate.Start: first instruction is a branch target, cannot prepend safely");
            var sil = sb.GetILProcessor();
            sil.InsertBefore(first, sil.Create(OpCodes.Ldarg_0));
            sil.InsertBefore(first, sil.Create(OpCodes.Call, slice));
            Console.WriteLine("  + ReflectionUpdate.Start: reflection probes one face per frame, no sun shadows, 12 s interval while idle (settings logged to Player.log)");
        }

        static void ApplyZoneTtlCheck(ModuleDefinition module, string managedDir)
        {
            using var helper = AssemblyDefinition.ReadAssembly(Path.Combine(managedDir, MistAssemblyFile));
            var ttl = FindOrThrow(helper.MainModule.Types, t => t.FullName == "NativePatchMist.ZoneTtl", "NativePatchMist.ZoneTtl");
            var check = module.ImportReference(FindOrThrow(ttl.Methods, m => m.Name == "HaveInstanceInSector", "ZoneTtl.HaveInstanceInSector"));

            var scene = FindOrThrow(module.Types, t => t.Name == "ZNetScene", "ZNetScene");
            var instances = FindOrThrow(scene.Fields, f => f.Name == "m_instances", "ZNetScene.m_instances");
            var have = FindOrThrow(scene.Methods, m => m.Name == "HaveInstanceInSector" && m.Parameters.Count == 1, "ZNetScene.HaveInstanceInSector");
            var body = have.Body;
            if (!body.Instructions.Any(i => i.Operand is FieldReference fr && fr.Name == "m_instances") ||
                !body.Instructions.Any(i => i.Operand is MethodReference mr && mr.Name == "GetZone") ||
                !body.Instructions.Any(i => i.Operand is FieldReference fd && fd.Name == "m_distant"))
                throw new InvalidOperationException("[PATCH ERROR] ZNetScene.HaveInstanceInSector: unexpected IL (expected vanilla instance scan)");
            int callers = module.Types.SelectMany(AllTypes).SelectMany(t => t.Methods).Where(m => m.HasBody)
                .Count(m => m.Body.Instructions.Any(i => i.Operand is MethodReference mr && mr.Name == "HaveInstanceInSector" && mr.DeclaringType.Name == "ZNetScene"));
            if (callers != 1)
                throw new InvalidOperationException($"[PATCH ERROR] ZNetScene.HaveInstanceInSector: expected 1 caller (ZoneSystem.UpdateTTL), found {callers}");

            body.Instructions.Clear();
            body.ExceptionHandlers.Clear();
            body.Variables.Clear();
            var il = body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, instances);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Call, check);
            il.Emit(OpCodes.Ret);
            Console.WriteLine("  + ZNetScene.HaveInstanceInSector -> NativePatchMist.ZoneTtl (expired-zone check without rescanning all objects)");
        }

        static void ApplySceneObjectsFix(ModuleDefinition module, string managedDir)
        {
            using var helper = AssemblyDefinition.ReadAssembly(Path.Combine(managedDir, MistAssemblyFile));
            var fix = FindOrThrow(helper.MainModule.Types, t => t.FullName == "NativePatchMist.SceneObjects", "NativePatchMist.SceneObjects");
            var scene = FindOrThrow(module.Types, t => t.Name == "ZNetScene", "ZNetScene");
            var instances = FindOrThrow(scene.Fields, f => f.Name == "m_instances", "ZNetScene.m_instances");

            foreach (var (name, args, marker) in new[] { ("RemoveObjects", 2, "TempRemoveEarmark"), ("OnZDODestroyed", 1, "ResetZDO") })
            {
                var target = FindOrThrow(fix.Methods, m => m.Name == name, "SceneObjects." + name);
                var method = FindOrThrow(scene.Methods, m => m.Name == name && m.Parameters.Count == args, "ZNetScene." + name);
                var body = method.Body;
                if (!body.Instructions.Any(i => i.Operand is FieldReference fr && fr.Name == "m_instances") ||
                    !body.Instructions.Any(i => i.Operand is MemberReference mr && mr.Name.Contains(marker)) ||
                    !body.Instructions.Any(i => i.Operand is MethodReference mr && mr.Name == "get_gameObject"))
                    throw new InvalidOperationException($"[PATCH ERROR] ZNetScene.{name}: unexpected IL (expected vanilla instance removal)");
                body.Instructions.Clear();
                body.ExceptionHandlers.Clear();
                body.Variables.Clear();
                var il = body.GetILProcessor();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, instances);
                for (int a = 1; a <= args; a++) il.Emit(OpCodes.Ldarg, method.Parameters[a - 1]);
                il.Emit(OpCodes.Call, module.ImportReference(target));
                il.Emit(OpCodes.Ret);
                Console.WriteLine($"  + ZNetScene.{name} -> NativePatchMist.SceneObjects (survives views destroyed outside ZNetScene)");
            }
        }

        static IEnumerable<TypeDefinition> AllTypes(TypeDefinition t) => new[] { t }.Concat(t.NestedTypes.SelectMany(AllTypes));

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

        static void ApplyForwardRenderingPatchesV6(TypeDefinition gsmType, TypeDefinition lightLodType, int pointLow, int pointMed, int pointHigh)
        {
            Instruction LdcI4(int val) => val switch
            {
                4 => Instruction.Create(OpCodes.Ldc_I4_4),
                6 => Instruction.Create(OpCodes.Ldc_I4_6),
                8 => Instruction.Create(OpCodes.Ldc_I4_8),
                _ => Instruction.Create(OpCodes.Ldc_I4_S, (sbyte)val)
            };

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
            il.Append(LdcI4(pointLow)); il.Emit(OpCodes.Ret); il.Append(nz);
            n1 = il.Create(OpCodes.Nop);
            il.Emit(argOpP); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Bne_Un_S, n1);
            il.Append(LdcI4(pointMed)); il.Emit(OpCodes.Ret); il.Append(n1);
            il.Append(LdcI4(pointHigh)); il.Emit(OpCodes.Ret);

            ApplySoftParticlesOff(gsmType);

            var llCtor = FindOrThrow(lightLodType.Methods, m => m.IsConstructor && !m.IsStatic, "LightLod..ctor");
            for (int i = 0; i < llCtor.Body.Instructions.Count; i++)
            {
                if (llCtor.Body.Instructions[i].Operand is FieldReference fr && (fr.Name == "m_lightDistance" || fr.Name == "m_shadowDistance"))
                {
                    if (llCtor.Body.Instructions[i - 1].OpCode != OpCodes.Ldc_R4)
                        throw new InvalidOperationException($"[PATCH ERROR] LightLod..ctor: expected ldc.r4 before {fr.Name}");
                    if (fr.Name == "m_lightDistance") llCtor.Body.Instructions[i - 1].Operand = 28.0f;
                    else llCtor.Body.Instructions[i - 1].Operand = 14.0f;
                }
            }
            Console.WriteLine($"  + Forward lights: 2/3/4, point: {pointLow}/{pointMed}/{pointHigh}, softParticles=false, LightLod 28m/14m (v6 adaptive)");
        }

        static void ApplySoftParticlesOff(TypeDefinition gsmType)
        {
            var applyQuality = FindOrThrow(gsmType.Methods, m => m.Name == "ApplyQualitySettings", "ApplyQualitySettings");
            for (int i = 0; i < applyQuality.Body.Instructions.Count; i++)
            {
                var inst = applyQuality.Body.Instructions[i];
                if (inst.Operand is MethodReference mr && mr.Name == "set_softParticles")
                {
                    if (applyQuality.Body.Instructions[i - 2].OpCode != OpCodes.Ldarg_0 || applyQuality.Body.Instructions[i - 1].OpCode != OpCodes.Ldfld)
                        throw new InvalidOperationException("[PATCH ERROR] ApplyQualitySettings: unexpected IL before set_softParticles (expected ldarg.0; ldfld)");
                    applyQuality.Body.Instructions[i - 2].OpCode = OpCodes.Nop; applyQuality.Body.Instructions[i - 1].OpCode = OpCodes.Ldc_I4_0; applyQuality.Body.Instructions[i - 1].Operand = null; break;
                }
            }
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

        static void ApplyWearNTearUpdaterCap(TypeDefinition updaterType, int maxUpdatesPerFrame)
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
        }

        static void ApplyWearNTearPatchesV7(TypeDefinition wntType, TypeDefinition updaterType, MethodReference getFrameCount, MethodReference getCountList, int maxUpdatesPerFrame)
        {
            ApplyWearNTearUpdaterCap(updaterType, maxUpdatesPerFrame);

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
            // Hardcoded locals: V_3 = parent Transform, V_6 = spawned GameObject. Fail loudly if a game update reshuffles them.
            if (m.Body.Variables[3].VariableType.FullName != "UnityEngine.Transform" ||
                instanceVar.VariableType.FullName != "UnityEngine.GameObject" ||
                instrs[startIdx].Operand != instanceVar)
                throw new InvalidOperationException("[PATCH ERROR] EffectList.Create: local variable layout changed (expected V_3 Transform, V_6 GameObject)");
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
            // Hardcoded locals: V_3 = parent Transform, V_6 = spawned GameObject. Fail loudly if a game update reshuffles them.
            if (m.Body.Variables[3].VariableType.FullName != "UnityEngine.Transform" ||
                instanceVar.VariableType.FullName != "UnityEngine.GameObject" ||
                instrs[startIdx].Operand != instanceVar)
                throw new InvalidOperationException("[PATCH ERROR] EffectList.Create: local variable layout changed (expected V_3 Transform, V_6 GameObject)");
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

        // ==================== PROFILER INJECTION ====================

        const string ProfilerAssemblyFile = "NativePatchProfiler.dll";
        const string MistAssemblyFile = "NativePatchMist.dll";
        const string ProfilerMarkerField = "s_nativeProfiler";

        /// <summary>
        /// Wraps every MonoBehaviour Update/LateUpdate/FixedUpdate, the MonoUpdaters batches and a few known
        /// heavy methods with Prof.Begin/End. The helper assembly is embedded in the patcher and dropped next to
        /// assembly_valheim.dll, where the game's Mono runtime resolves it.
        /// </summary>
        static void ApplyProfiler(ModuleDefinition module, string profilerDllPath)
        {
            using var res = typeof(Program).Assembly.GetManifestResourceStream(ProfilerAssemblyFile)
                ?? throw new InvalidOperationException($"[PATCH ERROR] Embedded {ProfilerAssemblyFile} missing from patcher build");
            using var ms = new MemoryStream();
            res.CopyTo(ms);
            File.WriteAllBytes(profilerDllPath, ms.ToArray());
            ms.Position = 0;
            using var profAsm = AssemblyDefinition.ReadAssembly(ms);
            var profType = FindOrThrow(profAsm.MainModule.Types, t => t.FullName == "NativePatchProfiler.Prof", "NativePatchProfiler.Prof");
            var mInit = module.ImportReference(FindOrThrow(profType.Methods, m => m.Name == "Init", "Prof.Init"));
            var mBegin = module.ImportReference(FindOrThrow(profType.Methods, m => m.Name == "Begin", "Prof.Begin"));
            var mEnd = module.ImportReference(FindOrThrow(profType.Methods, m => m.Name == "End", "Prof.End"));
            var mEndScope = module.ImportReference(FindOrThrow(profType.Methods, m => m.Name == "EndScope", "Prof.EndScope"));
            var mFrame = module.ImportReference(FindOrThrow(profType.Methods, m => m.Name == "Frame", "Prof.Frame"));

            var names = new List<string>();
            var done = new HashSet<MethodDefinition>();

            // Prologue: t = Prof.Begin(). Every ret becomes "br epilogue"; epilogue reports and returns.
            // Rets are never inside protected regions in valid IL, and keeping the same Instruction objects
            // preserves all branch targets and exception-handler boundaries.
            void Instrument(MethodDefinition m, Func<ILProcessor, VariableDefinition, Instruction[]> report)
            {
                var body = m.Body;
                body.SimplifyMacros();
                var tVar = new VariableDefinition(module.TypeSystem.Int64);
                body.Variables.Add(tVar);
                VariableDefinition? retVar = null;
                if (m.ReturnType.MetadataType != MetadataType.Void)
                {
                    retVar = new VariableDefinition(m.ReturnType);
                    body.Variables.Add(retVar);
                }
                body.InitLocals = true;
                var il = body.GetILProcessor();
                var rets = body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToList();

                var epilogue = new List<Instruction>();
                if (retVar != null) epilogue.Add(il.Create(OpCodes.Stloc, retVar));
                epilogue.AddRange(report(il, tVar));
                if (retVar != null) epilogue.Add(il.Create(OpCodes.Ldloc, retVar));
                epilogue.Add(il.Create(OpCodes.Ret));
                foreach (var e in epilogue) il.Append(e);

                foreach (var r in rets) { r.OpCode = OpCodes.Br; r.Operand = epilogue[0]; }

                var first = body.Instructions[0];
                il.InsertBefore(first, il.Create(OpCodes.Call, mBegin));
                il.InsertBefore(first, il.Create(OpCodes.Stloc, tVar));
                body.OptimizeMacros();
                done.Add(m);
            }

            void InstrumentById(MethodDefinition m, string name)
            {
                if (done.Contains(m) || !m.HasBody) return;
                int id = names.Count;
                names.Add(name);
                Instrument(m, (il, t) => new[] { il.Create(OpCodes.Ldc_I4, id), il.Create(OpCodes.Ldloc, t), il.Create(OpCodes.Call, mEnd) });
            }

            bool IsMonoBehaviour(TypeDefinition t)
            {
                for (var bt = t.BaseType; bt != null;)
                {
                    if (bt.FullName == "UnityEngine.MonoBehaviour") return true;
                    TypeDefinition? def;
                    try { def = bt.Resolve(); } catch { return false; }
                    bt = def?.BaseType;
                }
                return false;
            }

            // 1. Unity message methods: top-level per-frame work, one section per type
            int messages = 0;
            foreach (var type in module.GetTypes().Where(IsMonoBehaviour).ToList())
            {
                foreach (var m in type.Methods.Where(m => !m.IsStatic && m.HasBody && m.Parameters.Count == 0 && !m.HasGenericParameters &&
                                                          m.ReturnType.MetadataType == MetadataType.Void &&
                                                          (m.Name == "Update" || m.Name == "LateUpdate" || m.Name == "FixedUpdate")).ToList())
                {
                    InstrumentById(m, $"{type.Name}.{m.Name}");
                    messages++;
                }
            }
            Console.WriteLine($"  + Unity messages instrumented: {messages} Update/LateUpdate/FixedUpdate methods");

            // 2. MonoUpdaters batches, keyed by the game's own profiler scope strings
            var extra = module.Types.FirstOrDefault(t => t.Name == "MonoUpdatersExtra");
            int scopes = 0;
            if (extra != null)
            {
                foreach (var m in extra.Methods.Where(m => m.HasBody && m.Parameters.Any(p => p.Name == "profileScope")).ToList())
                {
                    var scopeParam = m.Parameters.First(p => p.Name == "profileScope");
                    Instrument(m, (il, t) => new[] { il.Create(OpCodes.Ldarg, scopeParam), il.Create(OpCodes.Ldloc, t), il.Create(OpCodes.Call, mEndScope) });
                    scopes++;
                }
            }
            Console.WriteLine($"  + MonoUpdaters batches instrumented: {scopes} methods (per-category timings)");

            // 3. Known heavy methods: most run nested inside the messages above; Invoke/InvokeRepeating targets are top-level
            var hotPaths = new (string Type, string Method, bool Nested)[]
            {
                ("ZNetScene", "CreateDestroyObjects", true), ("ZNetScene", "CreateObjects", true), ("ZNetScene", "RemoveObjects", true),
                ("ZoneSystem", "CreateLocalZones", true), ("ZoneSystem", "SpawnZone", true), ("ZoneSystem", "UpdateTTL", true),
                ("Heightmap", "Regenerate", true), ("Heightmap", "RebuildCollisionMesh", true), ("Heightmap", "RebuildRenderMesh", true),
                ("Minimap", "Explore", true), ("WearNTearUpdater", "UpdateWearNTear", true),
                ("ZNet", "Save", true), ("ZNet", "SaveWorld", true), ("Game", "SavePlayerProfile", true), ("Game", "CollectResources", true),
                ("ReflectionUpdate", "UpdateReflection", true),
                ("DepthCamera", "RenderDepth", false),
                ("Pathfinding", "BuildTile", true), ("Pathfinding", "UpdateAsyncBuild", true),
                ("Pathfinding", "DestroyQueuedNavmeshData", true), ("Pathfinding", "TimeoutTiles", true),
            };
            foreach (var (typeName, methodName, isNested) in hotPaths)
            {
                var type = module.Types.FirstOrDefault(t => t.Name == typeName);
                // Explore(int,int) is the per-pixel overload; only time the per-tick Explore(Vector3,float)
                var targets = type?.Methods.Where(m => m.Name == methodName && m.HasBody && !m.HasGenericParameters &&
                                                       !(methodName == "Explore" && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.MetadataType == MetadataType.Int32)).ToList();
                if (targets == null || targets.Count == 0)
                {
                    Console.WriteLine($"  ! {typeName}.{methodName} not found, skipped");
                    continue;
                }
                foreach (var m in targets) InstrumentById(m, $"{(isNested ? ">" : "")}{typeName}.{methodName}");
            }
            Console.WriteLine($"  + Hot-path methods instrumented: {hotPaths.Length} targets");

            // 4. Section names + frame marker at the start of Game.Update
            var gameType = FindOrThrow(module.Types, t => t.Name == "Game", "Game");
            var gameAwake = FindOrThrow(gameType.Methods, m => m.Name == "Awake", "Game.Awake");
            var ilA = gameAwake.Body.GetILProcessor();
            var firstA = gameAwake.Body.Instructions[0];
            ilA.InsertBefore(firstA, ilA.Create(OpCodes.Ldstr, string.Join("|", names)));
            ilA.InsertBefore(firstA, ilA.Create(OpCodes.Call, mInit));

            var gameUpdate = FindOrThrow(gameType.Methods, m => m.Name == "Update", "Game.Update");
            var ilU = gameUpdate.Body.GetILProcessor();
            ilU.InsertBefore(gameUpdate.Body.Instructions[0], ilU.Create(OpCodes.Call, mFrame));

            var wnt = FindOrThrow(module.Types, t => t.Name == "WearNTear", "WearNTear");
            wnt.Fields.Add(new FieldDefinition(ProfilerMarkerField, FieldAttributes.Private | FieldAttributes.Static, module.TypeSystem.Boolean));
            Console.WriteLine($"  + {names.Count} named sections, frame marker in Game.Update, helper written to {profilerDllPath}");
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
                _ when marker.Name.StartsWith("s_nativePatched_v9") => PatchVersion.V9,
                _ => PatchVersion.V8
            };
            PatchVersion verToVerify = targetVer ?? detected;

            Console.WriteLine($"Detected marker: {marker.Name} -> verifying against {verToVerify.ToString().ToLower()} ruleset...\n");

            int checksPassed = 0;
            int totalChecks = verToVerify == PatchVersion.V9 ? 24 : verToVerify == PatchVersion.V8 ? 12 : (verToVerify == PatchVersion.V7 ? 10 : (verToVerify == PatchVersion.V6 ? 9 : 8));

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

            if (verToVerify == PatchVersion.V9)
            {
                // 1. Marker check
                AssertCheck(marker.Name == MarkerFieldNameV9, "Marker v9", $"Found marker {marker.Name}");

                // 2. WearNTear: vanilla support logic, no frame stagger gate
                var updateWear = FindOrThrow(wnt.Methods, m => m.Name == "UpdateWear", "WearNTear.UpdateWear");
                bool noStaggerGate = !wnt.Methods.Any(m => m.Name == "ShouldUpdateSupport")
                                  && !updateWear.Body.Instructions.Any(i => i.Operand?.ToString()?.Contains("ShouldUpdateSupport") == true)
                                  && updateWear.Body.Instructions.Any(i => i.OpCode == OpCodes.Call && i.Operand?.ToString()?.Contains("UpdateSupport") == true);
                AssertCheck(noStaggerGate, "P0 WearNTear Vanilla Support", "UpdateSupport runs every wear tick (vanilla cache, no 1/8 frame stagger)");

                // 3. WearNTearUpdater: adaptive upper clamp 100 replaced by per-frame cap
                var updater = FindOrThrow(module.Types, t => t.Name == "WearNTearUpdater", "WearNTearUpdater");
                var updateWnt = FindOrThrow(updater.Methods, m => m.Name == "UpdateWearNTear", "WearNTearUpdater.UpdateWearNTear");
                bool updaterCapped = !updateWnt.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_I4_S && (sbyte)i.Operand == 100);
                AssertCheck(updaterCapped, "P1 WearNTearUpdater Cap", "Upper clamp of pieces/frame lowered from vanilla 100");

                // 4. Fire: vanilla 1.0s dot interval (damage per tick unchanged)
                var fire = FindOrThrow(module.Types, t => t.Name == "Fire", "Fire");
                var fireCtor = FindOrThrow(fire.Methods, m => m.IsConstructor && !m.IsStatic, "Fire..ctor");
                int dotIdx = fireCtor.Body.Instructions.ToList().FindIndex(i => i.Operand is FieldReference fr && fr.Name == "m_dotInterval");
                bool fireVanilla = dotIdx > 0 && fireCtor.Body.Instructions[dotIdx - 1].Operand is float dot && dot == 1.0f;
                AssertCheck(fireVanilla, "P0 Fire Balance", "m_dotInterval kept at vanilla 1.0s");

                // 5. Heightmap: cooking options left at Unity's default (30), not the slower-to-simulate 16
                var hm = FindOrThrow(module.Types, t => t.Name == "Heightmap", "Heightmap");
                var hmAwake = FindOrThrow(hm.Methods, m => m.Name == "Awake", "Heightmap.Awake");
                var hmInstrs = hmAwake.Body.Instructions;
                bool fastCooking = hmInstrs.Any(i => i.OpCode == OpCodes.Callvirt && i.Operand?.ToString()?.Contains("set_cookingOptions") == true
                                                  && i.Previous?.OpCode == OpCodes.Ldc_I4_S && (sbyte)i.Previous.Operand == 30);
                AssertCheck(fastCooking, "P0 Heightmap Cooking", "MeshCollider.cookingOptions = Unity default (keeps CookForFasterSimulation for runtime physics)");

                // 6. Vanilla light limits and LightLod distances
                var gsm = FindOrThrow(module.Types, t => t.Name == "GraphicsSettingsManager", "GraphicsSettingsManager");
                var getPointLightLimit = FindOrThrow(gsm.Methods, m => m.Name == "GetPointLightLimit", "GetPointLightLimit");
                var lightLodV = FindOrThrow(module.Types, t => t.Name == "LightLod", "LightLod");
                var lightLodCtor = FindOrThrow(lightLodV.Methods, m => m.IsConstructor && !m.IsStatic, "LightLod..ctor");
                bool vanillaLights = getPointLightLimit.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_I4_S && (sbyte)i.Operand == 15)
                                  && getPointLightLimit.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_I4_S && (sbyte)i.Operand == 40)
                                  && lightLodCtor.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 40f);
                AssertCheck(vanillaLights, "P1 Vanilla Lights", "Point lights Low/Med/High/Max = 4/15/40/unlimited, LightLod 40 m light / 20 m shadow (vanilla)");

                // 7. ClutterSystem Off == 0.0m
                var clutter = FindOrThrow(module.Types, t => t.Name == "ClutterSystem", "ClutterSystem");
                var applySettings = FindOrThrow(clutter.Methods, m => m.Name == "ApplySettings", "ClutterSystem.ApplySettings");
                bool clutterHandlesOff = applySettings.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 0.0f);
                AssertCheck(clutterHandlesOff, "P1 ClutterSystem Quality Logic", "Sets 0m vegetation distance when Quality is Off");

                // 8. Character.Awake combat preservation
                var character = FindOrThrow(module.Types, t => t.Name == "Character", "Character");
                var charAwake = FindOrThrow(character.Methods, m => m.Name == "Awake", "Character.Awake");
                bool charProtectsCombat = charAwake.Body.Instructions.Any(i => i.OpCode == OpCodes.Isinst && i.Operand?.ToString()?.Contains("Humanoid") == true)
                                       && charAwake.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldfld && i.Operand?.ToString()?.Contains("m_tamed") == true);
                AssertCheck(charProtectsCombat, "P0/P1 Combat Hitbox Preservation", "Excludes all Humanoids, Bosses, and Tamed pets from CullUpdateTransforms");

                // 9. EffectList SetParent(parent, true)
                var effectList = FindOrThrow(module.Types, t => t.Name == "EffectList", "EffectList");
                var create = FindOrThrow(effectList.Methods, m => m.Name == "Create" && m.Parameters.Count == 6, "EffectList.Create");
                bool elUsesSetParentTrue = create.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_I4_1)
                                        && create.Body.Instructions.Any(i => i.OpCode == OpCodes.Callvirt && i.Operand?.ToString()?.Contains("SetParent(UnityEngine.Transform,System.Boolean)") == true);
                AssertCheck(elUsesSetParentTrue, "P1 EffectList Transform Integrity", "Uses SetParent(parent, true) preserving world position, rotation and scale");

                // 10. EffectList 45m range threshold
                bool elExtendedRange = create.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 45.0f);
                AssertCheck(elExtendedRange, "P1 EffectList Combat Range", "45m distance threshold preserves archery sounds and combat particles");

                // 11. Ragdoll safety
                var ragdoll = FindOrThrow(module.Types, t => t.Name == "Ragdoll", "Ragdoll");
                bool ragdollNoLeak = !ragdoll.Fields.Any(f => f.Name == "s_activeRagdolls");
                var ragdollAwake = FindOrThrow(ragdoll.Methods, m => m.Name == "Awake", "Ragdoll.Awake");
                bool ragdollClampsTtl = ragdollAwake.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand <= 3.5f);
                AssertCheck(ragdollNoLeak && ragdollClampsTtl, "P2 Ragdoll Safety", "Clean PhysX release at TTL<=3.5s with zero static buffer leak");

                // 12. Heightmap rebuild budget: queued chunks spread across frames
                var hmBudget = FindOrThrow(module.Types, t => t.Name == "Heightmap", "Heightmap");
                var clu = FindOrThrow(hmBudget.Methods, m => m.Name == "CustomLateUpdate", "Heightmap.CustomLateUpdate");
                bool hasBudget = clu.Body.Instructions.Any(i => i.Operand?.ToString()?.Contains("get_realtimeSinceStartupAsDouble") == true)
                              && clu.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldsfld && i.Operand?.ToString()?.Contains("s_nativeRegenStart") == true);
                AssertCheck(hasBudget, "P1 Heightmap Rebuild Budget", "Queued terrain chunks rebuild within a 4 ms/frame budget (first one always runs)");

                // 13. ZNetScene object streaming left vanilla (no caps, no time budget)
                var zns = FindOrThrow(module.Types, t => t.Name == "ZNetScene", "ZNetScene");
                var cos = FindOrThrow(zns.Methods, m => m.Name == "CreateObjectsSorted", "ZNetScene.CreateObjectsSorted");
                var co = FindOrThrow(zns.Methods, m => m.Name == "CreateObjects" && m.Parameters.Count == 2, "ZNetScene.CreateObjects(2)");
                bool vanillaStreaming = !zns.Fields.Any(f => f.Name.StartsWith("s_nativeCreate"))
                    && cos.Body.Instructions.Any(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference mr && mr.Name == "Max")
                    // first int constant, not Instructions[0]: the profiler prepends its own prologue
                    && co.Body.Instructions.First(i => i.OpCode == OpCodes.Ldc_I4_S) is { } maxInst && (sbyte)maxInst.Operand == 10;
                AssertCheck(vanillaStreaming, "P0 Vanilla Object Streaming", "ZNetScene creates Max(Count/100, 10) objects/frame like vanilla (no pop-in of nearby mobs/pickables)");

                // 14. Minimap explored maps serialized in bulk on save
                var minimap = FindOrThrow(module.Types, t => t.Name == "Minimap", "Minimap");
                var getMapData = FindOrThrow(minimap.Methods, m => m.Name == "GetMapData" && m.Parameters.Count == 0, "Minimap.GetMapData");
                bool bulkSave = getMapData.Body.Instructions.Count(i => i.Operand is MethodReference mr && mr.Name == "WriteBitsAsBytes") == 2
                             && !getMapData.Body.Instructions.Any(i => i.Operand is MethodReference mr && mr.Name == "get_Item");
                AssertCheck(bulkSave, "P1 Minimap Bulk Save", "Explored maps written in one call each (autosave hitch without per-bit writes)");

                // 15. DepthCamera skips identical re-renders
                var depthCam = FindOrThrow(module.Types, t => t.Name == "DepthCamera", "DepthCamera");
                var renderDepth = FindOrThrow(depthCam.Methods, m => m.Name == "RenderDepth", "DepthCamera.RenderDepth");
                bool depthSkip = depthCam.Fields.Any(f => f.Name == "m_nativePos")
                              && renderDepth.Body.Instructions.Any(i => i.Operand is MethodReference mr && mr.Name == "NrOfInstances")
                              && renderDepth.Body.Instructions.Any(i => i.Operand is MethodReference mr && mr.Name == "RenderWithShader");
                AssertCheck(depthSkip, "P1 DepthCamera Skip Unchanged", "Rain/wetness mask re-rendered only when position or objects change (at least every 5 s)");

                // 16. Mistlands fog emitter delegates to the helper, which must be present next to the assembly
                var particleMist = FindOrThrow(module.Types, t => t.Name == "ParticleMist", "ParticleMist");
                var mistUpdate = FindOrThrow(particleMist.Methods, m => m.Name == "Update" && m.Parameters.Count == 0, "ParticleMist.Update");
                bool mistDelegated = mistUpdate.Body.Instructions.Count(i => i.Operand is MethodReference mr && mr.Name == "Tick" && mr.DeclaringType.Name == "MistTick") == 1
                                  && !mistUpdate.Body.Instructions.Any(i => i.Operand is MethodReference mr && mr.Name == "Emit")
                                  && File.Exists(Path.Combine(managedDir, MistAssemblyFile));
                AssertCheck(mistDelegated, "P1 Mistlands Fog Rewrite", "ParticleMist.Update runs NativePatchMist.MistTick (cached geometry, spread emission, buried-particle skip, particle cap)");

                // 17. Navmesh tile rebuild interval stretched
                var pathfinding = FindOrThrow(module.Types, t => t.Name == "Pathfinding", "Pathfinding");
                var buildtiles = FindOrThrow(pathfinding.Methods, m => m.Name == "Buildtiles", "Pathfinding.Buildtiles");
                var bi = buildtiles.Body.Instructions;
                int loadIdx = bi.ToList().FindIndex(i => i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference fr && fr.Name == "m_updateInterval");
                bool navScaled = loadIdx >= 0 && loadIdx + 2 < bi.Count && bi[loadIdx + 1].OpCode == OpCodes.Ldc_R4 && bi[loadIdx + 2].OpCode == OpCodes.Mul;
                AssertCheck(navScaled, "P1 Pathfinding Rebuild Interval", "Navmesh tiles rebuilt at most every 15 s instead of 5 s (less link re-stitching churn)");

                // 18. Navmesh link removal time-bounded
                var destroyQueued = FindOrThrow(pathfinding.Methods, m => m.Name == "DestroyQueuedNavmeshData", "Pathfinding.DestroyQueuedNavmeshData");
                var dq = destroyQueued.Body.Instructions.ToList();
                int divAt = dq.FindIndex(i => i.OpCode == OpCodes.Div);
                bool linkBudget = divAt >= 0 && divAt + 2 < dq.Count && dq[divAt + 1].OpCode == OpCodes.Pop && dq[divAt + 2].OpCode == OpCodes.Ldc_I4_0;
                AssertCheck(linkBudget, "P1 Pathfinding Link Removal Budget", "Queued navmesh links removed at most 25 / 1 ms per frame (vanilla forced Count/40 with no time limit)");

                // 19. Sun shadow cull distances for small layers
                var envMan = FindOrThrow(module.Types, t => t.Name == "EnvMan", "EnvMan");
                var envAwake = FindOrThrow(envMan.Methods, m => m.Name == "Awake", "EnvMan.Awake");
                bool sunCull = envAwake.Body.Instructions.Any(i => i.Operand is MethodReference mr && mr.Name == "set_layerShadowCullDistances")
                            && envAwake.Body.Instructions.Count(i => i.OpCode == OpCodes.Stelem_R4) == SunShadowLayerCull.Length;
                AssertCheck(sunCull, "P1 Sun Shadow Layer Cull", $"Sun shadows of {string.Join("/", SunShadowLayerCull.Select(c => $"{c.Layer} {c.Meters:0}m"))}; terrain, pieces, trees keep full range");

                // 20. SunShafts gate
                var setEnvM = FindOrThrow(envMan.Methods, m => m.Name == "SetEnv", "EnvMan.SetEnv");
                bool shaftsGate = setEnvM.Body.Instructions.Any(i => i.Operand is MethodReference mr && mr.Name == "TrackSunShafts" && mr.DeclaringType.Name == "RenderTweaks");
                AssertCheck(shaftsGate, "P2 Idle Sun Shafts Skip", "SunShafts disabled per frame while no screen point is within its radius of the sun (adds zero in vanilla)");

                // 21. Reflection probe time slicing
                var reflUpd = FindOrThrow(module.Types, t => t.Name == "ReflectionUpdate", "ReflectionUpdate");
                var reflStart = FindOrThrow(reflUpd.Methods, m => m.Name == "Start", "ReflectionUpdate.Start");
                bool probeSlice = reflStart.Body.Instructions.Any(i => i.Operand is MethodReference mr && mr.Name == "SliceReflectionProbes");
                AssertCheck(probeSlice, "P2 Reflection Probe Time Slicing", "Environment reflection: one cube face per frame, no sun shadows, 12 s interval while standing still");

                // 22. Expired-zone check
                var netScene = FindOrThrow(module.Types, t => t.Name == "ZNetScene", "ZNetScene");
                var haveInSector = FindOrThrow(netScene.Methods, m => m.Name == "HaveInstanceInSector" && m.Parameters.Count == 1, "ZNetScene.HaveInstanceInSector");
                bool zoneTtl = haveInSector.Body.Instructions.Any(i => i.Operand is MethodReference mr && mr.Name == "HaveInstanceInSector" && mr.DeclaringType.Name == "ZoneTtl");
                AssertCheck(zoneTtl, "P1 Zone Unload Check", "ZoneSystem.UpdateTTL: occupied zones proven by a remembered object, at most one object scan per tick (vanilla: one per expired zone)");

                // 23. Scene unload fix
                bool sceneFix = new[] { "RemoveObjects", "OnZDODestroyed" }.All(n => netScene.Methods.Any(m => m.Name == n && m.HasBody &&
                    m.Body.Instructions.Any(i => i.Operand is MethodReference mr && mr.Name == n && mr.DeclaringType.Name == "SceneObjects")));
                AssertCheck(sceneFix, "P0 Scene Unload Fix", "ZNetScene.RemoveObjects/OnZDODestroyed skip views destroyed elsewhere (vanilla threw every 1/30 s and stopped unloading objects)");

                // 24. All subsystems verified
                AssertCheck(checksPassed == totalChecks - 1, "Overall Integrity", $"All {checksPassed}/{totalChecks - 1} subsystem audits passed successfully!");
            }
            else if (verToVerify == PatchVersion.V8)
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

                // 11. EnemyHud hover text optimization
                var enemyHud = FindOrThrow(module.Types, t => t.Name == "EnemyHud", "EnemyHud");
                var updateHuds = FindOrThrow(enemyHud.Methods, m => m.Name == "UpdateHuds", "EnemyHud.UpdateHuds");
                bool ehNoPerFrameLocalize = !updateHuds.Body.Instructions.Any(i => i.OpCode == OpCodes.Callvirt && i.Operand?.ToString()?.Contains("GetHoverName") == true);
                AssertCheck(ehNoPerFrameLocalize, "P1 EnemyHud Hover Optimization", "Eliminated redundant per-frame Localization.Localize & set_text in UpdateHuds");

                // 12. Ragdoll safety
                var ragdoll = FindOrThrow(module.Types, t => t.Name == "Ragdoll", "Ragdoll");
                bool ragdollNoLeak = !ragdoll.Fields.Any(f => f.Name == "s_activeRagdolls");
                var ragdollAwake = FindOrThrow(ragdoll.Methods, m => m.Name == "Awake", "Ragdoll.Awake");
                bool ragdollClampsTtl = ragdollAwake.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand <= 3.5f);
                AssertCheck(ragdollNoLeak && ragdollClampsTtl, "P2 Ragdoll Safety", "Clean PhysX release at TTL<=3.5s with zero static buffer leak");

                // 13. All subsystems verified
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
