using System;
using System.Collections.Generic;
using UnityEngine;

namespace SplitScreenCoop
{
    public partial class SplitScreenCoop
    {
        /// <summary>
        /// Give every additional player an independent room realizer. Sharing the
        /// primary realizer's mutable lists caused duplicate mutation and freezes.
        /// </summary>
        /// <summary>The player each additional realizer belongs to; its followCreature changes while that player is dead.</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RoomRealizer, AbstractCreature> realizerOwners = new();

        private void MakeRealizer2(RainWorldGame game)
        {
            additionalRealizers.Clear();
            realizer2 = null;
            if (game?.roomRealizer == null || game.session?.Players == null) return;

            foreach (AbstractCreature player in game.session.Players)
            {
                if (player == game.roomRealizer.followCreature) continue;
                var realizer = new RoomRealizer(player, game.world);
                realizerOwners.Add(realizer, player);
                additionalRealizers.Add(realizer);
            }
            realizer2 = additionalRealizers.Count > 0 ? additionalRealizers[0] : null;
            realizerBudgetRooms = CountLivingPlayerRooms(game);
            realizerBudgetShrinkTicks = 0;
            float budget = ApplyRealizerBudget(game);
            Logger.LogInfo($"Created {additionalRealizers.Count} additional room realizer(s); shared performance budget={budget} for {realizerBudgetRooms} room(s) with players");
        }

        // Every realizer measures the whole world's realized rooms against one budget
        // (see RoomRealizer_CurrentPerformanceEstimation). Vanilla keeps 1500 worth of
        // rooms realized around one player; the budget adds the Remix slider's share for
        // every other room the living players are spread over. It used to add a share
        // per player, dead ones included, so four players standing together kept 2.5x
        // vanilla's neighbour rooms realized, every one of them updating every tick. It
        // grows at once when players split up and shrinks only after they have been
        // together for 30 s, so someone stepping through a door and back does not make
        // the realizers drop rooms and load them again.
        private int realizerBudgetRooms = 1;
        private int realizerBudgetShrinkTicks;
        private const int RealizerBudgetShrinkTicks = 40 * 30;
        private readonly int[] livingPlayerRooms = new int[4];

        private int CountLivingPlayerRooms(RainWorldGame game)
        {
            int rooms = 0;
            List<AbstractCreature> players = game?.session?.Players;
            if (players != null)
                for (int p = 0; p < players.Count; p++)
                {
                    AbstractCreature player = players[p];
                    if (player?.Room == null || CreatureIsDead(player)) continue;
                    int index = player.Room.index;
                    bool counted = false;
                    for (int i = 0; i < rooms && !counted; i++) counted = livingPlayerRooms[i] == index;
                    if (!counted && rooms < livingPlayerRooms.Length) livingPlayerRooms[rooms++] = index;
                }
            return Math.Max(1, rooms);
        }

        private void UpdateRealizerBudget(RainWorldGame game)
        {
            if (game?.roomRealizer == null) return;
            int rooms = CountLivingPlayerRooms(game);
            if (rooms > realizerBudgetRooms)
            {
                realizerBudgetRooms = rooms;
                realizerBudgetShrinkTicks = 0;
            }
            else if (rooms < realizerBudgetRooms)
            {
                if (++realizerBudgetShrinkTicks < RealizerBudgetShrinkTicks) return;
                realizerBudgetRooms = rooms;
                realizerBudgetShrinkTicks = 0;
            }
            else
            {
                realizerBudgetShrinkTicks = 0;
                return;
            }
            float before = game.roomRealizer.performanceBudget;
            float budget = ApplyRealizerBudget(game);
            if (budget != before)
                Logger.LogInfo($"[RoomRealizer] frame={Time.frameCount} shared budget {before:0} -> {budget:0}: the living players are in {realizerBudgetRooms} room(s)");
        }

        private float ApplyRealizerBudget(RainWorldGame game)
        {
            float perExtraRoom = Options != null ? Options.ExtraRealizerBudget.Value : 750f;
            float budget = 1500f + perExtraRoom * Math.Max(0, realizerBudgetRooms - 1);
            if (game?.roomRealizer != null) game.roomRealizer.performanceBudget = budget;
            for (int i = 0; i < additionalRealizers.Count; i++)
                if (additionalRealizers[i] != null) additionalRealizers[i].performanceBudget = budget;
            return budget;
        }

        /// <summary>
        /// Vanilla sums only the rooms this realizer tracks. With one realizer per
        /// player that let N realizers each fill a vanilla budget, so the number of
        /// realized rooms, and the creatures updating in them every tick, scaled with
        /// the player count. Measure everything that is actually realized instead.
        /// </summary>
        public float RoomRealizer_CurrentPerformanceEstimation(On.RoomRealizer.orig_CurrentPerformanceEstimation orig, RoomRealizer self)
        {
            float own = orig(self);
            if (additionalRealizers.Count == 0 || self?.world?.activeRooms == null) return own;
            float total = 0f;
            for (int i = 0; i < self.world.activeRooms.Count; i++)
            {
                Room room = self.world.activeRooms[i];
                if (room?.abstractRoom != null && !room.abstractRoom.offScreenDen)
                    total += self.RoomPerformanceEstimation(room.abstractRoom);
            }
            return Mathf.Max(own, total);
        }

        private void TrackShortcutDestination(World world, AbstractRoom room)
        {
            if (world == null || room == null) return;
            world.ActivateRoom(room);
            RainWorldGame game = world.game;
            if (game?.roomRealizer?.world == world)
                game.roomRealizer.AddNewTrackedRoom(room, true);
            foreach (RoomRealizer realizer in additionalRealizers)
                if (realizer?.world == world)
                    realizer.AddNewTrackedRoom(room, false);
            Logger.LogInfo($"[CameraPreload] frame={Time.frameCount} destination={room.name}; realized={room.realizedRoom != null}; trackedRealizers={additionalRealizers.Count + (game?.roomRealizer == null ? 0 : 1)}");
        }

        public void OverWorld_WorldLoaded(On.OverWorld.orig_WorldLoaded orig, OverWorld self, bool warpUsed)
        {
            bool rebuild = additionalRealizers.Count > 0;
            additionalRealizers.Clear();
            realizer2 = null;
            orig(self, warpUsed);
            if (rebuild || self.game?.session?.Players?.Count > 1) MakeRealizer2(self.game);
            ClearStaleWorldCameras(self.game);
            EnsureStableCameraAssignments(self.game);
            RefreshActiveCameraRendering(self.game, "world loaded");
            LogCameraSnapshot(self.game, "world loaded", true);
            ConsiderColapsing(self.game, true);
        }

        /// <summary>
        /// One player through a gate unloads the old world for everybody, but the
        /// cameras of the players left behind keep their room and every sprite
        /// leaser of it. Nothing draws those leasers usefully again, and the ones
        /// holding Unity objects (rot spore renderers, mask meshes) turn into
        /// per-frame exceptions once the objects are destroyed. Clean them the way
        /// ChangeRoom would; the camera is re-populated when its player is moved.
        /// </summary>
        private void ClearStaleWorldCameras(RainWorldGame game)
        {
            if (game?.cameras == null || game.world == null) return;
            foreach (RoomCamera camera in game.cameras)
            {
                if (camera?.room == null || camera.room.world == game.world || camera.spriteLeasers == null) continue;
                // A camera already heading for a room of the new world (every camera
                // during a warp: WarpMoveCameraActual runs before WorldLoaded finishes)
                // is on its way, not left behind. Clearing it here tore the warp's
                // ripple and hold-frame mask sources out from under the camera the
                // players were watching and left its colours broken, and emptied the
                // other two cells until their move completed.
                if (camera.loadingRoom != null) continue;
                int count = camera.spriteLeasers.Count;
                for (int i = 0; i < count; i++)
                {
                    try { camera.spriteLeasers[i].CleanSpritesAndRemove(); }
                    catch (Exception error) { LogHookError("ClearStaleWorldCameras", error); }
                }
                camera.spriteLeasers.Clear();
                Logger.LogInfo($"[CameraMove] frame={Time.frameCount} cam={camera.cameraNumber} cleared {count} sprite leasers left in unloaded world {camera.room.world.name}/{camera.room.abstractRoom?.name}");
            }
        }

        /// <summary>
        /// Vanilla always copies camera zero's target into a realizer. Temporarily
        /// expose this realizer's own target there so each instance remains stable.
        /// A dead player's realizer, the main one included, follows a living player
        /// instead of the corpse: a predator dragging the body used to realize, and
        /// preload the neighbours of, every room on its way, all of which then updated
        /// every tick for the rest of the cycle. The dead player's own rooms age out
        /// the way rooms do behind any player who left them.
        /// </summary>
        public void RoomRealizer_Update(On.RoomRealizer.orig_Update orig, RoomRealizer self)
        {
            RainWorldGame game = self?.world?.game;
            if (game?.cameras == null || game.cameras.Length == 0)
            {
                orig(self);
                return;
            }

            AbstractCreature previous = game.cameras[0].followAbstractCreature;
            AbstractCreature target = self == game.roomRealizer ? previous
                : realizerOwners.TryGetValue(self, out AbstractCreature owner) ? owner : self.followCreature;
            if (target != null && game.session?.Players?.Count > 1 && CreatureIsDead(target))
                target = FirstLivingPlayer(game) ?? target;
            if (target == null || target == previous)
            {
                orig(self);
                return;
            }
            try
            {
                game.cameras[0].followAbstractCreature = target;
                orig(self);
            }
            finally
            {
                game.cameras[0].followAbstractCreature = previous;
            }
        }

        private static AbstractCreature FirstLivingPlayer(RainWorldGame game)
        {
            List<AbstractCreature> players = game?.session?.Players;
            if (players == null) return null;
            for (int i = 0; i < players.Count; i++)
                if (players[i] != null && !CreatureIsDead(players[i])) return players[i];
            return null;
        }

        private int lastKillRoomBlockFrame = -10000;

        /// <summary>
        /// RemoveNotVisitedRooms abstractizes straight through KillRoom without ever
        /// consulting CanAbstractizeRoom, so guarding that method alone is not enough:
        /// whenever one player walks into a new room, their realizer drops every room
        /// it has not personally visited, which includes the room the other player is
        /// standing in. Abstractizing it destroys that player's realized creature, and
        /// if they are mid-shortcut it is worse still - ShortcutHandler only advances a
        /// vessel while vessel.room.realizedRoom is non-null, so the player freezes in
        /// the pipe permanently and the layout then reports them dead.
        /// </summary>
        public void RoomRealizer_KillRoom(On.RoomRealizer.orig_KillRoom orig,
            RoomRealizer self, AbstractRoom room)
        {
            string previousMarker = HangMarker;
            HangMarker = "RoomRealizer_KillRoom";
            try
            {
                if (room != null && (RoomIsInUseByAnyPlayer(self?.world?.game, room) || RoomIsHeldByAnotherRealizer(self, room)))
                {
                    if (Time.frameCount - lastKillRoomBlockFrame > 200)
                    {
                        Logger.LogInfo($"[RoomRealizer] frame={Time.frameCount} refused to abstractize {room.name}; a player, their camera, their realizer or a shortcut vessel still needs it");
                        lastKillRoomBlockFrame = Time.frameCount;
                    }
                    return;
                }
                orig(self, room);
            }
            finally { HangMarker = previousMarker; }
        }

        /// <summary>
        /// A realizer only ever consults its own trackers before killing a room, so
        /// realizer B would drop the neighbour rooms realizer A had just loaded for
        /// player A, and A loaded them again on its next tick. Each reload is a full
        /// room load on the main thread. Refuse while any other realizer still counts
        /// the room as too fresh or too close to abstractize.
        /// </summary>
        private static bool RoomIsHeldByAnotherRealizer(RoomRealizer self, AbstractRoom room)
        {
            RainWorldGame game = self?.world?.game;
            if (game == null) return false;
            if (game.roomRealizer != null && game.roomRealizer != self && RealizerNeedsRoom(game.roomRealizer, room)) return true;
            foreach (RoomRealizer other in additionalRealizers)
                if (other != null && other != self && RealizerNeedsRoom(other, room)) return true;
            return false;
        }

        private static bool RealizerNeedsRoom(RoomRealizer realizer, AbstractRoom room)
        {
            if (realizer.followCreature == null || realizer.realizedRooms == null) return false;
            for (int i = 0; i < realizer.realizedRooms.Count; i++)
            {
                var tracker = realizer.realizedRooms[i];
                if (tracker?.room != room) continue;
                try { return !realizer.CanAbstractizeRoom(tracker); }
                catch (Exception) { return false; }
            }
            return false;
        }

        private static bool RoomIsInUseByAnyPlayer(RainWorldGame game, AbstractRoom room)
        {
            if (game == null || room == null) return false;

            if (game.session?.Players != null)
                foreach (AbstractCreature player in game.session.Players)
                {
                    // A corpse holds no room (see RoomRealizer_Update); a dead player's
                    // camera still holds its own, below.
                    if (player == null || CreatureIsDead(player)) continue;
                    if (player.Room == room) return true;
                    if (player.realizedCreature?.room?.abstractRoom == room) return true;
                }

            if (game.cameras != null)
                foreach (RoomCamera camera in game.cameras)
                {
                    if (camera == null) continue;
                    if (camera.room?.abstractRoom == room) return true;
                    if (camera.loadingRoom?.abstractRoom == room) return true;
                }

            ShortcutHandler shortcuts = game.shortcuts;
            if (shortcuts != null)
            {
                if (shortcuts.transportVessels != null)
                    foreach (ShortcutHandler.ShortCutVessel vessel in shortcuts.transportVessels)
                        if (vessel?.creature is Player && vessel.room == room) return true;
                if (shortcuts.betweenRoomsWaitingLobby != null)
                    foreach (ShortcutHandler.Vessel vessel in shortcuts.betweenRoomsWaitingLobby)
                        if (vessel?.creature is Player && vessel.room == room) return true;
                if (shortcuts.borderTravelVessels != null)
                    foreach (ShortcutHandler.BorderVessel vessel in shortcuts.borderTravelVessels)
                        if (vessel?.creature is Player && vessel.room == room) return true;
            }
            return false;
        }

        public bool rrNestedLock;

        /// <summary>
        /// A room may abstract only when every player realizer agrees it is safe.
        /// </summary>
        public bool RoomRealizer_CanAbstractizeRoom(On.RoomRealizer.orig_CanAbstractizeRoom orig,
            RoomRealizer self, RoomRealizer.RealizedRoomTracker tracker)
        {
            bool result = orig(self, tracker);
            if (!result || rrNestedLock) return result;

            try
            {
                rrNestedLock = true;
                RoomRealizer primary = self?.world?.game?.roomRealizer;
                if (primary != null && primary != self && primary.followCreature != null)
                    result &= primary.CanAbstractizeRoom(tracker);
                foreach (RoomRealizer other in additionalRealizers)
                    if (other != null && other != self && other.followCreature != null)
                        result &= other.CanAbstractizeRoom(tracker);
                return result;
            }
            finally
            {
                rrNestedLock = false;
            }
        }
    }
}
