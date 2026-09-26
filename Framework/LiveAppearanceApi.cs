using System;
using StardewValley;

namespace LiveAppearanceFramework.Framework
{
    /// <summary>The implementation of <see cref="ILiveAppearanceApi"/> handed to other mods.</summary>
    public sealed class LiveAppearanceApi : ILiveAppearanceApi
    {
        private readonly RefreshEngine Engine;

        internal LiveAppearanceApi(RefreshEngine engine)
        {
            this.Engine = engine;
        }

        public event EventHandler<IAppearanceChangedEventArgs> AppearanceChanged
        {
            add => this.Engine.AppearanceChanged += value;
            remove => this.Engine.AppearanceChanged -= value;
        }

        public void Refresh(NPC npc)
        {
            this.Engine.Refresh(npc, "Api");
        }

        public string GetAppearanceId(NPC npc)
        {
            return this.Engine.GetAppliedId(npc);
        }

        public string GetExpectedAppearanceId(NPC npc)
        {
            return this.Engine.GetExpectedId(npc);
        }

        public void SafeChooseAppearance(NPC npc)
        {
            this.Engine.SafeChooseAppearance(npc);
        }
    }
}
