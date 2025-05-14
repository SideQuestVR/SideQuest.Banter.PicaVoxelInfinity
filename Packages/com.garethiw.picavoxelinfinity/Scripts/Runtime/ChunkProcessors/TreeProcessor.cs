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

            Chunk upchunk = volume.GetChunk((chunk.Position.x, chunk.Position.y + 1, chunk.Position.z));

            for (int z = 0; z < volume.ChunkSize; z++)
                for (int x = 0; x < volume.ChunkSize; x++)
                    for (int y = 0; y < volume.ChunkSize; y++)
                    {
                        if (chunk.Voxels[x + volume.ChunkSize * (y + volume.ChunkSize * z)].Value == (byte)TreeTrunkValue)
                        {
                            if (y == volume.ChunkSize-1)
                            {
                                if (upchunk != null)
                                {
                                    if (upchunk.Voxels[x + volume.ChunkSize * (0 + volume.ChunkSize * z)].State == 0)
                                    {
                                        trees.Add((x, y, z));
                                    }
                                }
                                else
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
            
            Chunk[] _neighbours = new Chunk[27];
            for(int x=0;x<3;x++)
                for(int y=0;y<3;y++)
                    for(int z=0;z<3;z++)
                        _neighbours[x + 3 * (y + 3 * z)] = volume.GetChunk((chunk.Position.x+(x-1),chunk.Position.y+(y-1),chunk.Position.z+(z-1)));

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

                            if (_neighbours[nx + 3 * (ny + 3 * nz)] == null)
                            {
                                nx = 1;
                                continue;
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

                            _neighbours[nx + 3 * (ny + 3 * nz)].Voxels[x + volume.ChunkSize * (y + volume.ChunkSize * z)].State = 2; // 2=Custom block/transparent
                            _neighbours[nx + 3 * (ny + 3 * nz)].Voxels[x + volume.ChunkSize * (y + volume.ChunkSize * z)].Value = (byte)TreeLeavesValue;

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