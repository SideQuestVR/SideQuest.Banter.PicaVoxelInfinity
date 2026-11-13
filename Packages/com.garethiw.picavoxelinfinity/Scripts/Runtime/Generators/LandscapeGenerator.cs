/////////////////////////////////////////////////////////////////////////
//
// PicaVoxel - The tiny voxel engine for Unity - http://picavoxel.com
// By Gareth Williams - @garethiw - http://gareth.pw
//
/////////////////////////////////////////////////////////////////////////

using System;
using UnityEngine;

namespace PicaVoxel
{
    /// <summary>
    /// Terrain with some shape to it, made the way Minecraft builds its land (much simplified): lowlands with lakes
    /// and sandy shores, rolling hills, mountain ranges with ridged peaks and valleys, bare rock on cliffs and snow on
    /// the summits, tunnels that wind through the ground (now and then breaking out at the surface) and the odd
    /// cavern, a bedrock floor, and trees spread through forested areas. TreeProcessor adds the leaves.
    ///
    /// The sand, snow and water values match the "landscape" tile sheet (Textures/minecrafttiles-pv-landscape).
    /// Water wants a custom block on the Volume with Has Transparency, Hide Faces Against Same and No Collision, and
    /// the tile array terrain shader's Water Block Value set to it.
    ///
    /// Thread safe and deterministic (chunks generate on several threads). Heights are worked out every 4 columns and
    /// the caves every 4 blocks, blended between (as Minecraft does), so a chunk costs a few hundred noise lookups
    /// rather than thousands.
    /// </summary>
    public class LandscapeGenerator : MonoBehaviour, I_VoxelDataGenerator
    {
        public int Seed { get; set; }
        public bool IsReady => true;

        [Header("Blocks (voxel values)")]
        public byte GrassValue = 0;
        public byte DirtValue = 1;
        public byte StoneValue = 2;
        public byte BedrockValue = 4;
        public byte SandValue = 14;
        public byte SnowValue = 15;
        public byte WaterValue = 16;
        public int TreeTrunkValue = 3;

        [Header("Height (world y)")]
        [Tooltip("Lakes fill up to here")]
        public int SeaLevel = -1;
        [Tooltip("The ground in the lowlands (lake beds, below sea level)")]
        public float LowlandHeight = -3f;
        [Tooltip("The ground in hilly country, before mountains")]
        public float HillHeight = 8f;
        [Tooltip("Rolling hills on top of that, up and down")]
        public float HillRoughness = 4f;
        [Tooltip("How far mountains rise above the land around them")]
        public float MountainHeight = 30f;
        [Tooltip("Share of the land that's mountainous (0-1)")]
        [Range(0f, 1f)] public float MountainCoverage = 0.3f;
        [Tooltip("The ground never goes above this (leave room for trees and their leaves below the volume's top)")]
        public int MaxHeight = 33;
        [Tooltip("Bedrock from the bottom of the volume up to here")]
        public int BedrockHeight = -38;
        [Tooltip("Scale of everything: bigger spreads the land out")]
        public float Scale = 1f;

        [Header("Surface")]
        [Tooltip("Slopes steeper than this (blocks up per block across) are bare stone")]
        public float CliffSlope = 1.5f;
        [Tooltip("Above this the ground starts to be stone, more so higher up")]
        public int RockHeight = 18;
        [Tooltip("Above this the summits are snow")]
        public int SnowHeight = 25;
        public int DirtDepth = 3;

        [Header("Caves")]
        public bool Caves = true;
        [Tooltip("How wide the winding tunnels are")]
        [Range(0f, 0.3f)] public float TunnelWidth = 0.1f;
        [Tooltip("How often tunnels break out at the surface (0-1)")]
        [Range(0f, 1f)] public float CaveEntrances = 0.3f;
        [Tooltip("How much of the underground opens into caverns (0-1)")]
        [Range(0f, 1f)] public float Caverns = 0.3f;

        [Header("Trees")]
        public bool Trees = true;
        [Tooltip("Spacing of possible tree spots (one per cell this many blocks across)")]
        public int TreeSpacing = 6;
        [Tooltip("How much of the land is forest (0-1)")]
        [Range(0f, 1f)] public float ForestCover = 0.4f;
        [Tooltip("Chance of a tree in each cell outside forests")]
        [Range(0f, 1f)] public float LoneTrees = 0.06f;
        public int MinTrunk = 4;
        public int MaxTrunk = 7;
        [Tooltip("No trees above this height")]
        public int TreeLine = 22;

        // Noise: set up once, under a lock, on whichever thread gets there first
        private FastNoiseLite _warp, _continents, _hills, _mountains, _ridges, _detail, _forest, _entrances;
        private FastNoiseLite _tunnelA, _tunnelB, _caverns;
        private readonly object _noiseLock = new object();
        private volatile bool _noiseReady;
        private int _noiseSeed;

        private void EnsureNoise()
        {
            if (_noiseReady && _noiseSeed == Seed)
                return;
            lock (_noiseLock)
            {
                if (_noiseReady && _noiseSeed == Seed)
                    return;
                int s = Seed;
                float f = 1f / Mathf.Max(0.1f, Scale);
                _warp = Noise(s + 1, 0.006f * f, FastNoiseLite.FractalType.None, 1);
                _warp.SetDomainWarpType(FastNoiseLite.DomainWarpType.OpenSimplex2);
                _warp.SetDomainWarpAmp(24f * Mathf.Max(0.1f, Scale));
                _continents = Noise(s + 2, 0.0035f * f, FastNoiseLite.FractalType.FBm, 3);
                _hills = Noise(s + 3, 0.02f * f, FastNoiseLite.FractalType.FBm, 2);
                _mountains = Noise(s + 4, 0.005f * f, FastNoiseLite.FractalType.FBm, 2);
                _ridges = Noise(s + 5, 0.011f * f, FastNoiseLite.FractalType.Ridged, 3);
                _detail = Noise(s + 6, 0.08f * f, FastNoiseLite.FractalType.None, 1);
                _forest = Noise(s + 7, 0.012f * f, FastNoiseLite.FractalType.FBm, 2);
                _entrances = Noise(s + 8, 0.02f * f, FastNoiseLite.FractalType.None, 1);
                _tunnelA = Noise(s + 9, 0.024f * f, FastNoiseLite.FractalType.None, 1);
                _tunnelB = Noise(s + 10, 0.024f * f, FastNoiseLite.FractalType.None, 1);
                _caverns = Noise(s + 11, 0.03f * f, FastNoiseLite.FractalType.FBm, 2);
                _noiseSeed = s;
                _noiseReady = true;
            }
        }

        private static FastNoiseLite Noise(int seed, float frequency, FastNoiseLite.FractalType fractal, int octaves)
        {
            var n = new FastNoiseLite(seed);
            n.SetNoiseType(FastNoiseLite.NoiseType.OpenSimplex2);
            n.SetFrequency(frequency);
            n.SetFractalType(fractal);
            n.SetFractalOctaves(octaves);
            n.SetFractalLacunarity(2f);
            n.SetFractalGain(0.5f);
            return n;
        }

        // Columns //////////////////////////////////////////////////////////
        // Worked out 16x16 at a time and kept per thread, since a chunk generates its columns in turn on one thread.
        // The height noise is sampled every 4 columns and blended (with a fine detail noise added per column).

        private const int Block = 16;
        private const int Border = Block + 2;      // with a column each side, for slopes
        private const int HStep = 4;
        private const int HNodes = 7;              // 4-column samples covering the bordered block

        private sealed class ColumnBlock
        {
            public LandscapeGenerator Owner;
            public int Seed, Bx, Bz;
            public readonly float[] Nodes = new float[HNodes * HNodes];
            public readonly float[] Height = new float[Border * Border];
            public readonly int[] Ground = new int[Block * Block];
            public readonly byte[] Surface = new byte[Block * Block];
            public readonly byte[] Under = new byte[Block * Block];
            public readonly byte[] UnderDepth = new byte[Block * Block];
            public readonly int[] TreeTop = new int[Block * Block];
            public readonly bool[] Entrance = new bool[Block * Block];
        }

        // A few blocks per thread, so chunks bigger than a block (or not lined up with them) don't keep redoing them
        private const int Slots = 4;
        [ThreadStatic] private static ColumnBlock[] t_columns;
        [ThreadStatic] private static int t_columnsNext;

        private ColumnBlock Columns(int x, int z)
        {
            int bx = FloorDiv(x, Block), bz = FloorDiv(z, Block);
            ColumnBlock[] slots = t_columns ??= new ColumnBlock[Slots];
            for (int s = 0; s < Slots; s++)
            {
                ColumnBlock hit = slots[s];
                if (hit != null && ReferenceEquals(hit.Owner, this) && hit.Seed == Seed && hit.Bx == bx && hit.Bz == bz)
                    return hit;
            }
            ColumnBlock c = slots[t_columnsNext] ??= new ColumnBlock();
            t_columnsNext = (t_columnsNext + 1) % Slots;

            int x0 = bx * Block, z0 = bz * Block;
            int nx0 = x0 - HStep, nz0 = z0 - HStep;    // first sample, a step before the bordered block
            for (int j = 0; j < HNodes; j++)
            for (int i = 0; i < HNodes; i++)
                c.Nodes[j * HNodes + i] = HeightAt(nx0 + i * HStep, nz0 + j * HStep);

            for (int j = 0; j < Border; j++)
            for (int i = 0; i < Border; i++)
            {
                int wx = x0 + i - 1, wz = z0 + j - 1;
                float fx = (wx - nx0) / (float)HStep, fz = (wz - nz0) / (float)HStep;
                int ix = (int)fx, iz = (int)fz;
                fx -= ix; fz -= iz;
                float a = c.Nodes[iz * HNodes + ix], b = c.Nodes[iz * HNodes + ix + 1];
                float d = c.Nodes[(iz + 1) * HNodes + ix], e = c.Nodes[(iz + 1) * HNodes + ix + 1];
                float h = Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(d, e, fx), fz);
                c.Height[j * Border + i] = Mathf.Min(h + _detail.GetNoise(wx, wz) * 0.7f, MaxHeight);
            }

            for (int j = 0; j < Block; j++)
            for (int i = 0; i < Block; i++)
            {
                int k = j * Block + i;
                int wx = x0 + i, wz = z0 + j;
                float h = c.Height[(j + 1) * Border + i + 1];
                int ground = Mathf.FloorToInt(h);
                float slope = Mathf.Max(
                    Mathf.Abs(c.Height[(j + 1) * Border + i + 2] - c.Height[(j + 1) * Border + i]),
                    Mathf.Abs(c.Height[(j + 2) * Border + i + 1] - c.Height[j * Border + i + 1])) * 0.5f;
                float speckle = _detail.GetNoise(wx * 3.1f, wz * 3.1f);

                byte surface, under;
                int underDepth = Mathf.Max(1, DirtDepth + Mathf.RoundToInt(_detail.GetNoise(wz, wx) * 1.5f));
                if (ground <= SeaLevel + 1)
                {
                    // Lake beds and their shores
                    surface = SandValue;
                    under = SandValue;
                }
                else if (ground >= SnowHeight + speckle * 2f && slope < CliffSlope * 1.6f)
                {
                    surface = SnowValue;
                    under = StoneValue;
                    underDepth = 0;
                }
                else if (slope > CliffSlope || (ground - RockHeight) / 8f + speckle * 0.5f > 0.5f)
                {
                    // Cliffs, and more and more bare rock above the rock line
                    surface = StoneValue;
                    under = StoneValue;
                    underDepth = 0;
                }
                else
                {
                    surface = GrassValue;
                    under = DirtValue;
                }

                c.Ground[k] = ground;
                c.Surface[k] = surface;
                c.Under[k] = under;
                c.UnderDepth[k] = (byte)underDepth;
                c.Entrance[k] = Caves && ground > SeaLevel + 1 && _entrances.GetNoise(wx, wz) > 1f - CaveEntrances;
                c.TreeTop[k] = int.MinValue;
            }
            c.Owner = this;
            c.Seed = Seed;
            c.Bx = bx;
            c.Bz = bz;

            if (Trees)
                PlaceTrees(c, x0, z0);
            return c;
        }

        // The ground height of a column (before the fine detail)
        private float HeightAt(int x, int z)
        {
            float wx = x, wz = z;
            _warp.DomainWarp(ref wx, ref wz);

            float continent = _continents.GetNoise(wx, wz);
            float hilliness = Smooth(-0.45f, 0.45f, continent);
            float h = Mathf.Lerp(LowlandHeight, HillHeight, hilliness);
            h += _hills.GetNoise(wx, wz) * HillRoughness * (0.4f + 0.6f * hilliness);

            // Mountain ranges where the slow mountain noise is high; ridged noise inside them for peaks and valleys
            float edge = Mathf.Lerp(0.35f, -0.35f, MountainCoverage);
            float range = Smooth(edge - 0.1f, edge + 0.2f, _mountains.GetNoise(wx, wz));
            if (range > 0f)
            {
                float ridge = Mathf.Clamp01((_ridges.GetNoise(wx, wz) + 1f) * 0.5f);
                h += range * (0.25f + 0.75f * ridge * ridge) * MountainHeight;
            }
            return h;
        }

        // One possible tree per TreeSpacing cell, at a spot in it chosen by hash, more of them in forest
        private void PlaceTrees(ColumnBlock c, int x0, int z0)
        {
            int cell = Mathf.Max(3, TreeSpacing);
            int cx0 = FloorDiv(x0, cell), cz0 = FloorDiv(z0, cell);
            int cx1 = FloorDiv(x0 + Block - 1, cell), cz1 = FloorDiv(z0 + Block - 1, cell);
            for (int cz = cz0; cz <= cz1; cz++)
            for (int cx = cx0; cx <= cx1; cx++)
            {
                uint h = Hash(cx, cz, Seed ^ 0x5eed);
                // A block in from the cell edge, so neighbouring trees don't grow into each other
                int tx = cx * cell + 1 + (int)(h % (uint)(cell - 2));
                int tz = cz * cell + 1 + (int)((h >> 8) % (uint)(cell - 2));
                int i = tx - x0, j = tz - z0;
                if (i < 0 || i >= Block || j < 0 || j >= Block)
                    continue;

                float forest = (_forest.GetNoise(tx, tz) + 1f) * 0.5f;
                float chance = forest > 1f - ForestCover ? 0.85f : LoneTrees;
                if (((h >> 16) & 0xffff) / 65535f >= chance)
                    continue;

                int k = j * Block + i;
                int ground = c.Ground[k];
                if (c.Surface[k] != GrassValue || ground > TreeLine)
                    continue;
                // Not where a tunnel has taken the ground away
                if (Caves && IsCave(tx, ground, tz, ground, c.Entrance[k], ref t_treeCaves))
                    continue;

                int trunk = MinTrunk + (int)((h >> 4) % (uint)Math.Max(1, MaxTrunk - MinTrunk + 1));
                c.TreeTop[k] = ground + trunk;
            }
        }

        // Caves ////////////////////////////////////////////////////////////
        // The cave noise sampled every 4 blocks over a 16-block cube and blended between, kept per thread like the
        // columns (with a second one for the tree checks, which look at other cubes)

        private const int Step = 4;
        private const int Lattice = Block / Step + 1;

        private sealed class CaveBlock
        {
            public LandscapeGenerator Owner;
            public int Seed, Bx, By, Bz;
            public readonly float[] A = new float[Lattice * Lattice * Lattice];
            public readonly float[] B = new float[Lattice * Lattice * Lattice];
            public readonly float[] C = new float[Lattice * Lattice * Lattice];
        }

        private sealed class CaveCache
        {
            public readonly CaveBlock[] Slots = new CaveBlock[LandscapeGenerator.Slots];
            public int Next;
        }

        [ThreadStatic] private static CaveCache t_caves;
        [ThreadStatic] private static CaveCache t_treeCaves;

        private CaveBlock CavesAround(int x, int y, int z, ref CaveCache cache)
        {
            int bx = FloorDiv(x, Block), by = FloorDiv(y, Block), bz = FloorDiv(z, Block);
            CaveCache cc = cache ??= new CaveCache();
            for (int s = 0; s < Slots; s++)
            {
                CaveBlock hit = cc.Slots[s];
                if (hit != null && ReferenceEquals(hit.Owner, this) && hit.Seed == Seed && hit.Bx == bx && hit.By == by && hit.Bz == bz)
                    return hit;
            }
            CaveBlock c = cc.Slots[cc.Next] ??= new CaveBlock();
            cc.Next = (cc.Next + 1) % Slots;
            for (int k = 0; k < Lattice; k++)
            for (int j = 0; j < Lattice; j++)
            for (int i = 0; i < Lattice; i++)
            {
                int n = (k * Lattice + j) * Lattice + i;
                float sx = bx * Block + i * Step, sy = by * Block + j * Step, sz = bz * Block + k * Step;
                // Squashed vertically, so tunnels and caverns run more across than up and down
                c.A[n] = _tunnelA.GetNoise(sx, sy * 1.5f, sz);
                c.B[n] = _tunnelB.GetNoise(sx, sy * 1.5f, sz);
                c.C[n] = _caverns.GetNoise(sx, sy * 2f, sz);
            }
            c.Owner = this;
            c.Seed = Seed;
            c.Bx = bx;
            c.By = by;
            c.Bz = bz;
            return c;
        }

        private bool IsCave(int x, int y, int z, int ground, bool entrance, ref CaveCache cache)
        {
            if (y <= BedrockHeight + 2)
                return false;
            // Near the surface only at entrances (never under a lake); caverns stay well below it
            int depth = ground - y;
            if (depth < 4 && !entrance)
                return false;
            if (ground <= SeaLevel + 1 && depth < 6)
                return false;

            CaveBlock c = CavesAround(x, y, z, ref cache);
            int lx = x - c.Bx * Block, ly = y - c.By * Block, lz = z - c.Bz * Block;
            int i = lx / Step, j = ly / Step, k = lz / Step;
            float fx = (lx - i * Step) / (float)Step, fy = (ly - j * Step) / (float)Step, fz = (lz - k * Step) / (float)Step;

            float a = Trilinear(c.A, i, j, k, fx, fy, fz);
            float b = Trilinear(c.B, i, j, k, fx, fy, fz);
            // A tunnel runs along where both noises cross zero
            if (a * a + b * b < TunnelWidth * TunnelWidth)
                return true;

            if (depth >= 8 && Caverns > 0f)
            {
                float cav = Trilinear(c.C, i, j, k, fx, fy, fz);
                if (cav > 0.75f - Caverns * 0.5f)
                    return true;
            }
            return false;
        }

        private static float Trilinear(float[] v, int i, int j, int k, float fx, float fy, float fz)
        {
            int n000 = (k * Lattice + j) * Lattice + i;
            int n100 = n000 + 1, n010 = n000 + Lattice, n110 = n010 + 1;
            int n001 = n000 + Lattice * Lattice, n101 = n001 + 1, n011 = n001 + Lattice, n111 = n011 + 1;
            float x00 = v[n000] + (v[n100] - v[n000]) * fx;
            float x10 = v[n010] + (v[n110] - v[n010]) * fx;
            float x01 = v[n001] + (v[n101] - v[n001]) * fx;
            float x11 = v[n011] + (v[n111] - v[n011]) * fx;
            float y0 = x00 + (x10 - x00) * fy;
            float y1 = x01 + (x11 - x01) * fy;
            return y0 + (y1 - y0) * fz;
        }

        // Voxels ///////////////////////////////////////////////////////////

        public bool GenerateVoxel(int x, int y, int z, ref Voxel voxel)
        {
            EnsureNoise();
            ColumnBlock col = Columns(x, z);
            int k = (z - col.Bz * Block) * Block + (x - col.Bx * Block);
            int ground = col.Ground[k];
            voxel.Color = new Color32(255, 255, 255, 255);

            // Bedrock floor, its top a little uneven
            if (y <= BedrockHeight + (int)(Hash(x, z, Seed) % 3u) - 1)
            {
                Solid(ref voxel, BedrockValue);
                return true;
            }

            if (y > ground)
            {
                if (y <= SeaLevel)
                    Solid(ref voxel, WaterValue);
                else if (y <= col.TreeTop[k])
                    Solid(ref voxel, (byte)TreeTrunkValue);
                else
                    Air(ref voxel);
                return true;
            }

            if (Caves && IsCave(x, y, z, ground, col.Entrance[k], ref t_caves))
            {
                Air(ref voxel);
                return true;
            }

            byte value = y == ground ? col.Surface[k] : y >= ground - col.UnderDepth[k] ? col.Under[k] : StoneValue;
            Solid(ref voxel, value);
            return true;
        }

        private static void Solid(ref Voxel v, byte value)
        {
            v.State = 1;
            v.Value = value;
        }

        private static void Air(ref Voxel v)
        {
            v.State = 0;
            v.Value = 0;
        }

        private static float Smooth(float a, float b, float t)
        {
            t = Mathf.Clamp01((t - a) / (b - a));
            return t * t * (3f - 2f * t);
        }

        private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;

        private static uint Hash(int x, int z, int seed)
        {
            unchecked
            {
                uint h = (uint)seed * 0x9E3779B1u;
                h ^= (uint)x * 0x85EBCA77u;
                h = (h << 13) | (h >> 19);
                h ^= (uint)z * 0xC2B2AE3Du;
                h ^= h >> 16;
                h *= 0x7FEB352Du;
                h ^= h >> 15;
                h *= 0x846CA68Bu;
                h ^= h >> 16;
                return h;
            }
        }
    }
}
