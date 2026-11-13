/////////////////////////////////////////////////////////////////////////
//
// PicaVoxel - The tiny voxel engine for Unity - http://picavoxel.com
// By Gareth Williams - @garethiw - http://gareth.pw
//
/////////////////////////////////////////////////////////////////////////

using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace PicaVoxel
{
    /// <summary>
    /// One vertex of a chunk's render mesh: the layout <see cref="Layout"/> describes, in the attribute order Unity
    /// requires (position, normal, color, uv0).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ChunkVertex
    {
        public float3 Position;
        public float3 Normal;
        public Color32 Color;
        // xy: the face's 0..1 coordinates (or a custom block's own UVs), z: voxel value, w: side (0-5, -1 custom mesh)
        public float4 UV;

        public static readonly VertexAttributeDescriptor[] Layout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 4),
        };

        public static readonly VertexAttributeDescriptor[] CollisionLayout =
        {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
        };
    }

    /// <summary>
    /// The volume's custom blocks as flat native tables, indexed by voxel value, for the meshing job.
    /// </summary>
    public struct CustomBlockTables
    {
        public const byte IsCustom = 1;
        public const byte HasMesh = 2;
        public const byte Transparent = 4;
        public const byte AllowOrientation = 8;
        /// <summary>No faces against a neighbour of the same value (water)</summary>
        public const byte HideAgainstSame = 16;
        /// <summary>Left out of the collision mesh</summary>
        public const byte NoCollision = 32;

        /// <summary>Per voxel value (256): the flags above.</summary>
        [ReadOnly] public NativeArray<byte> Flags;
        /// <summary>Per voxel value: first vertex, vertex count, first index, index count in the arrays below.</summary>
        [ReadOnly] public NativeArray<int4> MeshRanges;
        [ReadOnly] public NativeArray<float3> Vertices;
        [ReadOnly] public NativeArray<float3> Normals;
        [ReadOnly] public NativeArray<float2> UVs;
        [ReadOnly] public NativeArray<int> Indices;

        public bool IsCreated => Flags.IsCreated;

        public void Dispose()
        {
            if (Flags.IsCreated) Flags.Dispose();
            if (MeshRanges.IsCreated) MeshRanges.Dispose();
            if (Vertices.IsCreated) Vertices.Dispose();
            if (Normals.IsCreated) Normals.Dispose();
            if (UVs.IsCreated) UVs.Dispose();
            if (Indices.IsCreated) Indices.Dispose();
        }
    }

    /// <summary>
    /// Builds a chunk's Culled render mesh, and a collision mesh with the same surfaces, off the main thread.
    /// </summary>
    /// <remarks>
    /// The render mesh matches <see cref="MeshGenerator.GenerateCulled"/> exactly: the same faces in the same order,
    /// one quad per voxel face (so textures tile per block), the same per-corner self-shading and diagonal flip, the
    /// same UV encoding, custom block meshes and two-sided transparent cubes. Normals are written here rather than
    /// recalculated on the main thread.
    ///
    /// The collision mesh is the same set of faces with neighbouring coplanar faces merged into larger quads (and
    /// custom block meshes copied as they are). Raycasts against it hit the same points with the same normals, while
    /// PhysX has far fewer triangles to cook and to test.
    ///
    /// The chunk and its 26 neighbours are read through pinned pointers to their voxel arrays and copied into a padded
    /// block first, so the meshing loops never leave one array.
    /// </remarks>
    [BurstCompile(CompileSynchronously = true)]
    public unsafe struct CulledMeshJob : IJob
    {
        public int ChunkSize;
        public float VoxelSize;
        public float SelfShadeIntensity;
        public bool BuildCollision;
        /// <summary>Merge coplanar collision faces (needs ChunkSize <= 32; otherwise each face is its own quad).</summary>
        public bool MergeCollision;

        /// <summary>27 pointers to the voxel arrays of the chunk (index 13) and its neighbours, 0 where none.</summary>
        [ReadOnly] public NativeArray<long> Neighbours;
        public CustomBlockTables Blocks;

        /// <summary>Scratch: (ChunkSize + 2)^3 voxels.</summary>
        public NativeArray<Voxel> Padded;
        /// <summary>Scratch: 12 * ChunkSize * ChunkSize rows of face bits for merging collision faces.</summary>
        public NativeArray<uint> FaceMasks;

        public NativeList<ChunkVertex> Vertices;
        public NativeList<int> Indices;
        /// <summary>The same indices as 16 bit, filled when the mesh has 65535 vertices or fewer (else left empty).</summary>
        public NativeList<ushort> Indices16;
        public NativeList<float3> CollisionVertices;
        public NativeList<int> CollisionIndices;
        /// <summary>[0] min, [1] max of the render vertices.</summary>
        public NativeArray<float3> Bounds;

        int _stride;

        public void Execute()
        {
            Vertices.Clear();
            Indices.Clear();
            Indices16.Clear();
            CollisionVertices.Clear();
            CollisionIndices.Clear();

            _stride = ChunkSize + 2;
            FillPadded();

            bool mergeCollision = BuildCollision && MergeCollision && ChunkSize <= 32;
            if (mergeCollision)
                UnsafeUtility.MemClear(FaceMasks.GetUnsafePtr(), (long)FaceMasks.Length * sizeof(uint));

            float3 min = new float3(float.MaxValue);
            float3 max = new float3(float.MinValue);

            bool skipPass = true;
            int cs = ChunkSize;
            for (int pass = 0; pass < 2; pass++)
            {
                for (int z = 0; z < cs; z++)
                for (int y = 0; y < cs; y++)
                for (int x = 0; x < cs; x++)
                {
                    Voxel v = Padded[Index(x, y, z)];
                    if (v.State == 0) continue;

                    bool tpCube = false;
                    byte flags = Blocks.Flags[v.Value];
                    if ((flags & CustomBlockTables.IsCustom) != 0)
                    {
                        bool transparent = (flags & CustomBlockTables.Transparent) != 0;
                        if (transparent && pass == 0)
                        {
                            skipPass = false;
                            continue;
                        }
                        if (!transparent && pass == 1)
                            continue;
                        if ((flags & CustomBlockTables.HasMesh) != 0)
                        {
                            CustomMesh(x, y, z, v, flags, ref min, ref max, mergeCollision);
                            continue;
                        }
                        tpCube = true;
                    }

                    if (pass == 1 && !tpCube)
                        continue;

                    if (tpCube)
                    {
                        bool hideSame = (flags & CustomBlockTables.HideAgainstSame) != 0;
                        // The inside of a transparent cube: each visible face again, wound the other way
                        for (int f = 0; f < 6; f++)
                            if (FaceVisible(x, y, z, f) && !(hideSame && SameNeighbour(x, y, z, f, v.Value)))
                                Quad(x, y, z, f, v, !FrontWinding(f), true, ref min, ref max, mergeCollision);
                        for (int f = 0; f < 6; f++)
                            if (FaceVisible(x, y, z, f) && !(hideSame && SameNeighbour(x, y, z, f, v.Value)))
                                Quad(x, y, z, f, v, FrontWinding(f), false, ref min, ref max, mergeCollision);
                        continue;
                    }

                    for (int f = 0; f < 6; f++)
                        if (FaceVisible(x, y, z, f))
                            Quad(x, y, z, f, v, FrontWinding(f), false, ref min, ref max, mergeCollision);
                }

                if (skipPass)
                    break;
            }

            if (Vertices.Length == 0)
            {
                min = float3.zero;
                max = float3.zero;
            }
            Bounds[0] = min;
            Bounds[1] = max;

            if (mergeCollision)
                EmitMergedCollision();

            if (Vertices.Length <= 65535)
            {
                Indices16.ResizeUninitialized(Indices.Length);
                for (int i = 0; i < Indices.Length; i++)
                    Indices16[i] = (ushort)Indices[i];
            }
        }

        int Index(int x, int y, int z) => (x + 1) + _stride * ((y + 1) + _stride * (z + 1));

        // Copies the chunk and the one-voxel border around it from the 27 arrays; missing neighbours read as empty.
        void FillPadded()
        {
            int cs = ChunkSize;
            int voxelSize = UnsafeUtility.SizeOf<Voxel>();
            Voxel* dst = (Voxel*)Padded.GetUnsafePtr();

            for (int nz = 0; nz < 3; nz++)
            for (int ny = 0; ny < 3; ny++)
            for (int nx = 0; nx < 3; nx++)
            {
                Voxel* src = (Voxel*)Neighbours[nx + 3 * (ny + 3 * nz)];
                Range(nx, cs, out int x0, out int x1, out int sx);
                Range(ny, cs, out int y0, out int y1, out int sy);
                Range(nz, cs, out int z0, out int z1, out int sz);

                for (int z = z0; z <= z1; z++)
                for (int y = y0; y <= y1; y++)
                {
                    int srcZ = sz < 0 ? z : sz;
                    int srcY = sy < 0 ? y : sy;
                    Voxel* rowDst = dst + Index(x0, y, z);
                    int count = x1 - x0 + 1;
                    if (src == null)
                    {
                        UnsafeUtility.MemClear(rowDst, (long)count * voxelSize);
                        continue;
                    }
                    int srcX = sx < 0 ? x0 : sx;
                    UnsafeUtility.MemCpy(rowDst, src + (srcX + cs * (srcY + cs * srcZ)), (long)count * voxelSize);
                }
            }
        }

        // For neighbour slot n (0 below, 1 this chunk, 2 above): the padded range it fills, and the fixed source
        // coordinate it reads (-1 when the source coordinate equals the padded one).
        static void Range(int n, int cs, out int lo, out int hi, out int src)
        {
            if (n == 0) { lo = -1; hi = -1; src = cs - 1; }
            else if (n == 1) { lo = 0; hi = cs - 1; src = -1; }
            else { lo = cs; hi = cs; src = 0; }
        }

        // A neighbour that hides a face: a full cube, not a transparent or meshed custom block.
        bool Occludes(int x, int y, int z)
        {
            Voxel n = Padded[Index(x, y, z)];
            if (n.State != 1) return false;
            byte flags = Blocks.Flags[n.Value];
            if ((flags & CustomBlockTables.IsCustom) != 0 &&
                (flags & (CustomBlockTables.Transparent | CustomBlockTables.HasMesh)) != 0)
                return false;
            return true;
        }

        // A neighbour that shades a corner: any voxel in the "active" state, custom or not.
        bool Shades(int x, int y, int z) => Padded[Index(x, y, z)].State == 1;

        // The neighbour across a face is the same block (for blocks that hide faces against their own kind)
        bool SameNeighbour(int x, int y, int z, int side, byte value)
        {
            Voxel n;
            switch (side)
            {
                case 0: n = Padded[Index(x, y, z - 1)]; break;
                case 1: n = Padded[Index(x, y, z + 1)]; break;
                case 2: n = Padded[Index(x + 1, y, z)]; break;
                case 3: n = Padded[Index(x - 1, y, z)]; break;
                case 4: n = Padded[Index(x, y + 1, z)]; break;
                default: n = Padded[Index(x, y - 1, z)]; break;
            }
            return n.State == 1 && n.Value == value;
        }

        bool FaceVisible(int x, int y, int z, int side)
        {
            switch (side)
            {
                case 0: return !Occludes(x, y, z - 1);
                case 1: return !Occludes(x, y, z + 1);
                case 2: return !Occludes(x + 1, y, z);
                case 3: return !Occludes(x - 1, y, z);
                case 4: return !Occludes(x, y + 1, z);
                default: return !Occludes(x, y - 1, z);
            }
        }

        // The winding GenerateCulled gives each side's outward face.
        static bool FrontWinding(int side) => (side & 1) == 1;

        const int TopLeft = 1, TopRight = 2, BottomRight = 4, BottomLeft = 8;

        int VertShade(int x, int y, int z, int side)
        {
            if (SelfShadeIntensity <= 0f) return 0;
            int s = 0;
            switch (side)
            {
                case 0:
                case 1:
                {
                    int dz = side == 0 ? -1 : 1;
                    if (Shades(x - 1, y, z + dz) || Shades(x - 1, y - 1, z + dz) || Shades(x, y - 1, z + dz)) s |= TopLeft;
                    if (Shades(x + 1, y, z + dz) || Shades(x + 1, y - 1, z + dz) || Shades(x, y - 1, z + dz)) s |= BottomLeft;
                    if (Shades(x - 1, y, z + dz) || Shades(x - 1, y + 1, z + dz) || Shades(x, y + 1, z + dz)) s |= TopRight;
                    if (Shades(x + 1, y, z + dz) || Shades(x + 1, y + 1, z + dz) || Shades(x, y + 1, z + dz)) s |= BottomRight;
                    break;
                }
                case 2:
                case 3:
                {
                    int dx = side == 3 ? -1 : 1;
                    if (Shades(x + dx, y, z - 1) || Shades(x + dx, y - 1, z - 1) || Shades(x + dx, y - 1, z)) s |= TopLeft;
                    if (Shades(x + dx, y, z + 1) || Shades(x + dx, y - 1, z + 1) || Shades(x + dx, y - 1, z)) s |= BottomLeft;
                    if (Shades(x + dx, y, z - 1) || Shades(x + dx, y + 1, z - 1) || Shades(x + dx, y + 1, z)) s |= TopRight;
                    if (Shades(x + dx, y, z + 1) || Shades(x + dx, y + 1, z + 1) || Shades(x + dx, y + 1, z)) s |= BottomRight;
                    break;
                }
                default:
                {
                    int dy = side == 5 ? -1 : 1;
                    if (Shades(x, y + dy, z - 1) || Shades(x - 1, y + dy, z - 1) || Shades(x - 1, y + dy, z)) s |= TopLeft;
                    if (Shades(x, y + dy, z + 1) || Shades(x - 1, y + dy, z + 1) || Shades(x - 1, y + dy, z)) s |= TopRight;
                    if (Shades(x, y + dy, z - 1) || Shades(x + 1, y + dy, z - 1) || Shades(x + 1, y + dy, z)) s |= BottomLeft;
                    if (Shades(x, y + dy, z + 1) || Shades(x + 1, y + dy, z + 1) || Shades(x + 1, y + dy, z)) s |= BottomRight;
                    break;
                }
            }
            return s;
        }

        static void Corners(int side, out int3 tl, out int3 tr, out int3 br, out int3 bl)
        {
            switch (side)
            {
                case 0: tl = new int3(0, 1, 0); tr = new int3(1, 1, 0); br = new int3(1, 0, 0); bl = new int3(0, 0, 0); break;
                case 1: tl = new int3(0, 1, 1); tr = new int3(1, 1, 1); br = new int3(1, 0, 1); bl = new int3(0, 0, 1); break;
                case 2: tl = new int3(1, 1, 0); tr = new int3(1, 1, 1); br = new int3(1, 0, 1); bl = new int3(1, 0, 0); break;
                case 3: tl = new int3(0, 1, 0); tr = new int3(0, 1, 1); br = new int3(0, 0, 1); bl = new int3(0, 0, 0); break;
                case 4: tl = new int3(0, 1, 1); tr = new int3(1, 1, 1); br = new int3(1, 1, 0); bl = new int3(0, 1, 0); break;
                default: tl = new int3(0, 0, 1); tr = new int3(1, 0, 1); br = new int3(1, 0, 0); bl = new int3(0, 0, 0); break;
            }
        }

        // Color * (shaded ? 1 - intensity : 1), as GenerateCulled computes it through Color and back to Color32.
        Color32 Shade(Color32 c, bool shaded)
        {
            if (!shaded) return c;
            float k = 1f - SelfShadeIntensity;
            return new Color32(Channel(c.r, k), Channel(c.g, k), Channel(c.b, k), c.a);
        }

        static byte Channel(byte c, float k)
        {
            float f = math.saturate((c / 255f) * k) * 255f;
            return (byte)RoundHalfEven(f);
        }

        // Mathf.Round rounds halves to even
        static float RoundHalfEven(float f)
        {
            float r = math.floor(f);
            float diff = f - r;
            if (diff > 0.5f) return r + 1f;
            if (diff < 0.5f) return r;
            return math.fmod(r, 2f) == 0f ? r : r + 1f;
        }

        void Quad(int x, int y, int z, int side, Voxel v, bool backFace, bool insideFace, ref float3 min, ref float3 max,
            bool mergeCollision)
        {
            Corners(side, out int3 tl, out int3 tr, out int3 br, out int3 bl);
            int3 o = new int3(x, y, z);
            float3 p0 = (float3)(o + bl) * VoxelSize;
            float3 p1 = (float3)(o + br) * VoxelSize;
            float3 p2 = (float3)(o + tl) * VoxelSize;
            float3 p3 = (float3)(o + tr) * VoxelSize;

            int shade = VertShade(x, y, z, side);
            int ntr = (shade & TopRight) != 0 ? 1 : 0;
            int nbl = (shade & BottomLeft) != 0 ? 1 : 0;
            int ntl = (shade & TopLeft) != 0 ? 1 : 0;
            int nbr = (shade & BottomRight) != 0 ? 1 : 0;
            int test1 = ntr + nbl + ntl + nbr;
            int test2 = ntr + nbl;
            int test3 = ntl + nbr;
            bool flip = (test2 > test3 && test1 > 2) || (test2 <= test3 && test1 == 1);

            int i0, i1, i2, i3, i4, i5;
            if (flip)
            {
                if (backFace) { i0 = 2; i1 = 0; i2 = 1; i3 = 1; i4 = 3; i5 = 2; }
                else { i0 = 2; i1 = 3; i2 = 1; i3 = 1; i4 = 0; i5 = 2; }
            }
            else
            {
                if (backFace) { i0 = 3; i1 = 2; i2 = 0; i3 = 0; i4 = 1; i5 = 3; }
                else { i0 = 3; i1 = 1; i2 = 0; i3 = 0; i4 = 2; i5 = 3; }
            }

            float3 a = Corner(i0, p0, p1, p2, p3);
            float3 b = Corner(i1, p0, p1, p2, p3);
            float3 c = Corner(i2, p0, p1, p2, p3);
            float3 normal = math.normalizesafe(math.cross(b - a, c - a));

            int start = Vertices.Length;
            float value = v.Value;
            Vertices.Add(new ChunkVertex { Position = p0, Normal = normal, Color = Shade(v.Color, ntl != 0), UV = new float4(0f, 0f, value, side) });
            Vertices.Add(new ChunkVertex { Position = p1, Normal = normal, Color = Shade(v.Color, nbl != 0), UV = new float4(1f, 0f, value, side) });
            Vertices.Add(new ChunkVertex { Position = p2, Normal = normal, Color = Shade(v.Color, ntr != 0), UV = new float4(0f, 1f, value, side) });
            Vertices.Add(new ChunkVertex { Position = p3, Normal = normal, Color = Shade(v.Color, nbr != 0), UV = new float4(1f, 1f, value, side) });

            Indices.Add(start + i0);
            Indices.Add(start + i1);
            Indices.Add(start + i2);
            Indices.Add(start + i3);
            Indices.Add(start + i4);
            Indices.Add(start + i5);

            min = math.min(min, math.min(math.min(p0, p1), math.min(p2, p3)));
            max = math.max(max, math.max(math.max(p0, p1), math.max(p2, p3)));

            if (!BuildCollision || (Blocks.Flags[v.Value] & CustomBlockTables.NoCollision) != 0) return;
            if (mergeCollision)
            {
                AxisCoords(side, x, y, z, out int plane, out int u, out int row);
                FaceMasks[MaskIndex(side, insideFace, plane, row)] |= 1u << u;
            }
            else
            {
                int cstart = CollisionVertices.Length;
                CollisionVertices.Add(p0);
                CollisionVertices.Add(p1);
                CollisionVertices.Add(p2);
                CollisionVertices.Add(p3);
                CollisionIndices.Add(cstart + i0);
                CollisionIndices.Add(cstart + i1);
                CollisionIndices.Add(cstart + i2);
                CollisionIndices.Add(cstart + i3);
                CollisionIndices.Add(cstart + i4);
                CollisionIndices.Add(cstart + i5);
            }
        }

        static float3 Corner(int i, float3 p0, float3 p1, float3 p2, float3 p3)
        {
            switch (i)
            {
                case 0: return p0;
                case 1: return p1;
                case 2: return p2;
                default: return p3;
            }
        }

        void CustomMesh(int x, int y, int z, Voxel v, byte flags, ref float3 min, ref float3 max, bool mergeCollision)
        {
            int4 range = Blocks.MeshRanges[v.Value];
            quaternion rot = quaternion.identity;
            if ((flags & CustomBlockTables.AllowOrientation) != 0)
                rot = quaternion.RotateY(math.radians(90f * (v.State - 2)));

            float3 origin = new float3(x, y, z) * VoxelSize + new float3(VoxelSize * 0.5f);
            int start = Vertices.Length;
            int cstart = CollisionVertices.Length;
            float value = v.Value;
            Color32 white = new Color32(255, 255, 255, 255);
            for (int i = 0; i < range.y; i++)
            {
                float3 p = origin + math.rotate(rot, Blocks.Vertices[range.x + i]);
                float2 uv = Blocks.UVs[range.x + i];
                Vertices.Add(new ChunkVertex
                {
                    Position = p,
                    Normal = math.rotate(rot, Blocks.Normals[range.x + i]),
                    Color = white,
                    UV = new float4(uv.x, uv.y, value, -1f),
                });
                min = math.min(min, p);
                max = math.max(max, p);
                if (BuildCollision && (flags & CustomBlockTables.NoCollision) == 0)
                    CollisionVertices.Add(p);
            }
            for (int i = 0; i < range.w; i++)
            {
                int tri = Blocks.Indices[range.z + i];
                Indices.Add(start + tri);
                if (BuildCollision && (flags & CustomBlockTables.NoCollision) == 0)
                    CollisionIndices.Add(cstart + tri);
            }
        }

        // Collision face bookkeeping: per side, the axis the face is on (plane), and its coordinates on that plane.
        static void AxisCoords(int side, int x, int y, int z, out int plane, out int u, out int row)
        {
            if (side <= 1) { plane = z; u = x; row = y; }
            else if (side <= 3) { plane = x; u = z; row = y; }
            else { plane = y; u = x; row = z; }
        }

        int MaskIndex(int side, bool inside, int plane, int row) =>
            ((side * 2 + (inside ? 1 : 0)) * ChunkSize + plane) * ChunkSize + row;

        // Merges each plane's faces into rectangles (greedy, row runs grown downwards) and emits two triangles each,
        // wound to face the same way as the faces they replace.
        void EmitMergedCollision()
        {
            int cs = ChunkSize;
            for (int side = 0; side < 6; side++)
            for (int inside = 0; inside < 2; inside++)
            {
                // Outward faces point away from the voxel; the inside faces of transparent cubes point back in
                float3 normal = Outward(side) * (inside == 1 ? -1f : 1f);
                for (int plane = 0; plane < cs; plane++)
                {
                    int baseIndex = MaskIndex(side, inside == 1, plane, 0);
                    for (int row = 0; row < cs; row++)
                    {
                        uint bits = FaceMasks[baseIndex + row];
                        while (bits != 0)
                        {
                            int u0 = math.tzcnt(bits);
                            int w = math.tzcnt(~(bits >> u0));
                            uint run = (w >= 32 ? 0xFFFFFFFFu : ((1u << w) - 1u)) << u0;
                            int h = 1;
                            while (row + h < cs && (FaceMasks[baseIndex + row + h] & run) == run)
                            {
                                FaceMasks[baseIndex + row + h] &= ~run;
                                h++;
                            }
                            bits &= ~run;
                            MergedQuad(side, plane, u0, u0 + w, row, row + h, normal);
                        }
                    }
                }
            }
        }

        static float3 Outward(int side)
        {
            switch (side)
            {
                case 0: return new float3(0, 0, -1);
                case 1: return new float3(0, 0, 1);
                case 2: return new float3(1, 0, 0);
                case 3: return new float3(-1, 0, 0);
                case 4: return new float3(0, 1, 0);
                default: return new float3(0, -1, 0);
            }
        }

        float3 Point(int side, int planeCoord, int u, int row)
        {
            if (side <= 1) return new float3(u, row, planeCoord) * VoxelSize;
            if (side <= 3) return new float3(planeCoord, row, u) * VoxelSize;
            return new float3(u, planeCoord, row) * VoxelSize;
        }

        void MergedQuad(int side, int plane, int u0, int u1, int r0, int r1, float3 normal)
        {
            // Faces on the positive side of a voxel lie on its far plane
            int planeCoord = plane + (side == 1 || side == 2 || side == 4 ? 1 : 0);
            float3 a = Point(side, planeCoord, u0, r0);
            float3 b = Point(side, planeCoord, u1, r0);
            float3 c = Point(side, planeCoord, u0, r1);
            float3 d = Point(side, planeCoord, u1, r1);

            int start = CollisionVertices.Length;
            CollisionVertices.Add(a);
            CollisionVertices.Add(b);
            CollisionVertices.Add(c);
            CollisionVertices.Add(d);

            // (a, b, c) faces along cross(b - a, c - a); keep it if that is the way the faces looked, else reverse
            if (math.dot(math.cross(b - a, c - a), normal) > 0f)
            {
                CollisionIndices.Add(start);
                CollisionIndices.Add(start + 1);
                CollisionIndices.Add(start + 2);
                CollisionIndices.Add(start + 1);
                CollisionIndices.Add(start + 3);
                CollisionIndices.Add(start + 2);
            }
            else
            {
                CollisionIndices.Add(start);
                CollisionIndices.Add(start + 2);
                CollisionIndices.Add(start + 1);
                CollisionIndices.Add(start + 1);
                CollisionIndices.Add(start + 2);
                CollisionIndices.Add(start + 3);
            }
        }
    }
}
