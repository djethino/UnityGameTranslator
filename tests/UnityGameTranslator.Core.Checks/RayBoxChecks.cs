using System;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The arithmetic the inspector picks with (<see cref="RayBox"/>).
    ///
    /// 🔴 **Worth checking because its failures are invisible.** A wrong sign here does not throw
    /// and does not log: it shows up in a game as "sometimes I can select that object and sometimes
    /// I cannot, depending where I stand" — which is exactly what the pass this replaced did, and
    /// it took a week and a user's description of walking around a prop to name it.
    /// </summary>
    internal static class RayBoxChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            // A unit box from (1,-1,-1) to (3,1,1): straight ahead down +x, one unit wide.
            const float minX = 1f, minY = -1f, minZ = -1f, maxX = 3f, maxY = 1f, maxZ = 1f;

            float Ahead(float ox, float oy, float oz, float dx, float dy, float dz)
                => RayBox.Reach(ox, oy, oz, dx, dy, dz, minX, minY, minZ, maxX, maxY, maxZ);

            check(Near(Ahead(0, 0, 0, 1, 0, 0), 1f),
                "a ray aimed at the box reaches its near face",
                "the distance is where it enters, so the nearest object wins");

            check(Ahead(0, 0, 0, -1, 0, 0) < 0f,
                "a ray aimed away from the box misses it",
                "a box behind the eye is not something anybody is pointing at");

            check(Ahead(0, 5, 0, 1, 0, 0) < 0f,
                "a ray passing above the box misses it",
                "the whole point: the cursor either goes through the box or it does not");

            // 🔴 The case the old pass could not answer, and the reason it wandered: two corners
            // projected to the screen describe the box only when the camera is square to the world
            // axes. Here the ray comes in diagonally and still meets the box, exactly: from
            // (0,-3,0) along (1,1,0) it crosses the bottom face at (2,-1,0).
            //
            // ⚠ The answer counts in units of the DIRECTION, not in metres — the direction here is
            // √2 long, so 2 means 2√2 away. Callers compare answers from one ray, so it is the same
            // order either way. Written expecting metres the first time, and this case said so.
            check(Near(Ahead(0, -3, 0, 1, 1, 0), 2f),
                "a ray coming in diagonally meets the box where it really does",
                "the answer must not depend on how the box sits against the world axes");

            check(Ahead(0, -2, 0, 1, 0.1f, 0) < 0f,
                "and misses when the diagonal passes under it",
                "a test that says yes to everything would pick the whole scene");

            // ⚠ Standing inside it: entering is at zero, so the answer must be the FAR face or a
            // room would win every pick against everything it contains.
            check(Near(Ahead(2, 0, 0, 1, 0, 0), 1f),
                "from inside the box, the answer is where the ray leaves it",
                "what you see of a room you stand in is its far wall, not its near one");

            // Parallel to a pair of faces: between them, so this axis rules nothing out.
            check(Near(Ahead(0, 0, 0, 1, 0, 0), 1f) && Ahead(0, 9, 0, 1, 0, 0) < 0f,
                "a ray parallel to two faces is judged on whether it runs between them",
                "dividing by a direction of zero is the classic way this test goes wrong");

            // A flat box — a poster on a wall has no thickness at all.
            check(Near(RayBox.Reach(0, 0, 0, 1, 0, 0, 2f, -1f, -1f, 2f, 1f, 1f), 2f),
                "a box with no thickness is still met",
                "a plane is a box whose two faces touch; a strict comparison would miss it");
        }

        private static bool Near(float value, float expected)
            => Math.Abs(value - expected) < 0.0001f;
    }
}
