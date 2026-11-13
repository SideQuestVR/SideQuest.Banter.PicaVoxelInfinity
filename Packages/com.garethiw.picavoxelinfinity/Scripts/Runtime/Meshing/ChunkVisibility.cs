/////////////////////////////////////////////////////////////////////////
//
// PicaVoxel - The tiny voxel engine for Unity - http://picavoxel.com
// By Gareth Williams - @garethiw - http://gareth.pw
//
/////////////////////////////////////////////////////////////////////////

using Unity.Burst;
using Unity.Burst.CompilerServices;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace PicaVoxel
{
    /// <summary>
    /// Which sides of a chunk are joined by open space, for cave culling. A chunk's sides are numbered
    /// 0 -x, 1 +x, 2 -y, 3 +y, 4 -z, 5 +z, and each of the 15 pairs of sides has a bit in a 15 bit mask: set when some
    /// run of open voxels (air, or anything see-through) inside the chunk touches both of them.
    /// </summary>
    public static class ChunkConnectivity
    {
        /// <summary>Every pair joined: an empty chunk, and the value for a chunk not worked out yet.</summary>
        public const ushort All = 0x7FFF;

        /// <summary>The bit for a pair of different sides.</summary>
        public static int PairBit(int a, int b)
        {
            if (a > b) { int t = a; a = b; b = t; }
            return a * 5 - a * (a - 1) / 2 + (b - a - 1);
        }

        public static bool Connected(int mask, int a, int b) => a != b && (mask & (1 << PairBit(a, b))) != 0;

        /// <summary>The pairs joined by one run of open voxels that touches these sides (a bit per side).</summary>
        public static int PairsOf(int touchedSides)
        {
            int pairs = 0;
            for (int a = 0; a < 5; a++)
            {
                if ((touchedSides & (1 << a)) == 0) continue;
                for (int b = a + 1; b < 6; b++)
                    if ((touchedSides & (1 << b)) != 0)
                        pairs |= 1 << PairBit(a, b);
            }
            return pairs;
        }

        /// <summary>
        /// The cell of a chunk position in the visibility grid. An axis with a bound (&gt; 0) holds the chunks
        /// -bound..bound, one cell each; an unbounded axis wraps round its size, which is wide enough that no two
        /// chunks near the camera share a cell.
        /// </summary>
        public static int CellIndex(int3 p, int3 bound, int3 size)
        {
            int x = bound.x > 0 ? p.x + bound.x : ((p.x % size.x) + size.x) % size.x;
            int y = bound.y > 0 ? p.y + bound.y : ((p.y % size.y) + size.y) % size.y;
            int z = bound.z > 0 ? p.z + bound.z : ((p.z % size.z) + size.z) % size.z;
            return x + size.x * (y + size.y * z);
        }
    }

    /// <summary>
    /// Works out which chunks can be seen from the camera's chunk, by following open space from chunk to chunk: a
    /// chunk is reached by entering it through one side and leaving through another that its mask says is joined to
    /// the first. Paths only ever move away from the camera along each axis (never turning back on themselves), so
    /// every route to a chunk is the same length, which lets a plain breadth first walk merge them all.
    ///
    /// It is conservative: a chunk it leaves out can't be seen through any run of open space from the camera's chunk,
    /// but one it keeps may still be hidden behind solid ground (it only follows connections, it does not look at
    /// where things are). Chunks it never reaches are underground and sealed off, or behind the world's floor.
    /// Unknown chunks (no mask yet) count as fully open, so the walk goes through them rather than hiding what's
    /// beyond; the camera's own chunk can always leave by any side.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct ChunkVisibilityJob : IJob
    {
        /// <summary>Per cell: the chunk's mask (<see cref="ChunkConnectivity.All"/> where unknown or no chunk).</summary>
        [ReadOnly] public NativeArray<ushort> Masks;
        /// <summary>Out, per cell: 1 when the chunk can be seen.</summary>
        public NativeArray<byte> Visible;
        /// <summary>Scratch, per cell: the sides the walk entered the chunk through.</summary>
        public NativeArray<byte> Incoming;
        /// <summary>Scratch: the chunks to visit, as many as there are cells.</summary>
        public NativeArray<int3> Queue;

        public int3 Size;
        /// <summary>Per axis, the chunk coordinates run -Bound..Bound when this is above 0, else without limit.</summary>
        public int3 Bound;
        /// <summary>The camera's chunk.</summary>
        public int3 Start;
        /// <summary>Chunks further than this (in chunks, as a distance) from the camera's chunk aren't entered.</summary>
        public int Radius;

        const byte Anywhere = 64;

        public void Execute()
        {
            for (int i = 0; i < Visible.Length; i++)
            {
                Visible[i] = 0;
                Incoming[i] = 0;
            }

            int3 start = math.select(Start, math.clamp(Start, -Bound, Bound), Bound > 0);
            int radiusSq = Radius * Radius;

            int head = 0, tail = 0;
            int startCell = ChunkConnectivity.CellIndex(start, Bound, Size);
            Queue[tail++] = start;
            Visible[startCell] = 1;
            Incoming[startCell] = Anywhere;

            while (head < tail)
            {
                int3 p = Queue[head++];
                int cell = ChunkConnectivity.CellIndex(p, Bound, Size);
                int entered = Incoming[cell];
                int mask = Masks[cell];
                int3 off = p - start;

                for (int side = 0; side < 6; side++)
                {
                    int axis = side >> 1;
                    bool positive = (side & 1) == 1;
                    int o = off[axis];
                    // Never back towards the camera along an axis: a path that has moved +x can't move -x
                    if (positive ? o < 0 : o > 0)
                        continue;

                    if (entered != Anywhere)
                    {
                        bool joined = false;
                        for (int from = 0; from < 6 && !joined; from++)
                            joined = (entered & (1 << from)) != 0 && ChunkConnectivity.Connected(mask, from, side);
                        if (!joined)
                            continue;
                    }

                    int3 q = p;
                    q[axis] += positive ? 1 : -1;
                    if (Bound[axis] > 0 && math.abs(q[axis]) > Bound[axis])
                        continue;
                    int3 d = q - start;
                    if (math.dot(d, d) > radiusSq)
                        continue;

                    int next = ChunkConnectivity.CellIndex(q, Bound, Size);
                    // Entered through the side facing the one we left by
                    Incoming[next] |= (byte)(1 << (side ^ 1));
                    if (Visible[next] == 0)
                    {
                        Visible[next] = 1;
                        Queue[tail++] = q;
                    }
                }
            }
        }
    }
}
