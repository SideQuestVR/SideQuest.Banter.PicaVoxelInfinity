using System.Collections.Generic;
using UnityEngine;

namespace PicaVoxel
{
    public class TreeProcessor : MonoBehaviour, I_ChunkProcessor
    {
        [SerializeField]
        private ProcessingSchedule _schedule;
        [SerializeField]
        private int _order;

        public ProcessingSchedule Schedule
        {
            get => _schedule;
            set => _schedule = value;
        }

        public int Order
        {
            get => _order;
            set => _order = value;
        }

        public int TreeTrunkValue = 3;
        public int TreeLeavesValue = 12;

        public bool ProcessChunk(Volume volume, Chunk chunk)
        {
            List<(int x, int y, int z)> trees = new List<(int x, int y, int z)>();

            int cs = volume.ChunkSize;
            Voxel above = default;

            for (int z = 0; z < volume.ChunkSize; z++)
                for (int x = 0; x < volume.ChunkSize; x++)
                    for (int y = 0; y < volume.ChunkSize; y++)
                    {
                        if (chunk.Voxels[x + volume.ChunkSize * (y + volume.ChunkSize * z)].Value == (byte)TreeTrunkValue)
                        {
                            if (y == volume.ChunkSize-1)
                            {
                                // The voxel above is in the chunk overhead, which may not have generated yet (it read as
                                // air, growing a crown part way up the trunk), so ask the generator what's there
                                above = default;
                                volume.GenerateVoxel(x + cs * chunk.Position.x, cs * (chunk.Position.y + 1),
                                    z + cs * chunk.Position.z, ref above);
                                if (above.State == 0)
                                {
                                    trees.Add((x, y, z));
                                }
                            }
                            else if (chunk.Voxels[x + volume.ChunkSize * ((y + 1) + volume.ChunkSize * z)].State == 0)
                            {
                                trees.Add((x, y, z));
                            }
                        }
                    }

            if (trees.Count == 0)
                return true;
            
            foreach ((int x, int y, int z) tree in trees)
            {
                int x = tree.x;
                int y = tree.y;
                int z = tree.z;

                int nx = 1;
                int ny = 1;
                int nz = 1;
                
                for (int yy = 0; yy >=-4; yy--)
                {
                    y = tree.y + yy;
                    if (y < 0)
                    {
                        ny = 0;
                        y = volume.ChunkSize + y;
                    }

                    for (int zz = -2; zz <= 2; zz++)
                    {
                        z = tree.z + zz;
                        if (z < 0)
                        {
                            nz = 0;
                            z = volume.ChunkSize + z;
                        }
                        else if (z > volume.ChunkSize - 1)
                        {
                            nz = 2;
                            z -= volume.ChunkSize;
                        }

                        for (int xx = -2; xx <= 2; xx++)
                        {
                            x = tree.x + xx;
                            if (x < 0)
                            {
                                nx = 0;
                                x = volume.ChunkSize + x;
                            }
                            else if (x> volume.ChunkSize - 1)
                            {
                                nx = 2;
                                x -= volume.ChunkSize;
                            }

                            if (yy<0 && xx == 0 && zz == 0)
                            {
                                nx = 1;
                                continue;
                            }
                            
                            if (!(xx==0 && yy==0) && (xx+yy+zz)%2==0)
                            {
                                nx = 1;
                                continue;
                            }

                            int index = x + volume.ChunkSize * (y + volume.ChunkSize * z);
                            if (nx == 1 && ny == 1 && nz == 1)
                            {
                                chunk.Voxels[index].State = 2; // 2=Custom block/transparent
                                chunk.Voxels[index].Value = (byte)TreeLeavesValue;
                            }
                            else
                            {
                                // A neighbour may not be generated (or even created) yet; the volume holds the write
                                // until it is, where writing straight into its voxels lost the leaves to its generator
                                volume.WriteVoxelFromNeighbour(
                                    (chunk.Position.x + nx - 1, chunk.Position.y + ny - 1, chunk.Position.z + nz - 1),
                                    index, 2, (byte)TreeLeavesValue);
                            }

                            nx = 1;
                        }
                        nz = 1;
                    }
                    ny = 1;

                }
            }

            return true;
        }
    }
}