using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.GameData.Characters;

namespace LiveAppearanceFramework.Framework
{
    /// <summary>
    /// Wraps <see cref="NPC.ChooseAppearance"/>. Vanilla always ends that method with
    /// <c>Sprite.SpriteWidth/SpriteHeight = data.Size</c> and <c>ignoreSourceRectUpdates = false</c>, even when nothing
    /// changed. That breaks animations that use a different frame size or an extended source rect (vanilla
    /// clint_hammer, birdie_fish, dick_fish, event extendSourceRect, custom animations). This puts those values back
    /// when the NPC was in such a state, so only the texture changes.
    /// </summary>
    internal static class SafeAppearance
    {
        public static void Choose(NPC npc)
        {
            AnimatedSprite sprite = npc.Sprite;
            if (sprite == null)
            {
                npc.ChooseAppearance();
                return;
            }

            CharacterData data = npc.GetData();
            int width = sprite.SpriteWidth;
            int tempHeight = sprite.tempSpriteHeight;
            int height = GetBaseHeight(sprite);
            bool ignoreUpdates = sprite.ignoreSourceRectUpdates;
            Rectangle sourceRect = sprite.sourceRect;
            int frame = sprite.currentFrame;

            // A normal NPC already has these values, so vanilla's reset changes nothing and there's nothing to restore.
            bool special = ignoreUpdates
                || tempHeight != -1
                || (data != null && (width != data.Size.X || height != data.Size.Y));

            npc.ChooseAppearance();

            if (!special || !ReferenceEquals(npc.Sprite, sprite))
                return;

            // Only write when different, so nothing is marked dirty for multiplayer sync without reason.
            if (sprite.SpriteWidth != width)
                sprite.SpriteWidth = width;
            if (GetBaseHeight(sprite) != height)
                sprite.SpriteHeight = height; // this setter clears tempSpriteHeight, restored below
            sprite.tempSpriteHeight = tempHeight;
            sprite.ignoreSourceRectUpdates = ignoreUpdates;
            sprite.currentFrame = frame;
            sprite.sourceRect = sourceRect;
        }

        /// <summary>
        /// The sprite's base height. The <see cref="AnimatedSprite.SpriteHeight"/> getter returns
        /// <see cref="AnimatedSprite.tempSpriteHeight"/> instead while that's set, so hide it for the read.
        /// </summary>
        public static int GetBaseHeight(AnimatedSprite sprite)
        {
            int tempHeight = sprite.tempSpriteHeight;
            if (tempHeight == -1)
                return sprite.SpriteHeight;

            sprite.tempSpriteHeight = -1;
            int height = sprite.SpriteHeight;
            sprite.tempSpriteHeight = tempHeight;
            return height;
        }
    }
}
