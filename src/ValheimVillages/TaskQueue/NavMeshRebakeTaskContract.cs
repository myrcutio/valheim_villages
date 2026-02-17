namespace ValheimVillages.TaskQueue
{
    /// <summary>
    /// Task contract for navmesh rebake. Triggers enqueue a task with these attribute keys;
    /// the handler parses them and performs the bake. Bounds and agent settings are optional
    /// (handler computes bounds from village areas + beds when missing).
    /// </summary>
    public static class NavMeshRebakeTaskContract
    {
        /// <summary>Task name for the navmesh rebake handler.</summary>
        public const string TaskName = "navmesh_rebake";

        /// <summary>World-space minimum X of the bounding box (optional).</summary>
        public const string AttrMinX = "minX";

        /// <summary>World-space minimum Z of the bounding box (optional).</summary>
        public const string AttrMinZ = "minZ";

        /// <summary>World-space maximum X of the bounding box (optional).</summary>
        public const string AttrMaxX = "maxX";

        /// <summary>World-space maximum Z of the bounding box (optional).</summary>
        public const string AttrMaxZ = "maxZ";

        /// <summary>NavMesh agent radius in meters (optional; handler uses default if missing).</summary>
        public const string AttrAgentRadius = "agentRadius";

        /// <summary>NavMesh agent height in meters (optional).</summary>
        public const string AttrAgentHeight = "agentHeight";

        /// <summary>NavMesh agent max climb in meters (optional).</summary>
        public const string AttrAgentClimb = "agentClimb";

        /// <summary>Preset id for agent settings, e.g. "villager" (optional).</summary>
        public const string AttrAgentPreset = "agentPreset";

        /// <summary>Ledge drop height in meters for auto-link generation (optional; default 1m).</summary>
        public const string AttrLedgeDropHeight = "ledgeDropHeight";
    }
}
