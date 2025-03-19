using UnityEngine;

namespace PicaVoxel
{
    public class TerrainGenerator : I_VoxelDataGenerator
    {
        private const int HEIGHT_MAX = 8;
        private const int HEIGHT_MIN = 0;

        private int _seed;
        public int Seed
        {
            get => _seed;
            set => _seed = value;
        }
        
        private PerlinNoiseTerrain _noise;

        public void GenerateVoxel(int x, int y, int z, ref Voxel voxel)
        {
            if(_noise == null)
                _noise = new PerlinNoiseTerrain(seed: Seed);
            
            voxel.Color = Color.white;
            float val = _noise.GetTerrainHeight(x, z);
            if(Random.Range(0,100)==0)
                Debug.Log(val);
            val *= (HEIGHT_MAX - HEIGHT_MIN);
            //Debug.Log(val);
            voxel.Active = HEIGHT_MIN+y < val;
            voxel.Value = 0;
        }
    }

    
    public class PerlinNoiseTerrain
    {
        private int[] permutation;
        private float scale;  // Controls the "zoom" level of the terrain

        // Constructor to initialize the terrain generator
        public PerlinNoiseTerrain(int seed = 0, float scale = 0.9f)
        {
            this.scale = scale;
            permutation = new int[512];

            System.Random rand = new System.Random(seed);
            
            // Generate a permutation table for the Perlin noise function
            for (int i = 0; i < 256; i++)
            {
                permutation[i] = i;
            }

            // Shuffle the permutation array
            for (int i = 255; i > 0; i--)
            {
                int j = rand.Next(i + 1);
                int temp = permutation[i];
                permutation[i] = permutation[j];
                permutation[j] = temp;
            }

            // Duplicate the array to handle indices beyond 255
            for (int i = 0; i < 256; i++)
            {
                permutation[256 + i] = permutation[i];
            }
        }

        // Function to generate a Perlin noise value at the given (x, z) coordinates
        public float Generate(float x, float z)
        {
            // Scale the coordinates based on the given scale factor
            x /= scale;
            z /= scale;

            // Find the integer coordinates of the grid cell
            int X = (int)Mathf.Floor(x) & 255;
            int Z = (int)Mathf.Floor(z) & 255;

            // Find the fractional part of the coordinates
            float xf = x - Mathf.Floor(x);
            float zf = z - Mathf.Floor(z);

            // Fade curves for smooth interpolation
            float u = Fade(xf);
            float v = Fade(zf);

            // Hash the corner values using the permutation table
            int a = permutation[X] + Z;
            int b = permutation[X + 1] + Z;

            // Interpolate the results
            float aa = Grad(permutation[a], xf, zf);
            float ba = Grad(permutation[b], xf - 1.0f, zf);
            float ab = Grad(permutation[a + 1], xf, zf - 1.0f);
            float bb = Grad(permutation[b + 1], xf - 1.0f, zf - 1.0f);

            float x1 = Lerp(aa, ba, u);
            float x2 = Lerp(ab, bb, u);

            return Lerp(x1, x2, v);
        }
        
        private float Fade(float t)
        {
            return t * t * t * (t * (t * 6 - 15) + 10);
        }

        // Linear interpolation function
        private float Lerp(float a, float b, float t)
        {
            return a + t * (b - a);
        }

        // Gradient function to compute the dot product of a pseudo-random gradient vector
        private float Grad(int hash, float x, float z)
        {
            // Ensure the gradient is normalized to a reasonable range [-1, 1]
            int h = hash & 15; // Get the lower 4 bits of the hash value

            // Gradients: simple 2D vectors mapped to values from -1 to 1
            float u = h < 8 ? x : z;   // x for h < 8, z for h >= 8
            float v = h < 4 ? z : (h == 12 || h == 14 ? x : 0); // Different behavior based on h

            // Dot product gives a gradient value in the range of [-1, 1]
            return  ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }

        // Function to generate a terrain height (y) value between 0 and 1
        public float GetTerrainHeight(int x, int z)
        {
            int octaves = 4;
            float persistence = 0.5f;
            float total = 0;
            float frequency = 1;
            float amplitude = 1;
            float maxValue = 0;

            for(int i = 0; i < octaves; i++) {
                total += Generate(x * frequency, z * frequency) * amplitude;
                amplitude *= persistence;
                frequency *= 2;
                maxValue += amplitude;
            }
            
            float sub = total / maxValue;
            
            return (sub + 1) / 2f;
            //float perlinValue = Generate(x, z);
            
            // Map the Perlin value (which is between -1 and 1) to the range 0-1
            //perlinValue = (perlinValue + 1) / 2f;  // Now within the range [0, 1]
            //return perlinValue;
        }
    }
}