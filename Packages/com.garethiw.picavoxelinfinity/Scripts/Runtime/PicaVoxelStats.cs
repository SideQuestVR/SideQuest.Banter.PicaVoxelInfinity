/////////////////////////////////////////////////////////////////////////
//
// PicaVoxel - The tiny voxel engine for Unity - http://picavoxel.com
// By Gareth Williams - @garethiw - http://gareth.pw
//
/////////////////////////////////////////////////////////////////////////

namespace PicaVoxel
{
    /// <summary>
    /// Running totals of the work volumes do, for profiling and tests: compare two readings to see what a stretch of
    /// play cost. Counted on the main thread, across all volumes.
    /// </summary>
    public static class PicaVoxelStats
    {
        /// <summary>Data generation jobs started.</summary>
        public static int DataJobs;
        /// <summary>Mesh jobs scheduled (Culled).</summary>
        public static int MeshJobs;
        /// <summary>Mesh results uploaded from jobs.</summary>
        public static int MeshesApplied;
        /// <summary>Meshes built on the main thread for an edit.</summary>
        public static int ImmediateMeshes;
        /// <summary>Collision meshes cooked on a worker and assigned.</summary>
        public static int ColliderBakes;
        /// <summary>Chunks instantiated or recycled into a new position.</summary>
        public static int ChunksPlaced;
        /// <summary>Cave culling walks done (one whenever the camera's chunk or a chunk's connectivity changes).</summary>
        public static int CullWalks;

        public static void Reset()
        {
            DataJobs = MeshJobs = MeshesApplied = ImmediateMeshes = ColliderBakes = ChunksPlaced = CullWalks = 0;
        }

        public static string Summary() =>
            $"data={DataJobs} meshJobs={MeshJobs} applied={MeshesApplied} immediate={ImmediateMeshes} " +
            $"bakes={ColliderBakes} placed={ChunksPlaced} cullWalks={CullWalks}";
    }
}
