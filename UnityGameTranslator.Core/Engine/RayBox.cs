namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Where a line of sight meets a box, in plain numbers.
    ///
    /// 🔴 **Pure by contract — no Unity, no state, no clock** — and that is the point: this is the
    /// arithmetic the inspector picks with, and a sign error in it is invisible in a game. It shows
    /// as "sometimes I can select that object and sometimes I cannot", which is what the pass it
    /// replaces actually did (2026-09-19: it projected two opposite corners of the box and treated
    /// them as its outline on screen, so the answer moved with the camera). Held by
    /// <c>RayBoxChecks</c>; the caller (<c>InspectorPicker.RayReachesBox</c>) only unpacks vectors.
    ///
    /// The slab method: each pair of parallel faces narrows the stretch of the ray that could still
    /// be inside the box. What is left over is the crossing.
    /// </summary>
    public static class RayBox
    {
        /// <summary>A direction smaller than this on an axis is parallel to that pair of faces.</summary>
        private const float Parallel = 1e-8f;

        /// <summary>
        /// How far along the ray the box is first SEEN, or -1 when it is not.
        ///
        /// ⚠ **From inside the box, the answer is where the ray LEAVES it.** Entering is at zero
        /// for anything one stands in, so a room would otherwise win every pick against everything
        /// it contains. What is actually seen of a box one is inside is its far face — honestly
        /// further away than the furniture in front of it.
        ///
        /// ⚠ The direction need not be normalised, but the answer is then in units of it rather
        /// than in metres. Callers compare answers from one ray, so it does not matter to them.
        /// </summary>
        public static float Reach(float originX, float originY, float originZ,
                                  float directionX, float directionY, float directionZ,
                                  float minX, float minY, float minZ,
                                  float maxX, float maxY, float maxZ)
        {
            float near = 0f, far = float.MaxValue;

            if (!Slab(originX, directionX, minX, maxX, ref near, ref far)) return -1f;
            if (!Slab(originY, directionY, minY, maxY, ref near, ref far)) return -1f;
            if (!Slab(originZ, directionZ, minZ, maxZ, ref near, ref far)) return -1f;

            if (far < 0f) return -1f;              // entirely behind the eye
            return near > 0f ? near : far;         // outside: where it starts; inside: where it ends
        }

        /// <summary>One axis: narrows [near, far], or says the ray misses the box outright.</summary>
        private static bool Slab(float origin, float direction, float low, float high,
                                 ref float near, ref float far)
        {
            // Parallel to this pair of faces: either the ray runs between them for its whole
            // length, or it never crosses them at all.
            if (direction > -Parallel && direction < Parallel) return origin >= low && origin <= high;

            float inverse = 1f / direction;
            float enter = (low - origin) * inverse;
            float leave = (high - origin) * inverse;
            if (enter > leave) { float swap = enter; enter = leave; leave = swap; }

            if (enter > near) near = enter;
            if (leave < far) far = leave;
            return near <= far;
        }
    }
}
