using System;
using System.Linq;
using UnityEngine;
using MonoMod.Cil;
using Mono.Cecil.Cil;
using HUD;
using System.Collections.Generic;
using CoopLeash;

namespace SplitScreenCoop
{
    public partial class SplitScreenCoop
    {
        public delegate AbstractCreature orig_get_FirstAlivePlayer(RainWorldGame self);
        public AbstractCreature get_FirstAlivePlayer(orig_get_FirstAlivePlayer orig, RainWorldGame self)
        {
            if (selfSufficientCoop) return self.session.Players.FirstOrDefault(p => !PlayerDeadOrMissing(p)) ?? orig(self); // null bad lmao
            return orig(self);
        }

        private void SaveState_SessionEnded(ILContext il)
        {
            try
            {
                var c = new ILCursor(il);
                c.GotoNext(MoveType.Before, // go to food clamped
                    i => i.MatchLdfld<SlugcatStats>("maxFood"),
                    i => i.MatchCallOrCallvirt(out _), // custom.intclamp
                    i => i.MatchStfld<SaveState>("food")
                    );
                c.GotoPrev(MoveType.Before, // go to start of clamp block
                    i => i.MatchLdarg(0),
                    i => i.MatchLdarg(0),
                    i => i.MatchLdfld<SaveState>("food")
                    );
                var skip = c.IncomingLabels.First(); // a jump that skipped vanilla
                var vanilla = il.DefineLabel();
                c.GotoPrev(MoveType.After, i => i.MatchBr(out var lab) && lab.Target == skip.Target); // right before vanilla block
                c.MoveAfterLabels();
                c.Emit<SplitScreenCoop>(OpCodes.Ldsfld, "selfSufficientCoop");
                c.Emit(OpCodes.Brfalse, vanilla);
                c.Emit(OpCodes.Ldarg_0);
                c.Emit(OpCodes.Ldarg_1);
                c.EmitDelegate<Action<SaveState, RainWorldGame>>(CoopSessionFood);
                c.Emit(OpCodes.Br, skip);
                c.MarkLabel(vanilla);
            }
            catch (Exception e)
            {
                Logger.LogError(e);
                throw;
            }
        }

        void CoopSessionFood(SaveState ss, RainWorldGame game)
        {
            Logger.LogInfo($"CoopSessionFood was {ss.food}");
            ss.food += (game.Players.Where(p => !PlayerDeadOrMissing(p)).OrderByDescending(p => (p.realizedCreature as Player).FoodInRoom(false)).First().realizedCreature as Player).FoodInRoom(true);
            Logger.LogInfo($"CoopSessionFood became {ss.food}");
        }

        private void RainWorldGame_ctor2(ILContext il)
        {
            try
            {
                var c = new ILCursor(il);
                int loc = 0;
                c.GotoNext(MoveType.Before, // go to food
                    i => i.MatchLdfld<SaveState>("food"),
                    i => i.MatchStloc(out loc)
                    );

                c.GotoNext(MoveType.Before, // go to start of 'vanilla while'
                    i => i.MatchBr(out _)
                    );
                var vanilla = il.DefineLabel();
                c.MoveAfterLabels();
                c.Emit<SplitScreenCoop>(OpCodes.Ldsfld, "selfSufficientCoop");
                c.Emit(OpCodes.Brfalse, vanilla);
                c.Emit(OpCodes.Ldarg_0);
                c.Emit(OpCodes.Ldloc, loc);
                c.EmitDelegate<Func<RainWorldGame, int, int>>(CoopStartingFood);
                c.Emit(OpCodes.Stloc, loc);
                c.MarkLabel(vanilla);
            }
            catch (Exception e)
            {
                Logger.LogError(e);
                throw;
            }
        }

        int CoopStartingFood(RainWorldGame game, int foodInSave)
        {
            Logger.LogInfo("CoopStartingFood");
            if (selfSufficientCoop && coopSharedFood)
            {
                if (foodInSave > 0)
                {
                    Logger.LogInfo($"CoopStartingFood shared {foodInSave} food");
                    game.session.Players.ForEach(p => { (p.state as PlayerState).foodInStomach = foodInSave; });
                    Logger.LogInfo($"CoopStartingFood p0 has {(game.session.Players[0].state as PlayerState).foodInStomach} food");
                    foodInSave = 0;
                }
            }
            return foodInSave;
        }


        private int RegionGate_PlayersInZone(On.RegionGate.orig_PlayersInZone orig, RegionGate self)
        {
            if (selfSufficientCoop)
            {
                // vanilla logic was just wrong alltogether?
                if (self.room.game.Players.Any(p => (!PlayerDeadOrMissing(p) && p.Room != self.room.abstractRoom))) return -1;
            }
            return orig(self);
        }


        private void Creature_FlyAwayFromRoom(On.Creature.orig_FlyAwayFromRoom orig, Creature self, bool carriedByOther)
        {
            if (self is Player pl && selfSufficientCoop && !pl.isNPC && carriedByOther) pl.Die();
            orig(self, carriedByOther);
        }

        // vanilla assumes players[0].realizedcreature not null
        private void RegionGate_get_MeetRequirement(ILContext il)
        {
            try
            {
                var c = new ILCursor(il);
                c.GotoNext(MoveType.Before, // StorySession If
                    i => i.MatchCallOrCallvirt<RainWorldGame>("get_Players"),
                    i => i.MatchLdcI4(0),
                    i => i.MatchCallOrCallvirt(out _), // get_item
                    i => i.MatchCallOrCallvirt<AbstractCreature>("get_realizedCreature")
                    );

                c.Index++;
                c.EmitDelegate<Func<List<AbstractCreature>, List<AbstractCreature>>>((List<AbstractCreature> players) =>
                {
                    if (selfSufficientCoop)
                    {
                        return players.Where(p => (!PlayerDeadOrMissing(p))).ToList();
                    }
                    return players;
                });
            }
            catch (Exception e)
            {
                Logger.LogError(e);
                throw;
            }
        }

        // we are multiplayer
        private bool ProcessManager_IsGameInMultiplayerContext(On.ProcessManager.orig_IsGameInMultiplayerContext orig, ProcessManager self)
        {
            if (self.currentMainLoop is RainWorldGame game && game.IsStorySession && selfSufficientCoop) return true;
            return orig(self);
        }

        // Jolly still doesn't know how to do it proper
        private void RoomCamera_ChangeCameraToPlayer(On.RoomCamera.orig_ChangeCameraToPlayer orig, RoomCamera self, AbstractCreature cameraTarget)
        {
            Logger.LogInfo("RoomCamera_ChangeCameraToPlayer");
            if(self.game.cameras.Length >= self.game.Players.Count) // fixed camera/player slots keep screen sides stable
            {
                return;
            }
            if (cameraTarget.realizedCreature is Player player)
            {
                AssignCameraToPlayer(self, player);
            }
            orig(self, cameraTarget);
        }

        private void Player_TriggerCameraSwitch(ILContext il)
        {
            try
            {
                var c = new ILCursor(il);
                c.GotoNext(MoveType.Before, // roomcam = 
                    i => i.MatchStloc(out var _)
                    );

                // rcam on stack
                c.Emit(OpCodes.Ldarg_0);
                c.EmitDelegate<Func<RoomCamera, Player, RoomCamera>>((RoomCamera rc, Player self) => // use the cam that's my own or a cam that is free or cam0, in this order
                {
                    var wasrc = rc;
                    rc = self.abstractCreature.world.game.cameras.FirstOrDefault(c => c.followAbstractCreature == self.abstractCreature);

                    if (stickTogetherEnabled)
                        rc = StickTogetherCameraPriority(self);

                    if (rc == null) rc = self.abstractCreature.world.game.cameras.FirstOrDefault(c => IsCreatureDead(c.followAbstractCreature));
                    if (rc == null) rc = self.abstractCreature.world.game.cameras.FirstOrDefault(c => c.cameraNumber == self.playerState.playerNumber);
                    if (rc == null) rc = wasrc;
                    return rc;
                });
            }
            catch (Exception e)
            {
                Logger.LogError(e);
                throw;
            }
        }

        private RoomCamera StickTogetherCameraPriority(Player self)
        {
            RoomCamera result = null; // self.abstractCreature.world.game.cameras.FirstOrDefault(c => c.followAbstractCreature == self.abstractCreature);
            if (self.GetCat().defector)
                result = self.abstractCreature.world.game.cameras[1];
            else
                result = self.abstractCreature.world.game.cameras[0];

            return result;
        }

        private void Player_TriggerCameraSwitch1(On.Player.orig_TriggerCameraSwitch orig, Player self)
        {
            if (CurrentSplitMode != SplitMode.NoSplit && self.abstractCreature.world.game.cameras.Length > self.playerState.playerNumber)
                ToggleCameraZoom(self.abstractCreature.world.game.cameras[self.playerState.playerNumber]);
            orig(self);
        }

        // $15
        private void Player_ctor(On.Player.orig_ctor orig, Player self, AbstractCreature abstractCreature, World world)
        {
            orig(self, abstractCreature, world);
            self.cameraSwitchDelay = -1; // there, you can have your fix, for free
        }

        private void SaveState_SessionEndedFriends(On.SaveState.orig_SessionEnded orig, SaveState self,
            RainWorldGame game, bool survived, bool newMalnourished)
        {
            orig(self, game, survived, newMalnourished);
            if (!selfSufficientCoop || !survived || game?.GetStorySession?.playerSessionRecords == null) return;

            AbstractCreature primaryFriend = game.GetStorySession.playerSessionRecords.FirstOrDefault(r => r != null)?.friendInDen;
            int additionalFriends = game.GetStorySession.playerSessionRecords
                .Where(r => r?.friendInDen != null && r.friendInDen != primaryFriend)
                .Select(r => r.friendInDen)
                .Distinct()
                .Count();
            self.deathPersistentSaveData.friendsSaved += additionalFriends;
            if (additionalFriends > 0)
                game.rainWorld.progression.SaveWorldStateAndProgression(self.malnourished);
        }

        private void Player_GetInitialSlugcatClass(On.Player.orig_GetInitialSlugcatClass orig, Player self)
        {
            orig(self);
            if (!selfSufficientCoop || self.playerState?.playerNumber != 1 || Options == null) return;
            string configured = Options.Player2Class.Value;
            if (string.IsNullOrEmpty(configured) || configured == "Campaign") return;

            if (configured == "White") self.SlugCatClass = SlugcatStats.Name.White;
            else if (configured == "Yellow") self.SlugCatClass = SlugcatStats.Name.Yellow;
            else if (configured == "Red") self.SlugCatClass = SlugcatStats.Name.Red;
            else if (configured == "Night") self.SlugCatClass = SlugcatStats.Name.Night;
            else if (configured == "Gourmand") self.SlugCatClass = MoreSlugcats.MoreSlugcatsEnums.SlugcatStatsName.Gourmand;
            else if (configured == "Artificer") self.SlugCatClass = MoreSlugcats.MoreSlugcatsEnums.SlugcatStatsName.Artificer;
            else if (configured == "Rivulet") self.SlugCatClass = MoreSlugcats.MoreSlugcatsEnums.SlugcatStatsName.Rivulet;
            else if (configured == "Spear") self.SlugCatClass = MoreSlugcats.MoreSlugcatsEnums.SlugcatStatsName.Spear;
            else if (configured == "Saint") self.SlugCatClass = MoreSlugcats.MoreSlugcatsEnums.SlugcatStatsName.Saint;
            else if (configured == "Watcher") self.SlugCatClass = Watcher.WatcherEnums.SlugcatStatsName.Watcher;
        }

        private void PlayerGraphics_ApplyPalette(On.PlayerGraphics.orig_ApplyPalette orig, PlayerGraphics self,
            RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, RoomPalette palette)
        {
            orig(self, sLeaser, rCam, palette);
            ApplyPlayer2Colors(self, sLeaser);
        }

        private void PlayerGraphics_DrawSprites(On.PlayerGraphics.orig_DrawSprites orig, PlayerGraphics self,
            RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float timeStacker, Vector2 camPos)
        {
            orig(self, sLeaser, rCam, timeStacker, camPos);
            ApplyPlayer2Colors(self, sLeaser);
        }

        private static void ApplyPlayer2Colors(PlayerGraphics graphics, RoomCamera.SpriteLeaser sLeaser)
        {
            if (!selfSufficientCoop || Options == null || graphics?.player?.playerState?.playerNumber != 1 || sLeaser?.sprites == null) return;
            Color body = Options.Player2BodyColor.Value;
            int[] bodySprites = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 10 };
            foreach (int index in bodySprites)
                if (index < sLeaser.sprites.Length && sLeaser.sprites[index] != null) sLeaser.sprites[index].color = body;
            if (sLeaser.sprites.Length > 9 && sLeaser.sprites[9] != null) sLeaser.sprites[9].color = Options.Player2FaceColor.Value;
            if (sLeaser.sprites.Length > 11 && sLeaser.sprites[11] != null) sLeaser.sprites[11].color = Options.Player2AccentColor.Value;
        }

        private void SlugcatSelectMenu_StartGame(On.Menu.SlugcatSelectMenu.orig_StartGame orig, Menu.SlugcatSelectMenu self, SlugcatStats.Name storyGameCharacter)
        {
            if (selfSufficientCoop)
            {
                Logger.LogInfo("Requesting p2 rewired signin");
                self.manager.rainWorld.RequestPlayerSignIn(1, null);
            }
            orig(self, storyGameCharacter);
        }

        // Don't move a player when they have map opened
        private void Player_JollyInputUpdate(On.Player.orig_JollyInputUpdate orig, Player self)
        {
            orig(self);
            self.standStillOnMapButton = true;
        }
    }
}
