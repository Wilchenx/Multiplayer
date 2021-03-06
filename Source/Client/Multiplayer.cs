﻿extern alias zip;

using Harmony;
using Ionic.Zlib;
using LiteNetLib;
using Multiplayer.Common;
using RimWorld;
using RimWorld.Planet;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using UnityEngine;
using Verse;
using Verse.Profile;
using Verse.Sound;
using Verse.Steam;

namespace Multiplayer.Client
{
    [StaticConstructorOnStartup]
    public static class Multiplayer
    {
        public static MultiplayerSession session;
        public static MultiplayerGame game;

        public static IConnection Client => session?.client;
        public static MultiplayerServer LocalServer => session?.localServer;
        public static PacketLogWindow PacketLog => session?.packetLog;
        public static bool IsReplay => session?.replay ?? false;

        public static string username;
        public static bool arbiterInstance;
        public static HarmonyInstance harmony => MultiplayerMod.harmony;

        public static bool reloading;

        public static FactionDef FactionDef = FactionDef.Named("MultiplayerColony");
        public static FactionDef DummyFactionDef = FactionDef.Named("MultiplayerDummy");

        public static IdBlock GlobalIdBlock => game.worldComp.globalIdBlock;
        public static Faction DummyFaction => game.dummyFaction;
        public static MultiplayerWorldComp WorldComp => game.worldComp;

        public static Faction RealPlayerFaction
        {
            get => Client != null ? game.RealPlayerFaction : Faction.OfPlayer;
            set => game.RealPlayerFaction = value;
        }

        public static bool ExecutingCmds => MultiplayerWorldComp.executingCmdWorld || MapAsyncTimeComp.executingCmdMap != null;
        public static bool Ticking => MultiplayerWorldComp.tickingWorld || MapAsyncTimeComp.tickingMap != null || ConstantTicker.ticking;
        public static Map MapContext => MapAsyncTimeComp.tickingMap ?? MapAsyncTimeComp.executingCmdMap;

        public static bool dontSync;
        public static bool ShouldSync => Client != null && !Ticking && !ExecutingCmds && !reloading && Current.ProgramState == ProgramState.Playing && LongEventHandler.currentEvent == null && !dontSync;

        public static string ReplaysDir => GenFilePaths.FolderUnderSaveData("MpReplays");
        public static string DesyncsDir => GenFilePaths.FolderUnderSaveData("MpDesyncs");

        public static Stopwatch Clock = Stopwatch.StartNew();

        static Multiplayer()
        {
            if (GenCommandLine.CommandLineArgPassed("profiler"))
                SimpleProfiler.CheckAvailable();

            MpLog.info = str => Log.Message($"{username} {TickPatch.Timer} {str}");
            MpLog.error = str => Log.Error(str);

            SetUsername();

            SimpleProfiler.Init(username);

            if (SteamManager.Initialized)
                SteamIntegration.InitCallbacks();

            Log.Message($"Multiplayer version {MpVersion.Version}");
            Log.Message($"Player's username: {username}");
            Log.Message($"Processor: {SystemInfo.processorType}");

            PlantWindSwayPatch.Init();

            var gameobject = new GameObject();
            gameobject.AddComponent<OnMainThread>();
            UnityEngine.Object.DontDestroyOnLoad(gameobject);

            MpConnectionState.SetImplementation(ConnectionStateEnum.ClientSteam, typeof(ClientSteamState));
            MpConnectionState.SetImplementation(ConnectionStateEnum.ClientJoining, typeof(ClientJoiningState));
            MpConnectionState.SetImplementation(ConnectionStateEnum.ClientPlaying, typeof(ClientPlayingState));

            CollectCursorIcons();

            harmony.DoAllMpPatches();

            SyncHandlers.Init();
            Sync.RegisterAllSyncMethods();

            DoPatches();

            Log.messageQueue.maxMessages = 1000;

            HandleCommandLine();
        }

        private static void SetUsername()
        {
            Multiplayer.username = MultiplayerMod.settings.username;

            if (Multiplayer.username == null && SteamManager.Initialized)
            {
                Multiplayer.username = SteamUtility.SteamPersonaName;
                Multiplayer.username = new Regex("[^a-zA-Z0-9_]").Replace(Multiplayer.username, string.Empty);
                Multiplayer.username = Multiplayer.username.TrimmedToLength(15);

                MultiplayerMod.settings.username = Multiplayer.username;
                MultiplayerMod.settings.Write();
            }

            if (GenCommandLine.TryGetCommandLineArg("username", out string username))
                Multiplayer.username = username;
            else if (Multiplayer.username == null || MpVersion.IsDebug)
                Multiplayer.username = "Player" + Rand.Range(0, 9999);
        }

        private static void HandleCommandLine()
        {
            if (GenCommandLine.TryGetCommandLineArg("connect", out string ip))
            {
                int port = MultiplayerServer.DefaultPort;

                var split = ip.Split(':');
                if (split.Length == 0)
                    ip = "127.0.0.1";
                else if (split.Length >= 1)
                    ip = split[0];

                if (split.Length == 2)
                    int.TryParse(split[1], out port);

                if (IPAddress.TryParse(ip, out IPAddress addr))
                    LongEventHandler.QueueLongEvent(() => ClientUtil.TryConnect(addr, port), "Connecting", false, null);
            }

            if (GenCommandLine.CommandLineArgPassed("arbiter"))
            {
                arbiterInstance = true;
                username = "The Arbiter";
                Prefs.VolumeGame = 0;
            }

            if (GenCommandLine.TryGetCommandLineArg("replay", out string replay))
            {
                LongEventHandler.QueueLongEvent(() =>
                {
                    Replay.LoadReplay(replay, true, () =>
                    {
                        var rand = Find.Maps.Select(m => m.AsyncTime().randState).Select(s => $"{(uint)s} {s >> 32}");

                        Log.Message($"timer {TickPatch.Timer}");
                        Log.Message($"world rand {(uint)WorldComp.randState} {WorldComp.randState >> 32}");
                        Log.Message($"map rand {rand.ToStringSafeEnumerable()} | {Find.Maps.Select(m => m.AsyncTime().mapTicks).ToStringSafeEnumerable()}");
                    });
                }, "Replay", false, null);
            }
        }

        private static void DoPatches()
        {
            harmony.PatchAll();

            // General designation handling
            {
                var designatorMethods = new[] { "DesignateSingleCell", "DesignateMultiCell", "DesignateThing" };

                foreach (Type t in typeof(Designator).AllSubtypesAndSelf())
                {
                    foreach (string m in designatorMethods)
                    {
                        MethodInfo method = t.GetMethod(m, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                        if (method == null) continue;

                        MethodInfo prefix = AccessTools.Method(typeof(DesignatorPatches), m);
                        harmony.Patch(method, new HarmonyMethod(prefix), null, null);
                    }
                }
            }

            // Remove side effects from methods which are non-deterministic during ticking (e.g. camera dependent motes and sound effects)
            {
                var randPatchPrefix = new HarmonyMethod(typeof(RandPatches).GetMethod("Prefix"));
                var randPatchPostfix = new HarmonyMethod(typeof(RandPatches).GetMethod("Postfix"));

                var subSustainerStart = AccessTools.Method(typeof(SubSustainer), "<SubSustainer>m__0");
                var sampleCtor = typeof(Sample).GetConstructor(new[] { typeof(SubSoundDef) });
                var subSoundPlay = typeof(SubSoundDef).GetMethod("TryPlay");
                var effecterTick = typeof(Effecter).GetMethod("EffectTick");
                var effecterTrigger = typeof(Effecter).GetMethod("Trigger");
                var effecterCleanup = typeof(Effecter).GetMethod("Cleanup");
                var randomBoltMesh = typeof(LightningBoltMeshPool).GetProperty("RandomBoltMesh").GetGetMethod();
                var drawTrackerCtor = typeof(Pawn_DrawTracker).GetConstructor(new[] { typeof(Pawn) });
                var resolveAllGraphics = typeof(PawnGraphicSet).GetMethod("ResolveAllGraphics");
                var resolveApparelGraphics = typeof(PawnGraphicSet).GetMethod("ResolveApparelGraphics");

                var effectMethods = new MethodBase[] { subSustainerStart, sampleCtor, subSoundPlay, effecterTick, effecterTrigger, effecterCleanup, randomBoltMesh, drawTrackerCtor, resolveAllGraphics, resolveApparelGraphics };
                var moteMethods = typeof(MoteMaker).GetMethods(BindingFlags.Static | BindingFlags.Public);

                foreach (MethodBase m in effectMethods.Concat(moteMethods))
                    harmony.Patch(m, randPatchPrefix, randPatchPostfix);
            }

            // Set ThingContext and FactionContext (for pawns and buildings) in common Thing methods
            {
                var thingMethodPrefix = new HarmonyMethod(typeof(PatchThingMethods).GetMethod("Prefix"));
                var thingMethodPostfix = new HarmonyMethod(typeof(PatchThingMethods).GetMethod("Postfix"));
                var thingMethods = new[] { "Tick", "TickRare", "TickLong", "SpawnSetup", "TakeDamage", "Kill" };

                foreach (Type t in typeof(Thing).AllSubtypesAndSelf())
                {
                    foreach (string m in thingMethods)
                    {
                        MethodInfo method = t.GetMethod(m, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                        if (method != null)
                            harmony.Patch(method, thingMethodPrefix, thingMethodPostfix);
                    }
                }
            }

            // Full precision floating point saving
            {
                var doubleSavePrefix = new HarmonyMethod(typeof(ValueSavePatch).GetMethod(nameof(ValueSavePatch.DoubleSave_Prefix)));
                var floatSavePrefix = new HarmonyMethod(typeof(ValueSavePatch).GetMethod(nameof(ValueSavePatch.FloatSave_Prefix)));
                var valueSaveMethod = typeof(Scribe_Values).GetMethod(nameof(Scribe_Values.Look));

                harmony.Patch(valueSaveMethod.MakeGenericMethod(typeof(double)), doubleSavePrefix, null);
                harmony.Patch(valueSaveMethod.MakeGenericMethod(typeof(float)), floatSavePrefix, null);
            }

            // Set the map time for GUI methods depending on it
            {
                var setMapTimePrefix = new HarmonyMethod(AccessTools.Method(typeof(SetMapTimeForUI), "Prefix"));
                var setMapTimePostfix = new HarmonyMethod(AccessTools.Method(typeof(SetMapTimeForUI), "Postfix"));

                var windowMethods = new[] { "DoWindowContents", "WindowUpdate" };
                foreach (string m in windowMethods)
                    harmony.Patch(typeof(MainTabWindow_Inspect).GetMethod(m), setMapTimePrefix, setMapTimePostfix);

                foreach (var t in typeof(InspectTabBase).AllSubtypesAndSelf())
                {
                    var method = t.GetMethod("FillTab", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (method != null && !method.IsAbstract)
                        harmony.Patch(method, setMapTimePrefix, setMapTimePostfix);
                }
            }
        }

        public static UniqueList<Texture2D> icons = new UniqueList<Texture2D>();
        public static UniqueList<IconInfo> iconInfos = new UniqueList<IconInfo>();

        public class IconInfo
        {
            public bool hasStuff;
        }

        private static void CollectCursorIcons()
        {
            icons.Add(null);
            iconInfos.Add(null);

            foreach (var des in DefDatabase<DesignationCategoryDef>.AllDefsListForReading.SelectMany(c => c.AllResolvedDesignators))
            {
                if (des.icon == null) continue;

                if (icons.Add(des.icon))
                    iconInfos.Add(new IconInfo()
                    {
                        hasStuff = des is Designator_Build build && build.entDef.MadeFromStuff
                    });
            }
        }

        public static void ExposeIdBlock(ref IdBlock block, string label)
        {
            if (Scribe.mode == LoadSaveMode.Saving && block != null)
            {
                string base64 = Convert.ToBase64String(block.Serialize());
                Scribe_Values.Look(ref base64, label);
            }

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                string base64 = null;
                Scribe_Values.Look(ref base64, label);

                if (base64 != null)
                    block = IdBlock.Deserialize(new ByteReader(Convert.FromBase64String(base64)));
                else
                    block = null;
            }
        }

        public static void HandleReceive(ByteReader data, bool reliable)
        {
            try
            {
                Client.HandleReceive(data, reliable);
            }
            catch (Exception e)
            {
                Log.Error($"Exception handling packet by {Client}: {e}");
            }
        }
    }

    public static class FactionContext
    {
        private static Stack<Faction> stack = new Stack<Faction>();

        public static Faction Push(Faction faction)
        {
            if (faction == null || (faction.def != Multiplayer.FactionDef && faction.def != FactionDefOf.PlayerColony))
            {
                stack.Push(null);
                return null;
            }

            stack.Push(Find.FactionManager.OfPlayer);
            Set(faction);
            return faction;
        }

        public static Faction Pop()
        {
            Faction f = stack.Pop();
            if (f != null)
                Set(f);
            return f;
        }

        private static void Set(Faction faction)
        {
            Find.FactionManager.OfPlayer.def = Multiplayer.FactionDef;
            faction.def = FactionDefOf.PlayerColony;
            Find.FactionManager.ofPlayer = faction;
        }
    }

}

