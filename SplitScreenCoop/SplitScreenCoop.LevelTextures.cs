using System;
using MonoMod.Cil;
using Mono.Cecil.Cil;
using UnityEngine;

namespace SplitScreenCoop
{
    public partial class SplitScreenCoop
    {
        /// <summary>
        /// Which level image each camera's levelTexture currently holds, keyed by the
        /// manipulated room file name plus camera-position suffix. Every camera decodes
        /// a 1400x800 PNG on the main thread whenever it switches screens; when several
        /// cameras follow players through the same pipe, they all decode the same file
        /// in the same frame. A camera whose target image is already decoded in another
        /// camera's texture copies the raw pixels instead.
        /// </summary>
        private static readonly string[] loadedLevelTextureKeys = new string[4];
        private static int sharedLevelTextureCopies;

        /// <summary>
        /// Replaces `this.levelTexture.LoadImage(this.preLoadedTexture, false)` inside
        /// RoomCamera.ApplyPositionChange with a call that may copy from a sibling.
        /// </summary>
        private void RoomCamera_ApplyPositionChange(ILContext il)
        {
            var c = new ILCursor(il);
            // IL: ldarg.0; call get_levelTexture; ldarg.0; ldfld preLoadedTexture;
            //     ldc.i4.0; call ImageConversion::LoadImage; pop
            // levelTexture is a property, so match the getter call, not a field load.
            if (c.TryGotoNext(MoveType.Before,
                i => i.MatchLdarg(0),
                i => i.MatchCallOrCallvirt<RoomCamera>("get_levelTexture"),
                i => i.MatchLdarg(0),
                i => i.MatchLdfld<RoomCamera>("preLoadedTexture"),
                i => i.MatchLdcI4(0),
                i => i.MatchCall(typeof(ImageConversion), "LoadImage")))
            {
                c.Index += 5;
                c.Remove();
                c.Emit(OpCodes.Ldarg_0);
                c.EmitDelegate<Func<Texture2D, byte[], bool, RoomCamera, bool>>(LoadLevelTexture);
            }
            else Logger.LogError(new Exception("Couldn't IL-hook RoomCamera.ApplyPositionChange for level texture sharing from SplitScreenMod"));
        }

        private static bool LoadLevelTexture(Texture2D texture, byte[] bytes, bool markNonReadable, RoomCamera self)
        {
            int number = self?.cameraNumber ?? -1;
            bool tracked = number >= 0 && number < loadedLevelTextureKeys.Length;
            string key = null;
            try { key = LevelTextureKey(self); }
            catch (Exception error) { sLogger?.LogWarning("[LevelTexture] key failed: " + error.Message); }
            if (tracked && key != null && texture != null && self.game?.cameras != null)
            {
                foreach (RoomCamera other in self.game.cameras)
                {
                    if (other == null || other == self) continue;
                    int o = other.cameraNumber;
                    if (o < 0 || o >= loadedLevelTextureKeys.Length || loadedLevelTextureKeys[o] != key) continue;
                    Texture2D source = other.levelTexture;
                    if (source == null || source == texture || source.width != texture.width ||
                        source.height != texture.height || source.format != texture.format ||
                        source.mipmapCount != texture.mipmapCount) continue;
                    try
                    {
                        texture.LoadRawTextureData(source.GetRawTextureData<byte>());
                        texture.Apply(false, false);
                        loadedLevelTextureKeys[number] = key;
                        sharedLevelTextureCopies++;
                        NoteFrameEvent("level texture copied cam=" + number + " from cam=" + o);
                        return true;
                    }
                    catch (Exception error)
                    {
                        sLogger?.LogWarning("[LevelTexture] copy from cam " + o + " failed, decoding instead: " + error.Message);
                    }
                }
            }
            NoteFrameEvent("level texture decode cam=" + number);
            bool loaded = ImageConversion.LoadImage(texture, bytes, markNonReadable);
            if (tracked) loadedLevelTextureKeys[number] = loaded ? key : null;
            return loaded;
        }

        private static string LevelTextureKey(RoomCamera camera)
        {
            Room room = camera?.loadingRoom ?? camera?.room;
            if (room?.abstractRoom == null || camera.loadingCameraPos < 0 || camera.game == null) return null;
            string name = WorldLoader.RoomNameManipulator(room.abstractRoom.FileName, camera.game);
            return name + camera.CameraTextureSuffixManipulator(name, camera.loadingCameraPos);
        }
    }
}
